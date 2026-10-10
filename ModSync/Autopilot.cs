using AMP.Data;
using AMP.Logging;
using AMP.Network.Data.Sync;
using AMP.Overlay;
using System;
using System.Collections;
using System.IO;
using System.Linq;
using ThunderRoad;
using UnityEngine;
using ClientType = AMP.Network.Client.Client;
using Debug = UnityEngine.Debug;

namespace AMP.ModSync {
    // Scripted in-game tests (docs/superpowers/plans/2026-10-10-e2e-harness.md). Does nothing unless the game was started by the test harness:
    // the boot hook set AppDomain data AMP_TEST_ISOLATED (isolated save folder) AND the command line has "-amp-autopilot <absolute path to script.json>".
    // Runs on the main thread as one coroutine. Everything it says starts with "AMPTEST " so the harness can grep the Unity log:
    //   AMPTEST STEP <n> <name> / AMPTEST OK <n> / AMPTEST FAIL <n> <reason> (exit code 3) / AMPTEST DONE (exit code 0)
    // n is the step's index in the script (from 0); script or state problems before the first step report n = -1.
    // Progress is saved in <AMP_TEST_ISOLATED>\autopilot-state.json after every step, so the game relaunched by a mod swap continues at the next step.
    internal static class Autopilot {
        private const string Prefix    = "AMPTEST ";
        private const string Flag      = "-amp-autopilot";
        private const string StateFile = "autopilot-state.json";
        private const int    ExitCodeOnFail = 3;

        internal static bool Active { get; private set; }

        private static string failure;      // set by a step to fail itself
        private static int    restartStep = -1; // the step that closes the game on purpose (downloadAndRestart)

        private static WaitForSecondsRealtime Poll { get { return new WaitForSecondsRealtime(0.25f); } }

        internal static void StartIfRequested(MonoBehaviour host) {
            string scriptPath = AutopilotScript.FindArg(Environment.GetCommandLineArgs(), Flag);
            if(scriptPath == null) return;
            object isolated = AppDomain.CurrentDomain.GetData("AMP_TEST_ISOLATED");
            if(isolated == null) { Log.Warn(Defines.AMP, "Autopilot ignored: this is not an isolated test run."); return; }
            Active = true;
            host.StartCoroutine(Run(isolated.ToString(), scriptPath));
        }

        // The harness started the game through Steam with these arguments; a relaunch after a mod swap has to do the same
        internal static string RelaunchSpec() {
            return HelperScript.RelaunchSpec(SteamExe(), RelaunchArguments());
        }

        private static string[] RelaunchArguments() {
            return HelperScript.SteamRelaunchArguments(HelperScript.SteamAppId, Environment.GetCommandLineArgs());
        }

        private static string SteamExe() {
            const string fallback = @"C:\Program Files (x86)\Steam\steam.exe";
            try {
                string root = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
                string exe = string.IsNullOrEmpty(root) ? null : Path.GetFullPath(Path.Combine(root, "steam.exe"));
                if(exe != null && File.Exists(exe)) return exe;
            } catch(Exception e) {
                Log.Warn(Defines.AMP, "Autopilot: Steam's location wasn't found in the registry. " + e.Message);
            }
            return fallback;
        }

        #region Runner

