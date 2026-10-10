using AMP.Data;
using AMP.Logging;
using AMP.Network.Data;
using AMP.Network.Packets.Implementation;
using Netamite.Server.Data;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ServerType = AMP.Network.Server.Server;

namespace AMP.ModSync {
    // Server side: sends every joiner the host's mod list and holds back the normal greeting until the joiner answers.
    internal static class ModSyncHost {
        private const int REPORT_TIMEOUT_MS = 60 * 1000;      // a joiner without mod sync never answers
        private const int SETUP_TIMEOUT_MS  = 30 * 60 * 1000; // time a joiner gets to look at / set up mods
        private const int CHECK_TIMEOUT_MS  = 15 * 60 * 1000; // time a joiner gets to scan its mods
        internal const int MAX_REPORT_PARTS = 10;             // a joiner's report is tiny, anything longer is not one

        // ClientData.modSyncState; every change goes through Transition so concurrent packets and timers can't both win
        private const string WAITING = "waiting";
        private const string CHECKING = "checking";
        private const string SETUP   = "setup";
        private const string READY   = "ready";
        private const string KICKED  = "kicked";

        private static Task<string> hostModSetJson;

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
            hostModSetJson = null;
            int generation = ++prepareGeneration;
            HostMods       = null;
            HostModsFolder = null;
            mode = NormalizeMode(ModManager.safeFile.hostingSettings.modSyncMode);
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
            };
            hostModSetJson = Task.Run(() => {
                List<ModEntry> mods = ModInventory.Scan(modsFolder, cacheFolder, true);
                set.mods      = mods.Where(m => m.enabled).ToList();
                if(generation == prepareGeneration) { // a scan of an earlier hosting session must not overwrite this one's list
                    HostModsFolder = modsFolder;
                    HostMods       = set.mods;
                }
                set.loadOrder = ModInventory.ReadLoadOrder(modsFolder);
                ModEntry amp  = mods.FirstOrDefault(m => m.isAmp);
                set.ampBuild  = amp != null ? amp.hash : "";
                Log.Info(Defines.SERVER, $"Mod sync ({set.mode}): hosting with {set.mods.Count} mods.");
                return JsonConvert.SerializeObject(set);
            });
        }

        internal static void OnClientConnected(ClientData client) {
            ServerType server = ModManager.serverInstance;
            if(hostModSetJson == null) { // mod sync off: the old, delayed greeting
                server.GreetPlayer((ClientInformation) client);
                return;
            }

            // Timers and continuations below act on this server and mode only, never on a later hosting session
            string connectMode = mode;
            Transition(client, WAITING, "");
            hostModSetJson.ContinueWith(t => {
                if(t.IsFaulted) {
                    Log.Err(Defines.SERVER, "Mod sync: scanning the host's mods failed, joining without it. " + t.Exception.GetBaseException().Message);
                    Greet(server, client, WAITING);
                    return;
                }
                if(!IsConnected(server, client)) return;
                try {
                    foreach(ModSyncPacket packet in ModSyncPacket.Create(ModSyncPacket.HOST_MOD_SET, t.Result)) {
                        server.netamiteServer.SendTo(client, packet);
                    }
                } catch(Exception e) {
                    // The timeout below still decides what happens to this joiner
                    Log.Err(Defines.SERVER, "Mod sync: couldn't send the mod list to " + client.ClientName + ". " + e.Message);
                }
                Task.Delay(REPORT_TIMEOUT_MS).ContinueWith(_ => OnReportTimeout(server, client, connectMode));
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
                Task.Delay(CHECK_TIMEOUT_MS).ContinueWith(_ => OnCheckTimeout(server, client, mode));
            } else if(report.status == JoinerStatus.NeedsSetup) {
                if(!Transition(client, SETUP, WAITING, CHECKING)) return;
                Log.Info(Defines.SERVER, $"Mod sync: {client.ClientName} ({report.platform}) needs to set up mods:\n{report.detail}");
                Task.Delay(SETUP_TIMEOUT_MS).ContinueWith(_ => {
                    if(IsConnected(server, client) && Transition(client, KICKED, SETUP)) client.Kick("Mod setup took too long.");
                });
            }
            // Declined: the joiner disconnects by itself
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
