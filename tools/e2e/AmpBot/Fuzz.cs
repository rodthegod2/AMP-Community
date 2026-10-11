using AMP.ModSync;
using Netamite.Client.Implementation;
using Netamite.Datatype;
using Netamite.Network.Packet;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;

namespace AmpBot {
    // --join ... --fuzz: a hostile joiner. Sends malformed, oversized, out-of-order and duplicate MOD_SYNC / MOD_FILE and gameplay
    // packets plus raw garbage frames to the game host, reconnecting whenever the host drops it (a kick is fine). Afterwards a normal
    // joiner must still get in: the host didn't crash or hang. What the host wrote to disk is checked by the runner.
    public sealed class Fuzzer {
        private readonly string address, modsFolder, cacheFolder;
        private readonly int port;
        private readonly Expectations x;
        private readonly Random random = new Random(20261011);
        private IPClient client;
        private volatile bool hostListSeen, dropped;
        private string lastDrop = "";
        private int drops, sent;

        public Fuzzer(string address, int port, string modsFolder, string cacheFolder, Expectations x) {
            this.address = address; this.port = port; this.modsFolder = modsFolder; this.cacheFolder = cacheFolder; this.x = x;
        }

        public bool Run(Checklist list) {
            var batteries = new List<KeyValuePair<string, Action>> {
                Battery("joiner reports: bad part counts, broken JSON, wrong types, oversized", Reports),
                Battery("joiner reports: out-of-order and duplicate parts", ReportParts),
                Battery("mod requests: before setup, path tricks, AMP itself, too many parts, too soon, unknown folders", Requests),
                Battery("transfer control: acks, READY_NEXT and cancels for transfers that don't exist", TransferControl),
                Battery("MOD_FILE from a joiner, unknown MOD_SYNC kinds", Files),
                Battery("gameplay before the greeting: odd items, positions, names, levels", Gameplay),
                Battery("gameplay after the greeting", GreetThenGameplay),
                Battery("raw frames: unknown packet ids, truncated packets, garbage, maximum length", RawFrames),
            };
            foreach(KeyValuePair<string, Action> b in batteries) {
                if(!EnsureConnected()) { list.Fail("can't connect to the host any more (before: " + b.Key + "); last drop: " + lastDrop); return false; }
                Out.Info("FUZZ battery: " + b.Key);
                try { b.Value(); } catch(Exception e) { Out.Info("FUZZ battery threw on our side (not a host problem): " + e.GetType().Name + ": " + e.Message); }
                Thread.Sleep(1500);
                Out.Info("FUZZ after \"" + b.Key + "\": " + (client != null && client.IsConnected && !dropped ? "still connected" : "dropped by the host (" + lastDrop + ")"));
            }
            try { client?.Disconnect(); } catch(Exception) { }
            Out.Line("OK", "fuzz: " + sent + " hostile packets/frames sent, the host dropped the connection " + drops + " time(s)");
            return true;
        }

        private static KeyValuePair<string, Action> Battery(string name, Action a) { return new KeyValuePair<string, Action>(name, a); }

        private bool EnsureConnected() {
            if(client != null && client.IsConnected && !dropped) return true;
            for(int attempt = 0; attempt < 20; attempt++) {
                try { client?.Disconnect(); } catch(Exception) { }
                hostListSeen = false; dropped = false;
                var c = new IPClient(address, port) { ClientName = "AmpFuzz" };
                var answered = new ManualResetEvent(false);
                c.OnConnect += () => answered.Set();
                c.OnConnectionError += e => { lastDrop = "connection error: " + e; answered.Set(); };
                c.OnDisconnect += reason => { dropped = true; lastDrop = reason; Interlocked.Increment(ref drops); };
                c.OnDataReceived += p => { if(p is ModSyncPacket && ((ModSyncPacket) p).kind == ModSyncPacket.HOST_MOD_SET) hostListSeen = true; };
                client = c;
                c.Connect("");
                answered.WaitOne(TimeSpan.FromSeconds(15));
                if(c.IsConnected) {
                    DateTime until = DateTime.UtcNow.AddSeconds(15);
                    while(!hostListSeen && DateTime.UtcNow < until) Thread.Sleep(100);
                    return true;
                }
                Thread.Sleep(2000);
            }
            return false;
        }

