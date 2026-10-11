using AMP.Data;
using AMP.Logging;
using AMP.Network.Data;
using AMP.Network.Packets.Implementation;
using AMP.Threading;
using Netamite.Server.Data;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ServerType = AMP.Network.Server.Server;

namespace AMP.ModSync {
    // Server side: sends every joiner the host's mod list and holds back the normal greeting until the joiner answers.
    internal static class ModSyncHost {
        private const int REPORT_TIMEOUT_MS = 60 * 1000;      // a joiner without mod sync never answers
        private const int SETUP_TIMEOUT_MS  = 30 * 60 * 1000; // time a joiner gets to look at / set up mods
        private const int SETUP_STALL_MS    = 2 * 60 * 1000;  // ...longer while its download from this host still moves (acknowledged within this)
        private const int SETUP_RECHECK_MS  = 60 * 1000;      // how often a moving download's joiner is checked again after that
        internal const string TestSetupTimeoutFlag = "-amp-test-setup-timeout"; // seconds; honoured only in isolated test runs
        private static int setupTimeoutMs = SETUP_TIMEOUT_MS;
        private const int CHECK_TIMEOUT_MS  = 15 * 60 * 1000; // time a joiner gets to scan its mods
        internal const int MAX_REPORT_PARTS = 10;             // a joiner's report is tiny, anything longer is not one
        private const int MATCH_WAIT_MS     = 30 * 1000;      // how long a Quest joiner's updated mod list waits for the Quest matching of a new hosting session

        // ClientData.modSyncState; every change goes through Transition so concurrent packets and timers can't both win
        private const string WAITING = "waiting";
        private const string CHECKING = "checking";
        private const string SETUP   = "setup";
        private const string READY   = "ready";
        private const string KICKED  = "kicked";

        private static Task<HostModSet> hostModSet;

        // What ModTransferHost may serve: the enabled mods of the last scan (null until it finished) and the folder they live in
        internal static volatile List<ModEntry> HostMods;
        internal static volatile string HostModsFolder;
        private static volatile int prepareGeneration;
        private static string mode = ModSyncMode.Off;
        private static readonly MessageAssembler assembler = new MessageAssembler();

        // Anything but a known mode counts as "warn", a typo must not silently turn the check off
        private static string NormalizeMode(string value) {
            string normalized = (value ?? "").Trim().ToLowerInvariant();
            if(normalized == ModSyncMode.Off || normalized == ModSyncMode.Warn || normalized == ModSyncMode.Enforce) return normalized;
            Log.Warn(Defines.SERVER, $"Mod sync: unknown modSyncMode \"{value}\" in the config (use off, warn or enforce), using \"warn\".");
            return ModSyncMode.Warn;
        }

        // Main thread, before the server starts: settings and paths are read here, the folder scan runs in the background
        internal static void Prepare() {
            hostModSet = null;
            QuestMatcher.Stop();
            int generation = ++prepareGeneration;
            HostMods       = null;
            HostModsFolder = null;
            mode = NormalizeMode(ModManager.safeFile.hostingSettings.modSyncMode);
            setupTimeoutMs = TestSetupTimeoutMs() ?? SETUP_TIMEOUT_MS;
            if(mode != ModSyncMode.Off && !ModSyncPaths.Available) {
                Log.Warn(Defines.SERVER, "Mod sync is unavailable (Mods folder not found), hosting without it.");
                mode = ModSyncMode.Off;
            }
            if(mode == ModSyncMode.Off) return;

            string modsFolder  = ModSyncPaths.ModsFolder;
            string cacheFolder = ModSyncPaths.CacheFolder;
            var set = new HostModSet {
                platform     = ModSyncPaths.Platform,
                mode         = mode,
                personalMods = (ModManager.safeFile.hostingSettings.personalMods ?? new string[0]).Where(name => !string.IsNullOrWhiteSpace(name)).ToArray(),
                gameVersion  = ModSyncPaths.GameVersion,
            };
            // Quest matching: only a PC host maps its (Nexus) mods to mod.io pages with a Quest file
            bool matchQuest = ModSyncPaths.Platform == "windows";
            int matchGeneration = matchQuest ? QuestMatcher.Begin() : 0;
            hostModSet = Task.Run(() => {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                List<ModEntry> mods = ModInventory.Scan(modsFolder, cacheFolder, true);
                set.mods      = mods.Where(m => m.enabled).ToList();
                if(generation == prepareGeneration) { // a scan of an earlier hosting session must not overwrite this one's list
                    HostModsFolder = modsFolder;
                    HostMods       = set.mods;
                    // Run ignores a generation that StopHost (or a new hosting session) ended in the meantime
                    if(matchQuest) Dispatcher.Enqueue(() => QuestMatcher.Run(set.mods, set.personalMods, matchGeneration));
                }
                set.loadOrder = ModInventory.ReadLoadOrder(modsFolder);
                ModEntry amp  = mods.FirstOrDefault(m => m.isAmp && m.enabled);
                set.ampBuild  = amp != null ? amp.hash : "";
                Log.Info(Defines.SERVER, $"Mod sync ({set.mode}): hosting with {set.mods.Count} mods ({set.mods.Sum(m => m.sizeBytes) / (1024 * 1024)} MB, checked in {clock.Elapsed.TotalSeconds:0.0} s).");
                return set;
            });
        }

