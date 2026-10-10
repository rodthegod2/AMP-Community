using AMP.ModSync;
using Netamite.Network.Packet;
using Netamite.Network.Packet.Attributes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;

namespace AmpBot {
    // --selftest: 1) the bot's packet copies have AMP's ids, field types and field order (read from AMP's sources),
    // 2) a bot host and a bot joiner talk to each other over a real Netamite loopback (mod list, download, greeting, snapshot).
    public static class SelfTest {
        private static int fails;

        private static void Is(string name, bool ok, string detail = "") {
            Out.Line(ok ? "OK" : "FAIL", "selftest " + name + (detail == "" ? "" : " - " + detail));
            if(!ok) fails++;
        }

        public static int Run(string ampRepo) {
            if(ampRepo != null) LayoutTests(ampRepo);
            LoopbackTest();
            Out.Line(fails == 0 ? "DONE" : "FAIL", fails == 0 ? "selftest passed" : fails + " selftest checks failed");
            return fails == 0 ? 0 : 1;
        }

        #region Packet layouts

        private static void LayoutTests(string repo) {
            string folder = Path.Combine(repo, "Network", "Packets");
            Dictionary<string, int> ids = PacketIds(File.ReadAllText(Path.Combine(folder, "PacketType.cs")));
            Check<ModSyncPacket>(folder, "ModSyncPacket.cs", ids);
            Check<ModFilePacket>(folder, "ModFilePacket.cs", ids);
            Check<ServerInfoPacket>(folder, "ServerInfoPacket.cs", ids);
            Check<LevelChangePacket>(folder, "LevelChangePacket.cs", ids);
        }

        // AMP's PacketType enum: NAME = value, or NAME, (previous + 1)
        private static Dictionary<string, int> PacketIds(string source) {
            var ids = new Dictionary<string, int>();
            int next = 0;
            foreach(Match m in Regex.Matches(source, @"^\s*([A-Z_]+)\s*(?:=\s*(\d+))?\s*,?", RegexOptions.Multiline)) {
                int value = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : next;
                ids[m.Groups[1].Value] = value;
                next = value + 1;
            }
            return ids;
        }

        private static readonly Dictionary<string, Type> Keywords = new Dictionary<string, Type> {
            { "byte", typeof(byte) }, { "int", typeof(int) }, { "long", typeof(long) }, { "bool", typeof(bool) },
            { "string", typeof(string) }, { "string[]", typeof(string[]) }, { "EventTime", typeof(EventTime) },
        };

        private static void Check<TPacket>(string folder, string file, Dictionary<string, int> ids) where TPacket : NetPacket {
            string source = File.ReadAllText(Path.Combine(folder, "Implementation", file));
            Match def = Regex.Match(source, @"\[PacketDefinition\(\(byte\)\s*PacketType\.(\w+)\)\]");
            int ampId;
            Is(file + " id", def.Success && ids.TryGetValue(def.Groups[1].Value, out ampId) && ampId == typeof(TPacket).GetCustomAttribute<PacketDefinition>(true).packetType,
              "AMP " + (def.Success ? def.Groups[1].Value : "?") + ", bot " + typeof(TPacket).GetCustomAttribute<PacketDefinition>(true).packetType);

            List<string> amp = Regex.Matches(source, @"\[SyncedVar(\(\w*\))?\]\s*public\s+([\w\[\]]+)\s+(\w+)").Cast<Match>()
                                    .Select(m => (Keywords.ContainsKey(m.Groups[2].Value) ? Keywords[m.Groups[2].Value].Name : m.Groups[2].Value) + " " + m.Groups[3].Value
                                                 + (m.Groups[1].Value == "(true)" ? " low" : "")).ToList();
            // Netamite's own order: GetMembers() filtered by [SyncedVar]
            List<string> bot = typeof(TPacket).GetMembers().Select(member => new { member, att = member.GetCustomAttribute<SyncedVar>(true) })
                                        .Where(x => x.att != null).Select(x => ((FieldInfo) x.member).FieldType.Name + " " + x.member.Name + (x.att.LowPrecision ? " low" : "")).ToList();
            Is(file + " fields", amp.Count > 0 && amp.SequenceEqual(bot), "AMP [" + string.Join(", ", amp) + "] bot [" + string.Join(", ", bot) + "]");
        }

        #endregion

        #region Loopback

        private static void LoopbackTest() {
            string root = Path.Combine(Path.GetTempPath(), "AmpBot-selftest-" + Guid.NewGuid().ToString("N"));
            try {
                string hostMods = Path.Combine(root, "host"), joinMods = Path.Combine(root, "join");
                Mod(hostMods, "Shared", 1000);
                Mod(joinMods, "Shared", 1000);
                Mod(hostMods, "Extra", TransferSender.ChunkSize * 5 + 77); // several chunks, more than the ack window per file is not needed here
                File.WriteAllBytes(Path.Combine(hostMods, "Extra", "empty.bin"), new byte[0]);

                var snapshot = new List<OptionValue> { new OptionValue { mod = "MultiplayerMod", option = "PvP", title = "Disabled", index = 0 } };
                var hostX = new Expectations { reports = { JoinerStatus.NeedsSetup, JoinerStatus.Ready }, served = { "Extra" }, greetings = 1, sendSnapshot = snapshot, lingerSec = 0 };
                var joinX = new Expectations { planBlocks = true, download = true, downloaded = { "Extra" }, files = { "Extra/empty.bin", "Extra/data.bin" }, snapshot = snapshot, lingerSec = 0 };

                const int port = 27919;
                var hostList = new Checklist();
                var joinList = new Checklist();
                var hoster = new Hoster(port, hostMods, Path.Combine(root, "hostcache"), hostX, hostList);
                hoster.Start();
                var joiner = new Joiner("127.0.0.1", port, joinMods, Path.Combine(root, "joincache"), joinX, joinList);
                bool connected = joiner.Connect(DateTime.UtcNow.AddSeconds(30));
                Is("loopback connects", connected);
                bool joined = connected && joinList.Wait(TimeSpan.FromSeconds(60));
                Is("loopback joiner saw everything", joined, joinList.Failure ?? "");
                bool hosted = hostList.Wait(TimeSpan.FromSeconds(10));
                Is("loopback host saw everything", hosted, hostList.Failure ?? "");
                joiner.Done();
                joiner.Disconnect();
                hoster.Stop();
                Thread.Sleep(500);
            } finally {
                try { Directory.Delete(root, true); } catch(Exception) { }
            }
        }

        private static void Mod(string mods, string name, int bytes) {
            string dir = Path.Combine(mods, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "manifest.json"), "{\"Name\":\"" + name + "\",\"ModVersion\":\"1.0\"}");
            var data = new byte[bytes];
            new Random(bytes).NextBytes(data);
            File.WriteAllBytes(Path.Combine(dir, "data.bin"), data);
        }

        #endregion
    }
}