        private void Send(NetPacket p) { sent++; try { client.SendReliable(p); } catch(Exception) { } Thread.Sleep(5); }
        private void Sync(byte kind, int id, int part, int parts, string data) { Send(new ModSyncPacket(kind, id, part, parts, data)); }
        private string Junk(int length) { var sb = new StringBuilder(length); for(int i = 0; i < length; i++) sb.Append((char) random.Next(0x20, 0x2FF)); return sb.ToString(); }
        private string Report(string status, string detail = "") { return JsonConvert.SerializeObject(new JoinerReport { platform = "windows", status = status, detail = detail }); }

        private void Reports() {
            int id = 50000;
            Sync(ModSyncPacket.JOINER_REPORT, id++, 0, 0, Report("ready"));
            Sync(ModSyncPacket.JOINER_REPORT, id++, 0, -1, Report("ready"));
            Sync(ModSyncPacket.JOINER_REPORT, id++, 0, 11, Report("ready"));
            Sync(ModSyncPacket.JOINER_REPORT, id++, 5, 3, Report("ready"));
            Sync(ModSyncPacket.JOINER_REPORT, id++, -1, 3, Report("ready"));
            Sync(ModSyncPacket.JOINER_REPORT, id++, 0, int.MaxValue, Report("ready"));
            Sync(ModSyncPacket.JOINER_REPORT, int.MinValue, 0, 1, null);
            Sync(ModSyncPacket.JOINER_REPORT, id++, 0, 1, "{{{");
            Sync(ModSyncPacket.JOINER_REPORT, id++, 0, 1, "[1,2,3]");
            Sync(ModSyncPacket.JOINER_REPORT, id++, 0, 1, "{\"status\":123,\"platform\":[1,2],\"protocol\":\"x\"}");
            Sync(ModSyncPacket.JOINER_REPORT, id++, 0, 1, "{\"status\":\"checking\",\"platform\":\"" + new string('a', 14000) + "\"}");
            Sync(ModSyncPacket.JOINER_REPORT, id++, 0, 1, "{\"status\":\"needs_setup\",\"detail\":null}");
            Sync(ModSyncPacket.JOINER_REPORT, id++, 0, 1, "{\"status\":\"declined\"}");
            Sync(ModSyncPacket.JOINER_REPORT, id++, 0, 1, Junk(15000));
            Sync(ModSyncPacket.JOINER_REPORT, id++, 0, 1, new string('\u0000', 1000));
            Sync(ModSyncPacket.JOINER_REPORT, id++, 0, 1, "{\"status\":\"" + Junk(20) + "\"}");
        }

        // A real "checking" report in 3 parts, out of order and with duplicates; then a 10-part "needs_setup" with a huge detail
        private void ReportParts() {
            string json = Report("checking", new string('d', 30000));
            List<ModSyncPacket> parts = ModSyncPacket.Create(ModSyncPacket.JOINER_REPORT, json);
            foreach(int i in new[] { 2, 0, 0, 1, 2, 1 }) if(i < parts.Count) Send(parts[i]);
            List<ModSyncPacket> big = ModSyncPacket.Create(ModSyncPacket.JOINER_REPORT, Report("needs_setup", new string('e', 140000)));
            foreach(ModSyncPacket p in big.AsEnumerable().Reverse()) Send(p);
            Sync(ModSyncPacket.JOINER_REPORT, big[0].transferId, 0, big.Count + 1, "x"); // same id, other part count
        }