        private static IEnumerator Run(string isolatedFolder, string scriptPath) {
            yield return null; // Initialize has finished its synchronous part

            string text = null, error = null, problem = null;
            try {
                if(!Path.IsPathRooted(scriptPath)) throw new IOException("the script path must be absolute: " + scriptPath);
                if(!Directory.Exists(isolatedFolder)) throw new IOException("the isolated save folder doesn't exist: " + isolatedFolder);
                text = File.ReadAllText(scriptPath);
            } catch(Exception e) {
                problem = "can't read the script: " + e.Message;
            }
            if(problem != null) { Abort(-1, problem); yield break; }
            AutopilotScript script = AutopilotScript.Parse(text, out error);
            if(script == null) { Abort(-1, "invalid script: " + error); yield break; }

            string statePath = Path.Combine(isolatedFolder, StateFile);
            AutopilotState state = AutopilotState.Load(statePath, AutopilotState.HashOf(text));
            Application.quitting += () => { if(restartStep >= 0) Debug.Log(Prefix + "OK " + restartStep + " (closing for the restart)"); };
            Debug.Log(Prefix + "START " + script.Steps.Count + " steps, continuing at step " + state.step);
            Debug.Log(Prefix + "INFO relaunch arguments: " + HelperScript.QuoteArguments(RelaunchArguments()));

            // The same things the book needs before it can host or join
            float readyBy = Time.realtimeSinceStartup + AutopilotScript.DefaultTimeoutSec;
            while(ModManager.instance == null || ModManager.instance.UserData == null || ModManager.guiManager == null || ModManager.safeFile == null) {
                if(Time.realtimeSinceStartup > readyBy) { Abort(state.step, "AMP didn't finish starting up"); yield break; }
                yield return Poll;
            }

            while(state.step < script.Steps.Count) {
                int n = state.step;
                AutopilotStep step = script.Steps[n];
                Debug.Log(Prefix + "STEP " + n + " " + step.Name);
                failure = null;

                IEnumerator body = Execute(n, step, state, statePath);
                float deadline = Time.realtimeSinceStartup + step.TimeoutSec;
                bool more = true;
                while(more) {
                    try {
                        more = body.MoveNext();
                    } catch(Exception e) {
                        failure = e.GetType().Name + ": " + e.Message;
                    }
                    if(failure != null || !more) break;
                    if(Time.realtimeSinceStartup > deadline) { failure = "timed out after " + step.TimeoutSec + " s"; break; }
                    yield return body.Current;
                }

                if(failure != null) {
                    if(state.step != n) { state.step = n; state.Save(statePath); } // a failed restart didn't happen
                    Abort(n, failure);
                    yield break;
                }
                state.step = n + 1;
                state.Save(statePath);
                Debug.Log(Prefix + "OK " + n);
            }

            Debug.Log(Prefix + "DONE");
            Application.Quit(0);
        }

        private static void Abort(int n, string reason) {
            Debug.Log(Prefix + "FAIL " + n + " " + (reason ?? "").Replace("\r", " ").Replace("\n", " "));
            Application.Quit(ExitCodeOnFail);
        }

        private static IEnumerator Execute(int n, AutopilotStep step, AutopilotState state, string statePath) {
            switch(step.Name) {
                case "waitSpawn":           return WaitSpawn();
                case "host":                return Host(step);
                case "join":                return Join(step);
                case "waitPlayers":         return WaitPlayers(step);
                case "expectRemotePlayers": return ExpectRemotePlayers(step);
                case "expectRemotePlayerMoved": return ExpectRemotePlayerMoved(step);
                case "spawnItem":           return SpawnItem(step);
                case "expectItemSynced":    return ExpectItemSynced(step);
                case "updateAmp":           return UpdateAmp(n, state, statePath);
                case "expectAmpVersion":    return ExpectAmpVersion(step);
                case "expectModSync":       return ExpectModSync(step);
                case "downloadAndRestart":  return DownloadAndRestart(n, step, state, statePath);
                case "expectRejoined":      return ExpectRejoined(state);
                case "expectModFolder":     return ExpectModFolder(step);
                case "setOption":           return SetOption(step);
                case "expectOption":        return ExpectOption(step);
                case "expectProfileActive": return ExpectProfileActive(step);
                case "disconnect":          return Disconnect();
                case "wait":                return Wait(step);
                case "modioGetMod":         return ModioGetMod(step);
                case "modioSearch":         return ModioSearch(step);
                case "modioDownload":       return ModioDownload(step);
                case "expectMatch":         return ExpectMatch(step);
                case "expectUnavailable":   return ExpectUnavailable(step);
                case "expectCachedModio":   return ExpectCachedModio(step);
                case "killGame":            return KillGame(n, state, statePath);
                case "expectStartupRestore": return ExpectStartupRestore(step);
                case "quit":                return Nothing(); // the end of the script reports DONE and quits
                default: throw new InvalidOperationException("unknown step " + step.Name);
            }
        }

