using AMP.ModSync;
using Netamite.Client.Implementation;
using Netamite.Network.Packet;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AmpBot {
    // --join: behaves like an AMP joiner (ModSyncClient + ModTransferClient + SettingsSyncClient) without the game
    public class Joiner {
        private readonly string address, modsFolder, cacheFolder;
        private readonly int port;
        private readonly Expectations x;
        private readonly Checklist list;
        private readonly MessageAssembler assembler = new MessageAssembler();
        private readonly object gate = new object();
        private IPClient client;
        private volatile bool finished;
        private readonly GameplayWatch watch;
        private BotPlayer player;
        private int playerTickRate = 10;

        // Download state (guarded by gate)
        private ModCache cache;
        private Dictionary<string, ModEntry> pending;
        private int requestId;
        private TransferReceiver receiver;
        private ModEntry receiving;
        private string staging;
        private int receivingId;

        public Joiner(string address, int port, string modsFolder, string cacheFolder, Expectations x, Checklist list) {
            this.address = address;
            this.port = port;
            this.modsFolder = modsFolder;
            this.cacheFolder = cacheFolder;
            this.x = x;
            this.list = list;

            if(x.planBlocks.HasValue) list.Expect("planBlocks=" + x.planBlocks.Value.ToString().ToLowerInvariant());
            foreach(KeyValuePair<string, long> q in x.questIds) list.Expect("questId " + q.Key + "=" + q.Value);
            foreach(string folder in x.downloaded) list.Expect("downloaded " + folder);
            foreach(string file in x.files) list.Expect("file " + file);
            if(x.greeted) list.Expect("greeted");
            foreach(OptionValue v in x.snapshot) list.Expect("snapshot " + v.mod + "/" + v.option + (string.IsNullOrEmpty(v.title) ? "" : "=" + v.title));
            foreach(OptionValue v in x.settingChanged) list.Expect("settingChanged " + v.mod + "/" + v.option + "=" + v.title);
            watch = new GameplayWatch(x, list);
        }

        // Connects (retrying while the host isn't up yet) and returns once connected; false when the deadline passed
        public bool Connect(DateTime deadline) {
            while(DateTime.UtcNow < deadline && list.Failure == null) {
                var c = new IPClient(address, port) { ClientName = "AmpBot" };
                var answered = new ManualResetEvent(false);
                string error = null;
                c.OnConnect += () => answered.Set();
                c.OnConnectionError += e => { error = e; answered.Set(); };
                c.OnDisconnect += reason => { if(!finished) list.Fail("disconnected by the host: " + reason); };
                c.OnDataReceived += OnPacket;
                client = c;
                c.Connect("");
                answered.WaitOne(TimeSpan.FromSeconds(30));
                if(c.IsConnected) {
                    Out.Info("connected to " + address + ":" + port + " as client " + c.ClientId);
                    watch.IgnorePlayer(c.ClientId);
                    return true;
                }
                c.Disconnect();
                Thread.Sleep(2000);
            }
            return false;
        }

        // Everything was seen: the host closing the connection from now on is expected
        public void Done() { finished = true; }

        public void StopPlaying() { player?.Stop(); }

        public void Disconnect() {
            finished = true;
            try { client?.Disconnect(); } catch(Exception) { }
        }

        private void Send(NetPacket packet) { client.SendReliable(packet); }

        private volatile bool serverInfoSeen;

        private void OnPacket(NetPacket p) {
            try {
                if(p is ServerInfoPacket) serverInfoSeen = true;
                else if(!serverInfoSeen && x.quietBeforeGreeting && !(p is ModSyncPacket || p is ModFilePacket)) {
                    list.Fail("got " + p.GetType().Name + " while still in mod sync (before the host's greeting)");
                }
                if(p is ModSyncPacket) OnModSync((ModSyncPacket) p);
                else if(p is ModFilePacket) OnChunk((ModFilePacket) p);
                else if(p is ServerInfoPacket) {
                    ServerInfoPacket info = (ServerInfoPacket) p;
                    playerTickRate = info.player_tickrate;
                    list.Mark("greeted", "ServerInfo version " + info.version + ", max " + info.max_players);
                }
                else if(p is AllowTransmissionPacket) {
                    bool allow = ((AllowTransmissionPacket) p).allow;
                    Out.Info("transmission " + (allow ? "allowed" : "stopped"));
                    if(allow && x.play) StartPlaying();
                }
                else if(p is PlayerDataPacket) watch.OnPlayerData((PlayerDataPacket) p);
                else if(p is PlayerEquipmentPacket) watch.OnEquipment((PlayerEquipmentPacket) p);
                else if(p is PlayerPositionPacket) watch.OnPlayerMoved(((PlayerPositionPacket) p).playerId, ((PlayerPositionPacket) p).position);
                else if(p is PlayerRagdollPacket) watch.OnPlayerMoved(((PlayerRagdollPacket) p).playerId, ((PlayerRagdollPacket) p).position);
                else if(p is ItemSpawnPacket) {
                    ItemSpawnPacket item = (ItemSpawnPacket) p;
                    bool ours = item.clientsideId == OurItem && player != null && item.type == x.playItem;
                    if(ours) player.ItemHasId(item.itemId);
                    watch.OnItemSpawn(item, ours);
                }
                else if(p is ItemPositionPacket) watch.OnItemMoved((ItemPositionPacket) p);
                else if(p is ItemOwnerPacket) Out.Info("item " + ((ItemOwnerPacket) p).itemId + (((ItemOwnerPacket) p).owning ? " is ours" : " belongs to someone else")); else if(p is LevelChangePacket) {
                    // Like a joiner that already is in the host's level: answer with the same level, loading finished
                    LevelChangePacket level = (LevelChangePacket) p;
                    Out.Info("host level " + level.level + " / " + level.mode + ", answering as loaded");
                    Send(new LevelChangePacket(level.level, level.mode, level.options, EventTime.OnEnd));
                }
            } catch(Exception e) {
                list.Fail("handling " + p.GetType().Name + " threw " + e);
            }
        }

        private const int OurItem = 1; // clientsideId of the one item the bot spawns; the host echoes it with the network id

        // Like ClientSync.PlayerTickThread once transmission is allowed: player data, equipment, mod list, then positions
        private void StartPlaying() {
            lock(gate) {
                if(player != null) return;
                IPClient c = client;
                player = new BotPlayer(x, playerTickRate, () => c.FixedTimestamp, packet => c.SendReliable(packet), packet => c.SendUnreliable(packet));
                player.SpawnItemNow += () => {
                    Out.Info("spawning " + x.playItem);
                    c.SendReliable(player.ItemSpawn(0, OurItem));
                };
            }
            player.Start(client.ClientId, watch.LastOtherPosition ?? new Netamite.Datatype.Vector3(0, 0, 0), "AmpBot");
            client.SendReliable(new ModListPacket { modList = new[] { "AMP" } });
        }

        private void OnModSync(ModSyncPacket packet) {
            if(packet.parts < 1) return;
            switch(packet.kind) {
                case ModSyncPacket.HOST_MOD_SET: {
                    string json = assembler.Add("host:" + packet.transferId, packet.part, packet.parts, packet.data);
                    if(json != null) Task.Run(() => { try { OnHostModSet(json); } catch(Exception e) { list.Fail("planning threw " + e); } });
                    break;
                }
                case ModSyncPacket.SETTINGS_SNAPSHOT:
                case ModSyncPacket.SETTING_CHANGED: {
                    string json = assembler.Add("settings:" + packet.transferId, packet.part, packet.parts, packet.data);
                    if(json != null) OnSettings(packet.kind, JsonConvert.DeserializeObject<SettingsMessage>(json));
                    break;
                }
                case ModSyncPacket.TRANSFER_START:
                case ModSyncPacket.TRANSFER_END: {
                    string json = assembler.Add("xfer:" + packet.kind + ":" + packet.transferId, packet.part, packet.parts, packet.data);
                    if(json == null) break;
                    if(packet.kind == ModSyncPacket.TRANSFER_START) OnTransferStart(JsonConvert.DeserializeObject<TransferManifest>(json));
                    else OnTransferEnd(JsonConvert.DeserializeObject<TransferEnd>(json));
                    break;
                }
            }
        }

        private void Report(string status, string detail) {
            string json = JsonConvert.SerializeObject(new JoinerReport { platform = x.platform, status = status, detail = detail ?? "" });
            foreach(ModSyncPacket packet in ModSyncPacket.Create(ModSyncPacket.JOINER_REPORT, json)) Send(packet);
            Out.Info("reported " + status);
        }

        private void OnHostModSet(string json) {
            HostModSet set = JsonConvert.DeserializeObject<HostModSet>(json);
            Out.Info("host mod set: " + set.mods.Count + " mods, mode " + set.mode + ", platform " + set.platform + ", AMP " + Short(set.ampBuild));
            Report(JoinerStatus.Checking, "");

            Out.Info("questModioIds: " + string.Join(", ", set.mods.Select(m => m.folder + "=" + m.questModioId)));
            foreach(KeyValuePair<string, long> q in x.questIds) {
                ModEntry mod = set.mods.FirstOrDefault(m => m.folder == q.Key);
                if(mod != null && mod.questModioId == q.Value) list.Mark("questId " + q.Key + "=" + q.Value);
                else list.Fail("the host's mod list has " + q.Key + " with questModioId " + (mod == null ? "(missing)" : mod.questModioId.ToString()) + ", expected " + q.Value);
            }

            bool samePlatform = string.Equals(set.platform, x.platform, StringComparison.OrdinalIgnoreCase);
            List<ModEntry> local = ModInventory.Scan(modsFolder, cacheFolder, samePlatform);
            SyncPlan plan = SyncPlanner.Plan(set, local, ModInventory.ReadLoadOrder(modsFolder), x.platform);
            Out.Info("plan blocksJoin=" + plan.BlocksJoin + " ampDiffers=" + plan.ampDiffers + ": " + plan.Describe());

            if(x.planBlocks.HasValue) {
                if(plan.BlocksJoin == x.planBlocks.Value) list.Mark("planBlocks=" + x.planBlocks.Value.ToString().ToLowerInvariant());
                else { list.Fail("expected the plan to " + (x.planBlocks.Value ? "block" : "not block") + " joining"); return; }
            }

            if(!plan.BlocksJoin) { Report(JoinerStatus.Ready, ""); return; }
            if(!x.download) { Report(JoinerStatus.Ready, plan.Describe()); return; } // "join anyway"

            List<ModEntry> mods = plan.Of(SyncAction.Replace).Concat(plan.Of(SyncAction.Add)).Select(s => s.hostMod).ToList();
            Report(JoinerStatus.NeedsSetup, plan.Describe());
            if(mods.Count == 0) { Report(JoinerStatus.Ready, plan.Describe()); return; }

            List<ModSyncPacket> packets = ModSyncPacket.Create(ModSyncPacket.MODS_REQUEST, JsonConvert.SerializeObject(new ModsRequest { folders = mods.Select(m => m.folder).ToList() }));
            lock(gate) {
                cache = new ModCache(cacheFolder);
                pending = mods.ToDictionary(m => m.folder);
                requestId = packets[0].transferId;
            }
            Out.Info("requesting " + string.Join(", ", mods.Select(m => m.folder)));
            foreach(ModSyncPacket packet in packets) Send(packet);
        }

        private void OnTransferStart(TransferManifest manifest) {
            lock(gate) {
                ModEntry expected;
                if(pending == null || manifest == null || manifest.requestId != requestId || receiver != null
                   || !pending.TryGetValue(manifest.folder ?? "", out expected) || manifest.hash != expected.hash) {
                    list.Fail("the host sent a mod that wasn't asked for: " + (manifest == null ? "null" : manifest.folder));
                    return;
                }
                Out.Info("receiving " + manifest.folder + ": " + manifest.files.Count + " files, " + manifest.TotalBytes + " bytes");
                staging = cache.NewStagingFolder();
                receiver = TransferReceiver.Start(manifest, staging, expected.sizeBytes * 2 + 1024 * 1024);
                receiving = expected;
                receivingId = manifest.transferId;
                if(receiver.Complete) FinishMod();
            }
        }

        private void OnChunk(ModFilePacket packet) {
            lock(gate) {
                if(receiver == null || packet.transferId != receivingId) return;
                byte[] bytes = TransferRules.DecodeChunk(packet.data);
                if(bytes == null || !receiver.OnChunk(packet.fileIndex, packet.offset, bytes)) {
                    list.Fail("a chunk of " + receiving.folder + " doesn't fit (file " + packet.fileIndex + " at " + packet.offset + ")");
                    return;
                }
                Send(new ModSyncPacket(ModSyncPacket.CHUNK_ACK, packet.transferId, 0, 1, ""));
                if(receiver.Complete) FinishMod();
            }
        }

        // Inside the lock: verify like ModTransferClient.Commit does, then ask for the next mod or join
        private void FinishMod() {
            ModEntry mod = receiving;
            int transferId = receivingId;
            receiver.Dispose();
            receiver = null;
            receiving = null;
            receivingId = 0;

            CacheEntry entry = cache.Commit(staging, mod);
            if(entry == null) { list.Fail(mod.folder + " failed verification (hash mismatch)"); return; }
            string content = cache.ContentFolder(entry.hash);
            list.Mark("downloaded " + mod.folder, "hash " + Short(entry.hash) + " verified, " + entry.sizeBytes + " bytes");
            foreach(string wanted in x.files.Where(f => f.StartsWith(mod.folder + "/"))) {
                string path = Path.Combine(content, wanted.Substring(mod.folder.Length + 1).Replace('/', '\\'));
                if(File.Exists(path)) list.Mark("file " + wanted, new FileInfo(path).Length + " bytes");
                else list.Fail("downloaded " + mod.folder + " has no " + wanted);
            }

            pending.Remove(mod.folder);
            if(pending.Count > 0) {
                Send(new ModSyncPacket(ModSyncPacket.READY_NEXT, transferId, 0, 1, ""));
                return;
            }
            pending = null;
            Report(JoinerStatus.Ready, "downloaded the host's mods"); // the game restarts at this point; the bot just joins
        }

        private void OnTransferEnd(TransferEnd end) {
            if(end == null || end.ok) return;
            list.Fail("the host stopped the download: " + end.error);
        }

        private void OnSettings(byte kind, SettingsMessage message) {
            if(message == null || message.values == null) return;
            string what = kind == ModSyncPacket.SETTINGS_SNAPSHOT ? "snapshot" : "settingChanged";
            Out.Info(what + ": " + string.Join(", ", message.values.Select(v => v.mod + "/" + v.option + "=" + v.title + " [" + v.index + "]")));
            if(kind == ModSyncPacket.SETTING_CHANGED) {
                foreach(OptionValue no in x.noSettingChanged) {
                    if(message.values.Any(v => v != null && v.mod == no.mod && v.option == no.option && v.title == no.title))
                        list.Fail("SETTING_CHANGED " + no.mod + "/" + no.option + "=" + no.title + " arrived although that value didn't change");
                }
            }
            List<OptionValue> wanted = kind == ModSyncPacket.SETTINGS_SNAPSHOT ? x.snapshot : x.settingChanged;
            foreach(OptionValue w in wanted) {
                OptionValue got = message.values.FirstOrDefault(v => v != null && v.mod == w.mod && v.option == w.option);
                if(got == null) continue;
                bool anyTitle = kind == ModSyncPacket.SETTINGS_SNAPSHOT && string.IsNullOrEmpty(w.title);
                string item = what + " " + w.mod + "/" + w.option + (anyTitle ? "" : "=" + w.title);
                if(anyTitle || got.title == w.title) list.Mark(item, got.title);
            }
        }

        private static string Short(string hash) { return string.IsNullOrEmpty(hash) ? "-" : hash.Substring(0, Math.Min(12, hash.Length)); }
    }
}