        private void Requests() {
            string Req(params string[] folders) { return JsonConvert.SerializeObject(new ModsRequest { folders = folders.ToList() }); }
            int id = 60000;
            Sync(ModSyncPacket.MODS_REQUEST, id++, 0, 1, Req("AmpE2EDummy"));           // before saying needs_setup: must be ignored
            foreach(ModSyncPacket p in ModSyncPacket.Create(ModSyncPacket.JOINER_REPORT, Report("needs_setup", "fuzz"))) Send(p);
            Thread.Sleep(300);
            Sync(ModSyncPacket.MODS_REQUEST, id++, 0, 1, Req("..\\..\\..\\Windows\\win.ini", "../../../../Windows/System32", "C:\\Windows", "\\\\server\\share", "", null, "CON", "Mods\\..\\.."));
            Thread.Sleep(2100);
            Sync(ModSyncPacket.MODS_REQUEST, id++, 0, 1, Req("MultiplayerMod"));        // AMP itself
            Thread.Sleep(2100);
            Sync(ModSyncPacket.MODS_REQUEST, id++, 0, 1, Req(Enumerable.Range(0, 5000).Select(i => "nope" + i).ToArray()));
            Sync(ModSyncPacket.MODS_REQUEST, id++, 0, 1, "{\"folders\":\"notalist\"}");
            Sync(ModSyncPacket.MODS_REQUEST, id++, 0, 1, "{\"folders\":[1,{},[]]}");
            Sync(ModSyncPacket.MODS_REQUEST, id++, 0, 1, Junk(9000));
            for(int i = 0; i < 11; i++) Sync(ModSyncPacket.MODS_REQUEST, id, i, 11, "x"); // more parts than a request may have
            id++;
            Thread.Sleep(2100);
            // A real request, then the same again at once (too soon / already running), then cancel it
            Sync(ModSyncPacket.MODS_REQUEST, id++, 0, 1, Req("AmpE2EDummy", "AmpE2EDummy", "AmpE2EDummy"));
            Sync(ModSyncPacket.MODS_REQUEST, id++, 0, 1, Req("AmpE2EDummy"));
            Thread.Sleep(500);
            Sync(ModSyncPacket.TRANSFER_CANCEL, 0, 0, 1, "");
        }

        private void TransferControl() {
            for(int i = 0; i < 300; i++) Sync(ModSyncPacket.CHUNK_ACK, random.Next(), 0, 1, "");
            for(int i = 0; i < 50; i++) Sync(ModSyncPacket.READY_NEXT, random.Next(-5, 5), 0, 1, "");
            for(int i = 0; i < 20; i++) Sync(ModSyncPacket.TRANSFER_CANCEL, random.Next(), 0, 1, "");
            Sync(ModSyncPacket.TRANSFER_END, 1, 0, 1, "{\"ok\":false}");
            Sync(ModSyncPacket.TRANSFER_START, 1, 0, 1, "{\"folder\":\"../../evil\",\"files\":[{\"path\":\"../../x.dll\",\"size\":-5}]}");
            Sync(ModSyncPacket.HOST_MOD_SET, 1, 0, 1, "{\"mods\":[]}");
            Sync(ModSyncPacket.SETTING_CHANGED, 1, 0, 1, "{\"values\":[{\"mod\":\"MultiplayerMod\",\"option\":\"PvP\",\"title\":\"Disabled\",\"index\":0}]}");
            Sync(ModSyncPacket.SETTINGS_SNAPSHOT, 1, 0, 1, Junk(3000));
        }

        private void Files() {
            Send(new ModFilePacket(1, 0, 0, Convert.ToBase64String(new byte[100])));
            Send(new ModFilePacket(-1, -1, -1, "not base64 !!!"));
            Send(new ModFilePacket(int.MaxValue, int.MaxValue, long.MaxValue, new string('A', 44000)));
            Send(new ModFilePacket(0, 0, 0, null));
            foreach(byte kind in new byte[] { 0, 11, 99, 255 }) Sync(kind, 1, 0, 1, Junk(100));
        }