        #endregion

        #region Steps

        private static IEnumerator Nothing() { yield break; }

        private static IEnumerator WaitSpawn() {
            while(!ModSwapClient.InPlayableLevel()) yield return Poll;
        }

        private static IEnumerator Host(AutopilotStep step) {
            string hostError = null;
            GUIManager.HostServer((uint) step.Int("maxPlayers", 4), step.Int("port").ToString(), step.Str("password", ""), e => hostError = e);
            // HostServer joins itself as the first player; done when that connection has been greeted
            while(!Greeted()) {
                if(hostError != null) { failure = "hosting failed: " + hostError; yield break; }
                yield return Poll;
            }
        }

        private static IEnumerator Join(AutopilotStep step) {
            GUIManager.JoinServer(step.Str("address"), step.Int("port").ToString(), step.Str("password", ""), false);
            ClientType client = ModManager.clientInstance;
            if(client == null) { failure = "joining didn't start (invalid address or port?)"; yield break; }

            // In: greeted by the host, or the host's mod list arrived (then the mod sync decides what happens next)
            while(!(Greeted() || (Connected() && ModSyncClient.Plan != null))) {
                if(ModManager.clientInstance != client) { failure = "the connection closed before joining finished"; yield break; }
                yield return Poll;
            }
        }

        private static IEnumerator WaitPlayers(AutopilotStep step) {
            while(true) {
                Network.Server.Server server = ModManager.serverInstance;
                if(server == null) { failure = "not hosting"; yield break; }
                if(server.Clients.Count(c => c.greeted) >= step.Int("count")) yield break;
                yield return Poll;
            }
        }

        // Remote players (not us) whose avatar has been spawned in our game
        private static System.Collections.Generic.List<PlayerNetworkData> RemoteAvatars() {
            var result = new System.Collections.Generic.List<PlayerNetworkData>();
            ClientType client = ModManager.clientInstance;
            if(client == null || client.netclient == null || ModManager.clientSync == null) return result;
            foreach(PlayerNetworkData pnd in ModManager.clientSync.syncData.players.Values) {
                if(pnd.clientId != client.netclient.ClientId && pnd.creature != null && !pnd.isSpawning) result.Add(pnd);
            }
            return result;
        }

        private static IEnumerator ExpectRemotePlayers(AutopilotStep step) {
            while(RemoteAvatars().Count < step.Int("count")) {
                if(!Connected()) { failure = "not connected"; yield break; }
                yield return Poll;
            }
            foreach(PlayerNetworkData pnd in RemoteAvatars()) Debug.Log(Prefix + "INFO remote player " + pnd.clientId + " " + pnd.name + " (" + pnd.creatureId + ") at " + pnd.creature.transform.position);
        }

        private static IEnumerator ExpectRemotePlayerMoved(AutopilotStep step) {
            var start = new System.Collections.Generic.Dictionary<int, Vector3>();
            while(true) {
                if(!Connected()) { failure = "not connected"; yield break; }
                foreach(PlayerNetworkData pnd in RemoteAvatars()) {
                    Vector3 now = pnd.creature.transform.position;
                    Vector3 first;
                    if(!start.TryGetValue(pnd.clientId, out first)) { start[pnd.clientId] = now; continue; }
                    float moved = Vector3.Distance(first, now);
                    if(moved >= step.Int("minDistance")) {
                        Debug.Log(Prefix + "INFO remote player " + pnd.clientId + " moved " + moved.ToString("0.00") + " m (" + first + " -> " + now + ")");
                        yield break;
                    }
                }
                yield return Poll;
            }
        }