        // Serialized per joiner: Quest matches found since hosting started (or confirmed by the host) are included
        private static string HostModSetJson(HostModSet set, bool questPending) {
            lock(set) {
                foreach(ModEntry mod in set.mods) mod.questModioId = QuestMatcher.QuestIdFor(mod.folder);
                set.questPending = questPending;
                return JsonConvert.SerializeObject(set);
            }
        }

        internal static void OnClientConnected(ClientData client) {
            ServerType server = ModManager.serverInstance;
            if(hostModSet == null) { // mod sync off: the old, delayed greeting
                server.GreetPlayer((ClientInformation) client);
                return;
            }

            // Timers and continuations below act on this server and mode only, never on a later hosting session
            string connectMode = mode;
            Transition(client, WAITING, "");
            hostModSet.ContinueWith(t => {
                if(t.IsFaulted) {
                    Log.Err(Defines.SERVER, "Mod sync: scanning the host's mods failed, joining without it. " + t.Exception.GetBaseException().Message);
                    Greet(server, client, WAITING);
                    return;
                }
                // Right away, also while mod.io is still being asked for Quest versions (the list says so): a joiner on another platform reports
                // its platform with "checking" and gets the list again when that is done (OnPacket); PC joiners don't need them and don't wait
                bool pending = QuestMatcher.Running;
                if(pending) Interlocked.Exchange(ref client.modSyncQuestResend, 1);
                SendHostModSet(server, client, t.Result, pending);
                Task.Delay(REPORT_TIMEOUT_MS).ContinueWith(_ => OnReportTimeout(server, client, connectMode));
            });
        }

        // The timeout after the first send (OnClientConnected) still decides what happens to a joiner this doesn't reach
        private static void SendHostModSet(ServerType server, ClientData client, HostModSet set, bool questPending) {
            if(!IsConnected(server, client)) return;
            try {
                foreach(ModSyncPacket packet in ModSyncPacket.Create(ModSyncPacket.HOST_MOD_SET, HostModSetJson(set, questPending))) {
                    server.netamiteServer.SendTo(client, packet);
                }
            } catch(Exception e) {
                Log.Err(Defines.SERVER, "Mod sync: couldn't send the mod list to " + client.ClientName + ". " + e.Message);
            }
        }

