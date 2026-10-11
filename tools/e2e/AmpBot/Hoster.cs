using AMP.ModSync;
using Netamite.Network.Packet;
using Netamite.Server.Data;
using Netamite.Server.Implementation;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace AmpBot {
    // --host: behaves like an AMP host towards joiners (ModSyncHost + ModTransferHost + the start of Server.GreetPlayer)
    public class Hoster {
        // Defines.MOD_VERSION of the AMP build under test; a different value only makes the joiner show a warning
        public const string AmpVersion = "Beta 6";
        private const int Window = 8;
        private const int ReadyWaitMs = 120 * 1000;

        private readonly int port;
        private readonly string modsFolder;
        private readonly Expectations x;
        private readonly Checklist list;
        private readonly MessageAssembler assembler = new MessageAssembler();
        private readonly ConcurrentDictionary<int, Upload> uploads = new ConcurrentDictionary<int, Upload>();
        private readonly List<string> reportsSeen = new List<string>();
        private IPServer server;
        private List<ModEntry> hostMods;
        private string hostModSetJson;
        private int nextTransferId;
        private int greetings;
        private int nextItemId = 100;
        private readonly ConcurrentDictionary<int, bool> playing = new ConcurrentDictionary<int, bool>(); // fully greeted clients
        private readonly GameplayWatch watch;
        private readonly BotPlayer player;
        private ItemSpawnPacket botItem;          // the bot avatar's item once spawned, for joiners greeted later
        private const int BotPlayerId = 900;      // the host's own avatar; real client ids start at 1

        private class Upload {
            public volatile bool cancelled;
            public volatile int transferId;
            public volatile TransferSender sender;
            public readonly SemaphoreSlim readyNext = new SemaphoreSlim(0);
        }

        public Hoster(int port, string modsFolder, string cacheFolder, Expectations x, Checklist list) {
            this.port = port;
            this.modsFolder = modsFolder;
            this.x = x;
            this.list = list;

            for(int i = 0; i < x.reports.Count; i++) list.Expect("report " + (i + 1) + " " + x.reports[i]);
            foreach(string folder in x.served) list.Expect("served " + folder);
            for(int i = 1; i <= x.greetings; i++) list.Expect("greeting " + i);

            List<ModEntry> mods = ModInventory.Scan(modsFolder, cacheFolder, true);
            hostMods = mods.Where(m => m.enabled).ToList();
            foreach(ModEntry mod in hostMods) {
                long questId;
                if(x.sendQuestIds.TryGetValue(mod.folder, out questId)) mod.questModioId = questId;
            }
            ModEntry amp = mods.FirstOrDefault(m => m.isAmp && m.enabled);
            hostModSetJson = JsonConvert.SerializeObject(new HostModSet {
                platform  = "windows",
                mode      = x.modSyncMode,
                mods      = hostMods,
                loadOrder = ModInventory.ReadLoadOrder(modsFolder),
                ampBuild  = amp != null ? amp.hash : "",
            });
            watch = new GameplayWatch(x, list);
            watch.IgnorePlayer(BotPlayerId);
            player = new BotPlayer(x, 10, () => Netamite.Helper.TimeHelper.Millis, SendToPlaying, p => SendToPlaying(p, false));
            player.SpawnItemNow += () => {
                int id = Interlocked.Increment(ref nextItemId);
                botItem = player.ItemSpawn(id, 0);
                player.ItemHasId(id);
                Out.Info("spawning " + x.playItem + " as item " + id);
                SendToPlaying(botItem);
            };
            Out.Info("hosting " + hostMods.Count + " mods: " + string.Join(", ", hostMods.Select(m => m.folder + (m.questModioId != 0 ? " (Quest: " + m.questModioId + ")" : ""))));
        }

        public void Start() {
            server = new IPServer((short) port, 8);
            server.OnConnect += OnConnect;
            server.OnDataReceived += OnPacket;
            server.OnDisconnect += (c, reason) => {
                Out.Info("client " + c.ClientId + " left: " + reason);
                bool ignored;
                playing.TryRemove(c.ClientId, out ignored);
                Upload upload;
                if(uploads.TryRemove(c.ClientId, out upload)) Cancel(upload);
            };
            server.Start();
            Out.Info("listening on port " + port);
        }

        public void Stop() {
            player.Stop();
            try { server?.Stop(); } catch(Exception) { }
        }

        private void SendToPlaying(NetPacket p) { SendToPlaying(p, true); }

        private void SendToPlaying(NetPacket p, bool reliable) {
            foreach(ClientInformation c in server.Clients) {
                if(!playing.ContainsKey(c.ClientId)) continue;
                if(reliable) server.SendReliableTo(c, p); else server.SendUnreliableTo(c, p);
            }
        }

        private void OnConnect(ClientInformation c) {
            Out.Info("client " + c.ClientId + " (" + c.ClientName + ") connected, sending the mod list");
            foreach(ModSyncPacket packet in ModSyncPacket.Create(ModSyncPacket.HOST_MOD_SET, hostModSetJson)) server.SendTo(c, packet);
        }

        private void OnPacket(ClientInformation c, NetPacket p) {
            try {
                ModSyncPacket packet = p as ModSyncPacket;
                if(packet == null) {
                    OnGameplay(c, p);
                    return;
                }
                Upload upload;
                switch(packet.kind) {
                    case ModSyncPacket.JOINER_REPORT: {
                        string json = assembler.Add(c.ClientId + ":" + packet.transferId, packet.part, packet.parts, packet.data);
                        if(json != null) OnReport(c, JsonConvert.DeserializeObject<JoinerReport>(json));
                        break;
                    }
                    case ModSyncPacket.MODS_REQUEST: {
                        string json = assembler.Add(c.ClientId + ":" + packet.transferId, packet.part, packet.parts, packet.data);
                        if(json != null) OnRequest(c, packet.transferId, JsonConvert.DeserializeObject<ModsRequest>(json));
                        break;
                    }
                    case ModSyncPacket.CHUNK_ACK:
                        if(uploads.TryGetValue(c.ClientId, out upload) && upload.transferId == packet.transferId) upload.sender?.Ack();
                        break;
                    case ModSyncPacket.READY_NEXT:
                        if(uploads.TryGetValue(c.ClientId, out upload) && upload.transferId == packet.transferId) upload.readyNext.Release();
                        break;
                    case ModSyncPacket.TRANSFER_CANCEL:
                        Out.Info("client " + c.ClientId + " cancelled the download");
                        if(uploads.TryGetValue(c.ClientId, out upload)) Cancel(upload);
                        break;
                }
            } catch(Exception e) {
                list.Fail("handling " + p.GetType().Name + " threw " + e);
            }
        }

        private void OnReport(ClientInformation c, JoinerReport report) {
            if(report == null) return;
            Out.Info("client " + c.ClientId + " reports " + report.status + " (" + report.platform + ")" + (string.IsNullOrEmpty(report.detail) ? "" : ": " + report.detail));
            if(report.status != JoinerStatus.Checking) {
                lock(reportsSeen) {
                    reportsSeen.Add(report.status);
                    int n = reportsSeen.Count;
                    if(n <= x.reports.Count) {
                        if(x.reports[n - 1] == report.status) list.Mark("report " + n + " " + report.status);
                        else list.Fail("report " + n + " was " + report.status + ", expected " + x.reports[n - 1]);
                    }
                }
            }
            if(report.status == JoinerStatus.Ready) Greet(c);
        }

        // Server.GreetPlayer, first half: ServerInfo and the level; the joiner answers with the same level once it is loaded
        private void Greet(ClientInformation c) {
            server.SendTo(c, new ServerInfoPacket(AmpVersion, server.MaxClients));
            server.SendTo(c, new LevelChangePacket("Home", "Sandbox", new string[0], EventTime.OnEnd)); // no options: any Home counts as the same level
        }

        // Server.GreetPlayer, second half: the other players, the items, transmission allowed, time sync, settings snapshot
        private void FinishGreeting(ClientInformation c) {
            if(!playing.TryAdd(c.ClientId, true)) return;
            if(player.PlayerId > 0) {
                server.SendTo(c, new PlayerDataPacket { clientId = BotPlayerId, name = "AmpBot", creatureId = BotPlayer.CreatureId, height = 1.8f, playerPos = player.Center, playerRotY = 0, uniqueId = "" });
            }
            if(botItem != null) server.SendTo(c, botItem);
            server.SendTo(c, new AllowTransmissionPacket { allow = true });
            server.InitializeTimeSync(c);
            if(x.sendSnapshot.Count > 0) {
                string json = JsonConvert.SerializeObject(new SettingsMessage { values = x.sendSnapshot });
                foreach(ModSyncPacket packet in ModSyncPacket.Create(ModSyncPacket.SETTINGS_SNAPSHOT, json)) server.SendTo(c, packet);
            }
            list.Mark("greeting " + Interlocked.Increment(ref greetings), "client " + c.ClientId);
        }

        // What Server and the packets' ProcessServer do with a playing client's gameplay packets, as far as the bot needs it
        private void OnGameplay(ClientInformation c, NetPacket p) {
            if(p is LevelChangePacket) {
                LevelChangePacket level = (LevelChangePacket) p;
                Out.Info("client " + c.ClientId + " is in " + level.level + " (" + level.eventTime + ")");
                if(level.eventTime == EventTime.OnEnd) FinishGreeting(c);
            } else if(p is PlayerDataPacket) {
                PlayerDataPacket data = (PlayerDataPacket) p;
                data.clientId = c.ClientId; // the server decides who a player is
                watch.OnPlayerData(data);
                if(x.play) player.Start(BotPlayerId, data.playerPos ?? new Netamite.Datatype.Vector3(0, 0, 0), "AmpBot");
                if(!string.IsNullOrEmpty(x.spawnCreature) && Interlocked.Exchange(ref creatureSent, 1) == 0) {
                    Netamite.Datatype.Vector3 at = data.playerPos ?? new Netamite.Datatype.Vector3(0, 0, 0);
                    var spawn = new CreatureSpawnPacket { creatureId = 700, clientsideId = 0, type = x.spawnCreature, container = "", factionId = 0,
                                                          position = new Netamite.Datatype.Vector3(at.x + 3f, at.y, at.z), rotationY = 0, health = x.creatureHealth, maxHealth = x.creatureMaxHealth, height = 1.8f };
                    server.SendTo(c, spawn);
                    Out.Info("spawned NPC " + x.spawnCreature + " (700) with " + x.creatureHealth + " of " + x.creatureMaxHealth + " health next to client " + c.ClientId);
                }
            } else if(p is PlayerEquipmentPacket) {
                watch.OnEquipment((PlayerEquipmentPacket) p);
            } else if(p is PlayerRagdollPacket) {
                watch.OnPlayerMoved(c.ClientId, ((PlayerRagdollPacket) p).position);
            } else if(p is PlayerPositionPacket) {
                watch.OnPlayerMoved(c.ClientId, ((PlayerPositionPacket) p).position);
            } else if(p is ItemSpawnPacket) {
                // ItemSpawnPacket.ProcessServer: a new network id, echoed to the spawner with its own clientsideId, and ownership
                ItemSpawnPacket item = (ItemSpawnPacket) p;
                int id = Interlocked.Increment(ref nextItemId);
                var answer = new ItemSpawnPacket { itemId = id, type = item.type, category = item.category, clientsideId = item.clientsideId,
                                                   position = item.position, rotation = item.rotation, isMagicProjectile = item.isMagicProjectile };
                server.SendTo(c, answer);
                server.SendTo(c, new ItemOwnerPacket { itemId = id, owning = true });
                watch.OnItemSpawn(answer, false);
            } else if(p is ItemPositionPacket) {
                watch.OnItemMoved((ItemPositionPacket) p);
            }
        }

        private int dropped; // 1 once the download was cut off (dropAtPercent)
        private int creatureSent;

        // Cuts the joiner off in the middle of its download, off the upload thread
        private void Drop(ClientInformation c, long sent, long total) {
            if(Interlocked.Exchange(ref dropped, 1) == 1) return;
            new Thread(() => {
                Out.Info("DROP at " + sent + " of " + total + " bytes (" + (sent * 100 / Math.Max(1, total)) + "%), " + x.dropHow);
                if(x.dropHow == "quit") {
                    Stop();
                    Thread.Sleep(5000);
                    Start();
                    Out.Info("hosting again after the drop");
                } else {
                    // Like a lost network: the TCP connection is reset, no goodbye
                    var connections = (System.Collections.IDictionary) typeof(IPServer).GetField("tcpConnections", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(server);
                    object socket = connections[c.ClientId];
                    var tcp = (System.Net.Sockets.TcpClient) socket.GetType().GetProperty("client").GetValue(socket);
                    tcp.Client.LingerState = new System.Net.Sockets.LingerOption(true, 0);
                    tcp.Client.Close(0);
                    Upload upload; // the server side of a reset doesn't always raise OnDisconnect: the upload stops here
                    if(uploads.TryRemove(c.ClientId, out upload)) Cancel(upload);
                }
            }) { IsBackground = true }.Start();
        }

        private void OnRequest(ClientInformation c, int requestId, ModsRequest request) {
            if(request == null || request.folders == null) return;
            List<ModEntry> mods = request.folders.Distinct().Select(f => hostMods.FirstOrDefault(m => m.folder == f)).Where(m => m != null).ToList();
            Out.Info("client " + c.ClientId + " requests " + string.Join(", ", request.folders));
            foreach(string folder in request.folders.Intersect(x.notServed)) list.Fail("the joiner asked the host for " + folder + ", which must come from mod.io");
            var upload = new Upload();
            uploads[c.ClientId] = upload;
            new Thread(() => Send(c, requestId, upload, mods)) { IsBackground = true, Name = "upload " + c.ClientId }.Start();
        }

        // Same protocol as ModTransferHost.Upload: manifest, chunks with an ack window, end; READY_NEXT before the next mod
        private void Send(ClientInformation c, int requestId, Upload upload, List<ModEntry> mods) {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long bytes = 0, sent = 0;
            long total = mods.Sum(m => TransferRules.ListTransferFiles(Path.Combine(modsFolder, m.folder)).Sum(f => f.size));
            try {
                foreach(ModEntry mod in mods) {
                    if(upload.cancelled) { Out.Info("upload to client " + c.ClientId + " cancelled after " + bytes + " bytes, " + clock.Elapsed.TotalSeconds.ToString("0.0") + " s"); return; }
                    string folder = Path.Combine(modsFolder, mod.folder);
                    var manifest = new TransferManifest {
                        requestId = requestId, transferId = Interlocked.Increment(ref nextTransferId),
                        folder = mod.folder, hash = mod.hash, modioId = mod.modioId, files = TransferRules.ListTransferFiles(folder),
                    };
                    var sender = new TransferSender(manifest, folder, Window, 0,
                        (file, offset, data) => {
                            server.SendTo(c, new ModFilePacket(manifest.transferId, file, offset, Convert.ToBase64String(data)));
                            sent += data.Length;
                            if(x.dropAtPercent > 0 && sent * 100 >= total * x.dropAtPercent) Drop(c, sent, total);
                        });
                    upload.transferId = manifest.transferId;
                    upload.sender = sender;
                    foreach(ModSyncPacket packet in ModSyncPacket.Create(ModSyncPacket.TRANSFER_START, JsonConvert.SerializeObject(manifest))) server.SendTo(c, packet);
                    bool ok = sender.Run();
                    if(upload.cancelled) return;
                    End(c, requestId, manifest.transferId, ok, ok ? "" : "Sending " + mod.name + " was interrupted.");
                    if(!ok) { list.Fail("sending " + mod.folder + " failed (no acks?)"); return; }
                    list.Mark("served " + mod.folder, manifest.files.Count + " files, " + manifest.TotalBytes + " bytes");
                    bytes += manifest.TotalBytes;
                    if(mod != mods[mods.Count - 1] && !upload.readyNext.Wait(ReadyWaitMs)) { list.Fail("no READY_NEXT after " + mod.folder); return; }
                }
                double sec = Math.Max(0.001, clock.Elapsed.TotalSeconds);
                Out.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture, "SERVE SUMMARY {0} mods, {1:0.0} MB in {2:0.0} s = {3:0.00} MB/s", mods.Count, bytes / 1048576.0, sec, bytes / 1048576.0 / sec));
            } catch(Exception e) {
                list.Fail("upload threw " + e);
            }
        }

        private void End(ClientInformation c, int requestId, int transferId, bool ok, string error) {
            string json = JsonConvert.SerializeObject(new TransferEnd { requestId = requestId, transferId = transferId, ok = ok, error = error });
            foreach(ModSyncPacket packet in ModSyncPacket.Create(ModSyncPacket.TRANSFER_END, json)) server.SendTo(c, packet);
        }

        private static void Cancel(Upload upload) {
            upload.cancelled = true;
            upload.sender?.Cancel();
            upload.readyNext.Release();
        }
    }
}