        private static IEnumerator SpawnItem(AutopilotStep step) {
            ItemData data = Catalog.GetData<ItemData>(step.Str("dataId"), false);
            if(data == null) { failure = "no item " + step.Str("dataId") + " in the catalog"; yield break; }
            if(Player.currentCreature == null) { failure = "no player to spawn the item at"; yield break; }
            Transform body = Player.currentCreature.transform;
            Vector3 at = body.position + body.forward * 1f + Vector3.up * 1.5f; // in the air: it falls, so its position gets synced too
            Item spawned = null;
            bool done = false;
            data.SpawnAsync(item => { spawned = item; done = true; }, at, Quaternion.identity);
            while(!done) yield return Poll;
            if(spawned == null) { failure = "spawning " + step.Str("dataId") + " failed"; yield break; }
            // Synced: the server answered with a network id for it
            while(true) {
                if(!Connected()) { failure = "not connected"; yield break; }
                ItemNetworkData ind = ModManager.clientSync.syncData.items.Values.FirstOrDefault(i => i.clientsideItem == spawned && i.networkedId > 0);
                if(ind != null) { Debug.Log(Prefix + "INFO spawned " + ind.dataId + " has network id " + ind.networkedId); yield break; }
                yield return Poll;
            }
        }

        private static IEnumerator ExpectItemSynced(AutopilotStep step) {
            while(true) {
                if(!Connected()) { failure = "not connected"; yield break; }
                ItemNetworkData ind = ModManager.clientSync.syncData.items.Values.FirstOrDefault(i => i.dataId == step.Str("dataId") && i.networkedId > 0 && i.clientsideId <= 0 && i.clientsideItem != null);
                if(ind != null) { Debug.Log(Prefix + "INFO item " + ind.dataId + " (" + ind.networkedId + ") is in the game at " + ind.clientsideItem.transform.position); yield break; }
                yield return Poll;
            }
        }

        // The startup check (ReleaseUpdater.CheckRoutine, 10 s after start) finds the release; then the same journal + helper swap as a
        // mod download replaces AMP and relaunches the game. Like downloadAndRestart, progress is saved before the game quits.
        private static IEnumerator UpdateAmp(int n, AutopilotState state, string statePath) {
            float giveUp = Time.realtimeSinceStartup + 60f;
            while(ReleaseUpdater.Available == null) {
                if(Time.realtimeSinceStartup > giveUp) { failure = "no newer AMP release was found (this is " + Defines.COMMUNITY_VERSION + "; " + (ReleaseUpdater.CannotUpdateReason() ?? "check the log for the update check") + ")"; yield break; }
                yield return Poll;
            }
            string reason = ReleaseUpdater.CannotUpdateReason();
            if(reason != null) { failure = "can't update: " + reason; yield break; }
            string unsafeRelaunch = AutopilotScript.IsolationProblem(RelaunchArguments());
            if(unsafeRelaunch != null) { failure = "refusing to restart, the relaunch wouldn't be isolated: " + unsafeRelaunch; yield break; }
            Debug.Log(Prefix + "INFO updating " + Defines.COMMUNITY_VERSION + " -> " + ReleaseUpdater.Available.version);

            state.values["restartedFrom"] = n.ToString();
            state.step = n + 1;
            state.Save(statePath);
            restartStep = n;
            ReleaseUpdater.UpdateNow();
            while(true) {
                if(!ReleaseUpdater.Busy) { restartStep = -1; failure = "the update stopped: " + ReleaseUpdater.Status; yield break; }
                yield return Poll;
            }
        }

        private static IEnumerator ExpectAmpVersion(AutopilotStep step) {
            string folder = ModSwapClient.FindAmpFolder();
            string manifest = folder == null ? null : ModInventory.ReadEntry(folder, new string[0])?.version;
            Debug.Log(Prefix + "INFO running AMP " + Defines.COMMUNITY_VERSION + " from " + folder + " (manifest version " + manifest + ")");
            if(Defines.COMMUNITY_VERSION != step.Str("version")) failure = "AMP is " + Defines.COMMUNITY_VERSION + ", expected " + step.Str("version");
            yield break;
        }