        // A joiner on another platform got the list before the Quest matching finished: it gets the list again once the matching is done
        // (or after MATCH_WAIT_MS anyway), exactly once
        private static void ResendWhenMatched(ServerType server, ClientData client, string platform) {
            if(string.Equals(platform, ModSyncPaths.Platform, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(platform)) return;
            if(Interlocked.Exchange(ref client.modSyncQuestResend, 0) != 1) return;
            Task<HostModSet> scan = hostModSet;
            if(scan == null || !scan.IsCompleted || scan.IsFaulted) return;
            Task.WhenAny(QuestMatcher.PassDone, Task.Delay(MATCH_WAIT_MS)).ContinueWith(_ => {
                if(!IsSettingUp(client) || ModManager.serverInstance != server) return; // already let in, or gone
                Log.Info(Defines.SERVER, $"Mod sync: sending {client.ClientName} ({platform}) the mod list with the Quest versions.");
                SendHostModSet(server, client, scan.Result, false);
            });
        }

        // Frees what a half-sent report of this client still occupies
        internal static void OnClientDisconnected(ClientData client) {
            assembler.Forget(client.ClientId + ":");
            ModTransferHost.OnClientDisconnected(client);
        }

        // Joiners that are checking or setting up their mods are the ones who may download them
        internal static bool IsSettingUp(ClientData client) {
            lock(client.modSyncLock) {
                return client.modSyncState == CHECKING || client.modSyncState == SETUP;
            }
        }

        // A joiner still going through mod sync is connected but not playing yet: no item/NPC migration, no lobby admin
        internal static bool IsPlaying(ClientData client) {
            lock(client.modSyncLock) {
                return client.modSyncState == "" || client.modSyncState == READY;
            }
        }

        internal static void OnPacket(ClientData client, ModSyncPacket packet) {
            if(packet.kind != ModSyncPacket.JOINER_REPORT) return;
            if(packet.parts < 1 || packet.parts > MAX_REPORT_PARTS) return;
            string json = assembler.Add(client.ClientId + ":" + packet.transferId, packet.part, packet.parts, packet.data);
            if(json == null) return;

            JoinerReport report;
            try {
                report = JsonConvert.DeserializeObject<JoinerReport>(json);
            } catch(JsonException) {
                return;
            }
            if(report == null) return;
            client.modSyncPlatform = report.platform ?? "";

            ServerType server = ModManager.serverInstance;
            if(report.status == JoinerStatus.Ready) {
                if(Greet(server, client, WAITING, CHECKING, SETUP) && !string.IsNullOrEmpty(report.detail)) {
                    Log.Info(Defines.SERVER, $"Mod sync: {client.ClientName} joins with differences: {report.detail}");
                }
            } else if(report.status == JoinerStatus.Checking) {
                if(!Transition(client, CHECKING, WAITING)) return;
                ResendWhenMatched(server, client, client.modSyncPlatform); // after the transition: a pass that's already done resends at once and needs CHECKING
                Task.Delay(CHECK_TIMEOUT_MS).ContinueWith(_ => OnCheckTimeout(server, client, mode));
            } else if(report.status == JoinerStatus.NeedsSetup) {
                if(!Transition(client, SETUP, WAITING, CHECKING)) {
                    // Repeated while the joiner downloads from mod.io (nothing the host sees otherwise): its setup is still moving
                    if(IsSettingUp(client)) Interlocked.Exchange(ref client.modSyncKeepAliveTicks, DateTime.UtcNow.Ticks);
                    return;
                }
                Log.Info(Defines.SERVER, $"Mod sync: {client.ClientName} ({report.platform}) needs to set up mods:\n{report.detail}");
                Task.Delay(setupTimeoutMs).ContinueWith(_ => OnSetupTimeout(server, client));
            }
            // Declined: the joiner disconnects by itself
        }

        // A big mod set over a slow upload can take longer than the setup time: a joiner whose download from this host still moves stays
        private static void OnSetupTimeout(ServerType server, ClientData client) {
            if(!IsConnected(server, client)) return;
            DateTime? moved = ModTransferHost.LastProgress(client);
            long keepAlive = Interlocked.Read(ref client.modSyncKeepAliveTicks);
            if(keepAlive > 0 && (!moved.HasValue || keepAlive > moved.Value.Ticks)) moved = new DateTime(keepAlive, DateTimeKind.Utc);
            if(TransferRules.DownloadStillMoving(DateTime.UtcNow, moved, TimeSpan.FromMilliseconds(SETUP_STALL_MS))) {
                if(IsSettingUp(client)) Task.Delay(SETUP_RECHECK_MS).ContinueWith(_ => OnSetupTimeout(server, client));
                return;
            }
            if(Transition(client, KICKED, SETUP)) client.Kick("Mod setup took too long.");
        }

        private static int? TestSetupTimeoutMs() {
            if(AppDomain.CurrentDomain.GetData("AMP_TEST_ISOLATED") == null) return null;
            string value = AutopilotScript.FindArg(Environment.GetCommandLineArgs(), TestSetupTimeoutFlag);
            int seconds;
            if(value == null || !int.TryParse(value, out seconds) || seconds < 1) return null;
            Log.Info(Defines.SERVER, "Mod sync: test run, joiners get " + seconds + " s to set up their mods.");
            return seconds * 1000;
        }

        private static void OnReportTimeout(ServerType server, ClientData client, string connectMode) {
            if(!IsConnected(server, client)) return;
            if(connectMode == ModSyncMode.Enforce) {
                if(Transition(client, KICKED, WAITING)) client.Kick("This server checks mods on join. Update AMP to join.");
            } else if(Greet(server, client, WAITING)) {
                Log.Info(Defines.SERVER, $"Mod sync: {client.ClientName} didn't answer (older AMP?), joining without mod check.");
            }
        }

        private static void OnCheckTimeout(ServerType server, ClientData client, string checkMode) {
            if(!IsConnected(server, client)) return;
            if(checkMode == ModSyncMode.Enforce) {
                if(Transition(client, KICKED, CHECKING)) client.Kick("Checking mods took too long.");
            } else if(Greet(server, client, CHECKING)) {
                Log.Info(Defines.SERVER, $"Mod sync: {client.ClientName} took too long to check its mods, joining without mod check.");
            }
        }

        // Moves the joiner from one of the given states to "ready" and greets it, exactly once
        private static bool Greet(ServerType server, ClientData client, params string[] from) {
            if(!IsConnected(server, client) || !Transition(client, READY, from)) return false;
            server.GreetPlayer(client, false);
            return true;
        }

        // Atomic check-and-set of the joiner's state: false if it isn't in one of the expected states
        private static bool Transition(ClientData client, string to, params string[] from) {
            lock(client.modSyncLock) {
                if(Array.IndexOf(from, client.modSyncState) < 0) return false;
                client.modSyncState = to;
                return true;
            }
        }

        // The client must still be connected to the very server that gated it
        private static bool IsConnected(ServerType server, ClientData client) {
            return server != null && ModManager.serverInstance == server && server.netamiteServer.GetClientById(client.ClientId) == client;
        }
    }
}