        private void Gameplay() {
            var nan = new Vector3(float.NaN, float.PositiveInfinity, float.NegativeInfinity);
            var far = new Vector3(1e30f, -1e30f, 1e30f);
            Send(new ItemSpawnPacket { itemId = -1, type = "../../../evil", category = 255, clientsideId = -5, position = nan, rotation = nan });
            Send(new ItemSpawnPacket { itemId = 0, type = new string('i', 30000), category = 1, clientsideId = 7, position = far, rotation = far });
            Send(new ItemSpawnPacket { itemId = 1, type = null, category = 1, clientsideId = 8, position = new Vector3(0, 0, 0), rotation = new Vector3(0, 0, 0) });
            for(int i = 0; i < 200; i++) Send(new ItemPositionPacket { timestamp = random.Next(), itemId = random.Next(-10, 1000), position = nan, rotation = far, velocity = nan, angularVelocity = far });
            Send(new ItemOwnerPacket { itemId = 1, owning = true });
            Send(new ItemOwnerPacket { itemId = -1, owning = true });
            Send(new PlayerDataPacket { clientId = 999, name = new string('n', 30000), creatureId = "../../evil", height = float.NaN, playerPos = nan, playerRotY = float.NaN, uniqueId = null });
            Send(new PlayerEquipmentPacket { clientId = 999, colors = new Color[0], equipment = Enumerable.Range(0, 2000).Select(i => "Apparel" + i).ToArray(), ethnicGroup = null });
            Send(new PlayerPositionPacket { playerId = -3, position = nan, headPos = far, headRot = nan, handLeftPos = nan, handLeftRot = nan, handRightPos = nan, handRightRot = nan, rotationY = float.NaN });
            Send(new LevelChangePacket("../../../etc", "x", Enumerable.Range(0, 3000).Select(i => "o" + i).ToArray(), EventTime.OnEnd));
            Send(new LevelChangePacket(null, null, null, EventTime.OnStart));
            Send(new ModListPacket { modList = Enumerable.Range(0, 3000).Select(i => new string('m', 10)).ToArray() });
        }

        // Gets greeted properly (same mods: ready), answers the level, then repeats the gameplay garbage as a playing client
        private void GreetThenGameplay() {
            foreach(ModSyncPacket p in ModSyncPacket.Create(ModSyncPacket.JOINER_REPORT, Report("ready"))) Send(p);
            Thread.Sleep(1500);
            Send(new LevelChangePacket("Home", "Sandbox", new string[0], EventTime.OnEnd));
            Thread.Sleep(1500);
            Gameplay();
            Send(new ItemSpawnPacket { itemId = 0, type = "SwordShortCommon", category = 1, clientsideId = 1, position = new Vector3(0, 1, 0), rotation = new Vector3(0, 0, 0) });
            for(int i = 0; i < 100; i++) Send(new ItemSpawnPacket { itemId = 0, type = "DaggerCommon", category = 1, clientsideId = 100 + i, position = new Vector3(i, 1, 0), rotation = new Vector3(0, 0, 0) });
        }

        // Writes straight into the TCP stream: frames Netamite's own serializer would never produce
        private void RawFrames() {
            NetworkStream stream = RawStream();
            if(stream == null) { Out.Info("FUZZ raw frames skipped: no access to the TCP stream"); return; }
            var frames = new List<byte[]>();
            for(int type = 1; type < 256; type++) frames.Add(Frame((byte) type, new byte[] { 1, 2, 3 }));          // every id, truncated body
            frames.Add(Frame(111, new byte[0]));                                                               // MOD_SYNC with nothing
            frames.Add(Frame(111, new byte[] { 2, 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 255, 255 }));              // string length beyond the frame
            frames.Add(Frame(112, Enumerable.Repeat((byte) 0xFF, 60000).ToArray()));                           // MOD_FILE of 0xFF
            var noise = new byte[65530]; random.NextBytes(noise);
            frames.Add(Frame(111, noise));                                                                     // the largest frame, random body
            foreach(byte[] f in frames) {
                if(dropped) break;
                try { lock(stream) stream.Write(f, 0, f.Length); sent++; } catch(Exception) { break; }
                Thread.Sleep(2);
            }
            // Finally a length that never completes, then plain noise
            try { byte[] b = { 0xFF, 0xFF, 111, 1, 2 }; stream.Write(b, 0, b.Length); var n = new byte[3000]; random.NextBytes(n); stream.Write(n, 0, n.Length); sent += 2; } catch(Exception) { }
        }

        private static byte[] Frame(byte type, byte[] body) {
            int length = 1 + body.Length;
            var f = new byte[2 + length];
            f[0] = (byte) (length & 0xFF); f[1] = (byte) (length >> 8); f[2] = type;
            Array.Copy(body, 0, f, 3, body.Length);
            return f;
        }

        private NetworkStream RawStream() {
            object tcp = typeof(IPClient).GetField("tcp", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(client);
            return tcp?.GetType().GetProperty("stream", BindingFlags.Public | BindingFlags.Instance)?.GetValue(tcp) as NetworkStream;
        }
    }
}