        private static IEnumerator ExpectModSync(AutopilotStep step) {
            while(ModSyncClient.Plan == null) {
                if(ModManager.clientInstance == null) { failure = "not connected, no mod plan will arrive"; yield break; }
                yield return Poll;
            }
            SyncPlan plan = ModSyncClient.Plan;
            Debug.Log(Prefix + "INFO plan blocksJoin=" + plan.BlocksJoin + ": " + plan.Describe().Replace("\r", "").Replace("\n", " | "));
            if(plan.BlocksJoin != step.Bool("blocks")) failure = "expected the plan to " + (step.Bool("blocks") ? "block" : "not block") + " joining";
        }

        // The game quits while this step runs: its progress is saved first so the next start continues after it
        private static IEnumerator DownloadAndRestart(int n, AutopilotStep step, AutopilotState state, string statePath) {
            SwapMode mode = (SwapMode) Enum.Parse(typeof(SwapMode), step.Str("mode"));
            while(ModSyncClient.Plan == null) {
                if(ModManager.clientInstance == null) { failure = "not connected, nothing to download"; yield break; }
                yield return Poll;
            }
            if(!ModSwapClient.CanDownload(ModSyncClient.Plan)) { failure = "the plan can't be downloaded (not a Windows host, or nothing to fetch)"; yield break; }
            // The relaunched game must be an isolated test again, or it would run on the player's real saves
            string unsafeRelaunch = AutopilotScript.IsolationProblem(RelaunchArguments());
            if(unsafeRelaunch != null) { failure = "refusing to restart, the relaunch wouldn't be isolated: " + unsafeRelaunch; yield break; }

            state.values["restartedFrom"] = n.ToString();
            state.step = n + 1;
            state.Save(statePath);
            restartStep = n;

            while(true) {
                if(!ModSwapClient.Busy) {
                    if(!string.IsNullOrEmpty(ModSwapClient.LastError)) { restartStep = -1; failure = "download failed: " + ModSwapClient.LastError; yield break; }
                    ModSwapClient.DownloadAndRestart(mode); // ignored while the release updater or the retry cool-down is busy: tried again
                }
                yield return Poll;
            }
        }

        // Like Android ending the app: no quit event, so nothing gets undone on the way out. The runner starts the game again.
        private static IEnumerator KillGame(int n, AutopilotState state, string statePath) {
            state.step = n + 1;
            state.Save(statePath);
            Debug.Log(Prefix + "OK " + n + " (closing for the restart)");
            yield return new WaitForSecondsRealtime(1f); // the log line reaches the file
            System.Diagnostics.Process.GetCurrentProcess().Kill();
            yield return new WaitForSecondsRealtime(10f);
            failure = "the game is still running after killing itself";
        }

        private static IEnumerator ExpectStartupRestore(AutopilotStep step) {
            if(!ModSwapClient.RestoredAtStartup) { failure = "this start didn't undo a swap in-game"; yield break; }
            if(!ModSyncPaths.Available || Directory.Exists(Path.Combine(ModSyncPaths.ModsFolder, step.Str("folder")))) { failure = "Mods/" + step.Str("folder") + " is still there"; yield break; }
            while(!ModSwapClient.ClosingForRestart) yield return Poll;
            Debug.Log(Prefix + "INFO the game closes by itself to load the restored mods");
        }

        private static IEnumerator ExpectRejoined(AutopilotState state) {
            if(!state.values.ContainsKey("restartedFrom")) { failure = "the game hasn't been restarted by this script (no downloadAndRestart before)"; yield break; }
            if(!ModSwapClient.RejoinStarted) { failure = "this start didn't rejoin by itself (the swap journal has no pending rejoin)"; yield break; }
            while(!(Greeted() && PlanEmpty())) yield return Poll;
            yield return new WaitForSecondsRealtime(3f); // the client tells the host its level right after the greeting; a quit before that looks like a failed join
        }

        private static IEnumerator ExpectModFolder(AutopilotStep step) {
            if(!ModSyncPaths.Available) { failure = "the Mods folder is unknown"; yield break; }
            bool present = Directory.Exists(Path.Combine(ModSyncPaths.ModsFolder, step.Str("folder")));
            if(present != step.Bool("present")) failure = "Mods/" + step.Str("folder") + (present ? " exists" : " doesn't exist");
            yield break;
        }

        private static IEnumerator SetOption(AutopilotStep step) {
            while(!ModOptionBridge.OptionsLoaded()) yield return Poll;
            ModOption option;
            int index;
            failure = ResolveOption(step, out option, out index);
            if(failure != null) yield break;

            ModOptionBridge.Apply(option, index);
            Debug.Log(Prefix + "INFO set " + step.Str("mod") + "/" + step.Str("option") + " to " + step.Str("title"));
            failure = CheckOption(option, step);
        }

        private static IEnumerator ExpectOption(AutopilotStep step) {
            while(!ModOptionBridge.OptionsLoaded()) yield return Poll;
            ModOption option;
            int index;
            failure = ResolveOption(step, out option, out index) ?? CheckOption(option, step);
            yield break;
        }

        private static string ResolveOption(AutopilotStep step, out ModOption option, out int index) {
            option = ModOptionBridge.Find(step.Str("mod"), step.Str("option"));
            index = -1;
            if(option == null) return "option " + step.Str("mod") + "/" + step.Str("option") + " not found";
            string[] titles = ModOptionBridge.Titles(option);
            index = Array.IndexOf(titles, step.Str("title"));
            return index < 0 ? "option " + step.Str("option") + " has no value titled \"" + step.Str("title") + "\" (has: " + string.Join(", ", titles) + ")" : null;
        }

        private static string CheckOption(ModOption option, AutopilotStep step) {
            string now = ModOptionBridge.Read(option).title;
            return now == step.Str("title") ? null : "option " + step.Str("option") + " is \"" + now + "\", expected \"" + step.Str("title") + "\"";
        }

        private static IEnumerator ExpectProfileActive(AutopilotStep step) {
            if(ProfileSwitch.Active != step.Bool("active")) failure = "multiplayer profile is " + (ProfileSwitch.Active ? "active" : "not active");
            yield break;
        }

        private static IEnumerator Disconnect() {
            ModManager.Stop();
            while(ModManager.clientInstance != null || ModManager.serverInstance != null) yield return Poll;
        }

        private static IEnumerator Wait(AutopilotStep step) {
            yield return new WaitForSecondsRealtime(step.Int("seconds"));
        }

        private static string DescribeModio(ModIO.ModProfile p) {
            ModIO.Implementation.API.Objects.Modfile file = p.modfile;
            return p.id.id + " \"" + p.name + "\" by " + p.creator.username + ", file " + file.id + " (" + file.filename + ", " + file.filesize + " bytes, version " + file.version
                 + "), platforms: " + string.Join(", ", (p.platforms ?? new ModIO.Implementation.API.Objects.ModPlatform[0]).Select(x => x.platform + "=" + x.modfileLive))
                 + " -> " + ModioClient.PlatformAvailability(p);
        }

        private static IEnumerator ModioGetMod(AutopilotStep step) {
            ModIO.ModProfile? profile = null;
            string error = null;
            bool answered = false;
            ModioClient.GetMod(step.Int("id"), (p, e) => { profile = p; error = e; answered = true; });
            while(!answered) yield return Poll;
            if(error != null) { failure = "mod.io GetMod failed: " + error; yield break; }
            Debug.Log(Prefix + "INFO mod.io mod " + DescribeModio(profile.Value));
            if(profile.Value.name != step.Str("expectName")) failure = "mod.io mod " + step.Int("id") + " is \"" + profile.Value.name + "\", expected \"" + step.Str("expectName") + "\"";
        }

        private static IEnumerator ModioSearch(AutopilotStep step) {
            System.Collections.Generic.List<ModIO.ModProfile> found = null;
            string error = null;
            bool answered = false;
            ModioClient.Search(step.Str("phrase"), (list, e) => { found = list; error = e; answered = true; });
            while(!answered) yield return Poll;
            if(error != null) { failure = "mod.io search failed: " + error; yield break; }
            Debug.Log(Prefix + "INFO mod.io search \"" + step.Str("phrase") + "\": " + found.Count + " results: " + string.Join("; ", found.Take(5).Select(p => p.id.id + " \"" + p.name + "\" by " + p.creator.username + " (" + ModioClient.PlatformAvailability(p) + ")")));
            if(!found.Any(p => p.id.id == step.Int("expectId"))) failure = "the search didn't find mod " + step.Int("expectId");
        }

        // Like a cross-platform delivery: the download goes into the mod cache under its own hash, with its mod.io ids
        private static IEnumerator ModioDownload(AutopilotStep step) {
            if(!ModSyncPaths.Available) { failure = "the mod cache folder is unknown"; yield break; }
            var cache = new ModCache(ModSyncPaths.CacheFolder);
            ModioClient.DownloadResult download = null;
            float lastLogged = -1f;
            string crashed = null;
            yield return ModioClient.Guarded(ModioClient.Download(step.Int("id"), cache, 512L * 1024 * 1024,
                p => { if(p - lastLogged >= 0.25f) { lastLogged = p; Debug.Log(Prefix + "INFO mod.io download " + (int) (p * 100) + "%"); } },
                () => true, r => download = r), e => crashed = e.GetType().Name + ": " + e.Message);
            if(download == null) { failure = "the download crashed: " + crashed; yield break; }
            if(download.error != null) { failure = download.error; yield break; }

            string folderName = Path.GetFileName(download.folder);
            var files = Directory.GetFiles(download.folder, "*", SearchOption.AllDirectories).Select(f => ModInventory.RelativePath(download.folder, f)).ToList();
            Debug.Log(Prefix + "INFO mod.io file " + download.fileId + " (" + download.fileName + ", version " + download.version + ") unpacked to folder " + folderName + ": " + string.Join(", ", files));
            var expected = new ModEntry { folder = folderName, name = folderName, modioId = step.Int("id") };
            CacheEntry entry = cache.CommitUnverified(download.folder, expected, download.fileId);
            ModioClient.TryDelete(download.root);
            if(entry == null) { failure = "the download couldn't be stored in the mod cache"; yield break; }
            string content = cache.ContentFolder(entry.hash);
            Debug.Log(Prefix + "INFO cached as " + entry.hash + " (" + entry.sizeBytes + " bytes, modioId " + entry.modioId + ", modioFileId " + entry.modioFileId + ")");
            if(!File.Exists(Path.Combine(content, "manifest.json"))) failure = "the cached mod has no manifest.json";
            else if(step.Str("expectFile", null) != null && !File.Exists(Path.Combine(content, step.Str("expectFile")))) failure = "the cached mod has no " + step.Str("expectFile");
            else if(Directory.GetFiles(content, "*", SearchOption.AllDirectories).Any(ModInventory.IsModioIdFile)) failure = "the cached mod has an id.modio";
            else if(entry.modioFileId != download.fileId || entry.modioId != step.Int("id")) failure = "the cache entry doesn't record the mod.io ids";
        }

        // Host: waits for the Quest matching of this hosting session, then checks one mod's result (what joiners get as questModioId)
        private static IEnumerator ExpectMatch(AutopilotStep step) {
            string folder = step.Str("folder");
            long expected = step.Int("modioId");
            bool confirm = step.Args.ContainsKey("confirm") && step.Bool("confirm");
            if(ModManager.serverInstance == null) { failure = "not hosting"; yield break; }
            while(!QuestMatcher.PassDone.IsCompleted && !(confirm && QuestMatcher.PendingFor(folder) != null)) yield return Poll;

            if(confirm) {
                QuestMatcher.Candidate candidate = QuestMatcher.PendingFor(folder);
                if(candidate == null) { failure = "no Quest match of " + folder + " is waiting for the host's answer (sent as " + QuestMatcher.QuestIdFor(folder) + ")"; yield break; }
                Debug.Log(Prefix + "INFO asking the host: " + candidate.modName + " -> mod.io " + candidate.modio.id + " \"" + candidate.modio.name + "\" by " + candidate.modio.author + "; answering Yes");
                if(candidate.modio.id != expected) { failure = "the match to confirm is mod.io " + candidate.modio.id + ", expected " + expected; yield break; }
                QuestMatcher.Answer(candidate, true);
            }
            ModMatch stored = QuestMatcher.StoredFor(folder);
            long sent = QuestMatcher.QuestIdFor(folder);
            Debug.Log(Prefix + "INFO Quest match of " + folder + ": " + (stored == null ? "nothing stored" : "mod.io " + stored.modioId + (stored.confirmed ? " (confirmed)" : "")) + ", joiners get questModioId " + sent);
            if(expected > 0 ? sent != expected : sent != -1) failure = "joiners get questModioId " + sent + " for " + folder + ", expected " + (expected > 0 ? expected.ToString() : "-1 (no Quest version)");
        }

        private static IEnumerator ExpectUnavailable(AutopilotStep step) {
            while(ModSyncClient.Plan == null) {
                if(ModManager.clientInstance == null) { failure = "not connected, no mod plan will arrive"; yield break; }
                yield return Poll;
            }
            if(!ModSyncClient.Plan.Of(SyncAction.Unavailable).Any(s => string.Equals(s.hostMod.folder, step.Str("folder"), StringComparison.OrdinalIgnoreCase)))
                failure = step.Str("folder") + " isn't listed as unavailable: " + ModSyncClient.Plan.Describe().Replace("\r", "").Replace("\n", " | ");
        }

        // The cache entry of a mod downloaded from mod.io is the file mod.io has live for that platform (read from the mod's platform list)
        private static IEnumerator ExpectCachedModio(AutopilotStep step) {
            if(!ModSyncPaths.Available) { failure = "the mod cache folder is unknown"; yield break; }
            string entries = Path.Combine(ModSyncPaths.CacheFolder, "mods");
            CacheEntry entry = (Directory.Exists(entries) ? Directory.GetFiles(entries, "entry.json", SearchOption.AllDirectories) : new string[0])
                .Select(f => { try { return Newtonsoft.Json.JsonConvert.DeserializeObject<CacheEntry>(File.ReadAllText(f)); } catch(Exception) { return null; } })
                .FirstOrDefault(e => e != null && e.folder == step.Str("folder") && e.modioFileId > 0);
            if(entry == null) { failure = "no cache entry from mod.io for " + step.Str("folder"); yield break; }

            ModIO.ModProfile? profile = null;
            string error = null;
            bool answered = false;
            ModioClient.GetMod(entry.modioId, (p, e) => { profile = p; error = e; answered = true; });
            while(!answered) yield return Poll;
            if(error != null) { failure = "mod.io GetMod failed: " + error; yield break; }
            Debug.Log(Prefix + "INFO cached " + step.Str("folder") + " is mod.io " + entry.modioId + " file " + entry.modioFileId + "; mod.io: " + DescribeModio(profile.Value));
            long live = (profile.Value.platforms ?? new ModIO.Implementation.API.Objects.ModPlatform[0])
                .Where(p => string.Equals(p.platform, step.Str("platform"), StringComparison.OrdinalIgnoreCase)).Select(p => p.modfileLive).FirstOrDefault();
            if(live <= 0) failure = "mod.io " + entry.modioId + " has no live file for " + step.Str("platform");
            else if(live != entry.modioFileId) failure = "the cached file is " + entry.modioFileId + ", the live " + step.Str("platform") + " file is " + live;
        }

        #endregion

        #region State of the connection

        private static bool Connected() {
            ClientType client = ModManager.clientInstance;
            return client != null && client.netclient != null && client.netclient.IsConnected;
        }

        // ServerInfoPacket is the first thing of the host's greeting (also the one sent to the host's own client)
        private static bool Greeted() {
            return Connected() && ModManager.clientSync != null && ModManager.clientSync.syncData.server_config != null;
        }

        // Nothing left to set up: mods this platform can't get (Quest) are only a warning
        private static bool PlanEmpty() {
            SyncPlan plan = ModSyncClient.Plan;
            return plan == null || (plan.steps.All(s => s.action == SyncAction.Unavailable) && !plan.loadOrderDiffers);
        }

        #endregion
    }
}
