using AMP.Data;
using AMP.Logging;
using AMP.Overlay;
using Netamite.Client.Implementation;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ThunderRoad;
using UnityEngine;
using ClientType = AMP.Network.Client.Client;
using Debug = UnityEngine.Debug;

namespace AMP.ModSync {
    // PC joiner: downloads the host's mods, closes the game, lets the helper swap the folders (JournalApplier, outside the game because files are locked),
    // reconnects on the next start, and puts the player's own mods back (Temporary swaps on quit, or through "Restore my mods").
    // Quest (android): no helper and no file locks, so the journal is applied and undone in-game; the game then closes and the player starts it
    // again (Android apps can't restart themselves). Mods of a PC host come from mod.io (the Quest builds the host mapped them to).
    // Everything here runs on the main thread. JournalApplier is never called in-game on PC (a PC test simulating android does, with JSON-only mods).
    internal static class ModSwapClient {
        private const float RETRY_COOLDOWN = 3f;   // the host ignores mod requests within 2 s of the previous one
        private const float LEVEL_WAIT_MAX = 600f; // how long to wait for the player to load into a level

        private static SwapJournal journal;        // what is on disk (null: no swap)
        private static bool   busy;
        private static int    attempt;             // changes when the connection resets, so a stale download callback can tell
        private static float  retryAt;

        internal static string LastError { get; private set; } = "";
        internal static bool   Busy      { get { return busy; } }

        internal static string JournalPath  { get { return Path.Combine(ModSyncPaths.CacheFolder, "session.json"); } }
        internal static string HelperFolder { get { return Path.Combine(ModSyncPaths.CacheFolder, "helper"); } }

        // A swap that can still be undone: show "Restore my mods"
        internal static bool CanRestore {
            get { return journal != null && (journal.state == SwapState.Applied || journal.state == SwapState.RestorePending); }
        }

        // Quest: the swap is applied in-game (no helper, no file locks there)
        internal static bool InGame { get { return ModSyncPaths.Platform == "android"; } }

        // PC with a Windows host: the host has every file (mod.io mods are fetched from mod.io first when it serves the host's version).
        // Quest: what the plan adds comes from mod.io (another platform's host) or from a Quest host; AMP itself is never swapped in-game.
        internal static bool CanDownload(SyncPlan plan) {
            HostModSet set = ModSyncClient.HostSet;
            if(plan == null || set == null || !ModSyncPaths.Available || !plan.BlocksJoin) return false;
            bool samePlatform = string.Equals(set.platform, ModSyncPaths.Platform, StringComparison.OrdinalIgnoreCase);
            List<SyncStep> fetch = plan.Of(SyncAction.Add).Concat(plan.Of(SyncAction.Replace)).ToList();
            if(fetch.Any(s => s.hostMod == null || string.IsNullOrEmpty(s.hostMod.folder))) return false;
            if(ModSyncPaths.Platform == "windows") return samePlatform && fetch.All(s => !string.IsNullOrEmpty(s.hostMod.hash));
            if(!InGame || fetch.Any(s => s.hostMod.isAmp)) return false;
            string host;
            int port;
            if(!RejoinAddress(out host, out port)) return false; // see CannotDownloadReason
            return fetch.All(s => s.source == "modio" ? s.modioId > 0 : samePlatform && !string.IsNullOrEmpty(s.hostMod.hash));
        }

        // Shown instead of the download buttons when the plan could be set up but not from this connection; null otherwise.
        // Quest: the game can't reopen itself, and a start that doesn't reconnect undoes the swap again (a swap the player never got to use),
        // so an in-game swap needs an address the next start can rejoin by itself.
        internal static string CannotDownloadReason(SyncPlan plan) {
            if(!InGame || plan == null || !plan.BlocksJoin || !ModSyncPaths.Available) return null;
            string host;
            int port;
            if(RejoinAddress(out host, out port)) return null;
            return "Setting up the host's mods needs a direct connection: join the host by IP address (Join > Direct (IP)) so the game can reconnect by itself after the restart.";
        }

        // Where the next start reconnects to; false when this connection can't be rejoined by address (Steam lobby, no port)
        private static bool RejoinAddress(out string host, out int port) {
            ClientType client = ModManager.clientInstance;
            bool isIp = client != null && client.netclient is IPClient && ModManager.guiManager != null;
            host = isIp ? ModManager.guiManager.join_ip : "";
            port = 0;
            if(isIp) int.TryParse(ModManager.guiManager.join_port, out port);
            return isIp && JournalPlanner.CanRejoin(host, port);
        }

        // A new connection starts: nothing of a half-finished download may leak into it. The journal stays.
        internal static void Reset() {
            attempt++;
            busy = false;
            LastError = ""; // an error of the previous connection's setup doesn't belong on the next host's page
        }

        #region Download & restart

        internal static void DownloadAndRestart(SwapMode mode) {
            if(busy || ReleaseUpdater.Busy || Time.realtimeSinceStartup < retryAt) return;
            SyncPlan plan = ModSyncClient.Plan;
            HostModSet set = ModSyncClient.HostSet;
            if(!CanDownload(plan)) return;

            // A second journal would overwrite the first one and with it the only record of where the player's own mods went
            if(CanRestore) {
                Fail("A previous mod swap is still active.\nUse \"Restore my mods\" first, then join again.");
                return;
            }

            busy = true;
            LastError = "";
            int current = ++attempt;
            Progress("Asking the host for the mods...");

            if(!string.Equals(set.platform, ModSyncPaths.Platform, StringComparison.OrdinalIgnoreCase)) {
                // Another platform's host: every download is that mod's build for this platform from mod.io
                ModManager.instance.StartCoroutine(FetchForThisPlatform(plan, plan.Of(SyncAction.Add).ToList(), current, () => ApplyInGame(plan, set, mode)));
                return;
            }
            List<ModEntry> mods = plan.Of(SyncAction.Replace).Concat(plan.Of(SyncAction.Add)).Select(s => s.hostMod).ToList();
            List<ModEntry> fromModio = plan.Of(SyncAction.Add).Where(s => s.source == "modio" && s.hostMod.modioId > 0 && !s.hostMod.isAmp).Select(s => s.hostMod).ToList();
            // mod.io first; whatever it couldn't deliver as the host's version comes from the host (mods already in the cache are skipped there)
            ModManager.instance.StartCoroutine(FetchFromModio(fromModio, current, () => DownloadFromHost(plan, set, mode, mods, current)));
        }

        // Cross-platform: the mod.io mod the host mapped each mod to, as this platform's file. It can't match the host's hash (another build),
        // so it is stored under its own hash (step.cacheHash) with its mod.io ids; a file already in the cache is not downloaded again.
        // A mod mod.io has no file for on this platform (or that is gone) becomes "not available" and the rest goes on; any other failure
        // (network, storage) stops the whole setup (nothing has been changed yet). The cache is then trimmed like after a host transfer.
        private static IEnumerator FetchForThisPlatform(SyncPlan plan, List<SyncStep> steps, int current, Action next) {
            var cache = new ModCache(ModSyncPaths.CacheFolder);
            long cacheBytes = Math.Max(0, ModManager.safeFile.modSettings.modCacheMaxMB) * 1024L * 1024L;
            long maxBytes = cacheBytes > 0 ? cacheBytes : 4096L * 1024 * 1024; // the host's size is another platform's build: only the cache limit applies
            var unavailable = new List<string>();
            foreach(SyncStep step in steps) {
                if(attempt != current) yield break;
                string name = step.hostMod.name;
                long modioId = step.modioId;
                Progress("Downloading " + name + " from mod.io...");
                ModioClient.DownloadResult download = null;
                CacheEntry reused = null;
                string crashed = null;
                yield return ModioClient.Guarded(ModioClient.Download(modioId, cache, maxBytes,
                    p => { if(attempt == current) Progress("Downloading " + name + " from mod.io...\n" + (int) (p * 100) + "%"); },
                    () => attempt == current, r => download = r, fileId => (reused = cache.FindModioFile(modioId, fileId)) != null),
                    e => crashed = e.GetType().Name + ": " + e.Message);
                if(attempt != current) yield break;
                if(download != null && download.unavailable) {
                    Log.Warn(Defines.CLIENT, "Mod sync: " + name + " (mod.io " + modioId + ") isn't available for " + ModSyncPaths.Platform + ": " + download.error);
                    step.action = SyncAction.Unavailable;
                    unavailable.Add(name);
                    continue;
                }
                if(download == null || download.error != null) {
                    Fail("Couldn't download " + name + " from mod.io:\n" + (download == null ? crashed ?? "the download stopped" : download.error));
                    yield break;
                }
                if(download.cached) {
                    step.cacheHash = reused.hash;
                    cache.Touch(reused.hash);
                    Log.Info(Defines.CLIENT, "Mod sync: " + name + " (mod.io " + modioId + " file " + download.fileId + ") is already in the cache.");
                    continue;
                }

                ModEntry expected = new ModEntry { folder = step.hostMod.folder, name = step.hostMod.name, modioId = modioId };
                Task<CacheEntry> commit = Task.Run(() => cache.CommitUnverified(download.folder, expected, download.fileId)); // hashing: off the main thread
                while(!commit.IsCompleted) yield return null;
                ModioClient.TryDelete(download.root);
                if(commit.IsFaulted || commit.Result == null) {
                    Fail("Couldn't store " + name + " from mod.io" + (commit.IsFaulted ? ": " + commit.Exception.GetBaseException().Message : "."));
                    yield break;
                }
                step.cacheHash = commit.Result.hash;
                Log.Info(Defines.CLIENT, "Mod sync: " + name + " downloaded from mod.io " + modioId + ", file " + download.fileId + " (" + download.fileName + ", version " + download.version + ") for " + ModSyncPaths.Platform + ".");
            }
            if(attempt != current) yield break;

            // The same cache limit as host transfers; this download's entries are never evicted
            var keep = new HashSet<string>(steps.Where(s => s.action == SyncAction.Add && s.cacheHash != null).Select(s => s.cacheHash));
            Task evict = Task.Run(() => cache.Evict(cacheBytes, keep));
            while(!evict.IsCompleted) yield return null;
            if(evict.IsFaulted) Log.Err(Defines.CLIENT, "Mod sync: cleaning the mod cache failed. " + evict.Exception.GetBaseException().Message);
            if(attempt != current) yield break;

            if(unavailable.Count > 0) ModSyncClient.ShowNotice("Not available for your platform on mod.io:\n" + string.Join(", ", unavailable));
            if(!plan.BlocksJoin) {
                // Nothing left to install: join with the warning, like a plan that only had unavailable mods
                busy = false;
                ModSyncClient.JoinWithoutSetup();
                yield break;
            }
            next();
        }

        // Host mods that came from mod.io are downloaded from mod.io, one after the other. mod.io only serves a mod's latest file, so a download
        // only counts when its files hash exactly like the host's copy: then it goes into the cache under the host's hash. Anything else
        // (another version, mod.io unreachable, too big) is dropped and the host sends that mod instead. Never writes id.modio.
        private static IEnumerator FetchFromModio(List<ModEntry> mods, int current, Action next) {
            var cache = new ModCache(ModSyncPaths.CacheFolder);
            long cacheBytes = Math.Max(0, ModManager.safeFile.modSettings.modCacheMaxMB) * 1024L * 1024L;
            foreach(ModEntry mod in mods) {
                if(attempt != current) yield break;
                if(cache.Find(mod.hash) != null) continue;

                long maxBytes = mod.sizeBytes * 2 + 1024 * 1024; // the same limit as a transfer from the host
                if(cacheBytes > 0) maxBytes = Math.Min(maxBytes, cacheBytes);
                string name = mod.name;
                Progress("Downloading " + name + " from mod.io...");
                ModioClient.DownloadResult download = null;
                string crashed = null;
                yield return ModioClient.Guarded(ModioClient.Download(mod.modioId, cache, maxBytes,
                    p => { if(attempt == current) Progress("Downloading " + name + " from mod.io...\n" + (int) (p * 100) + "%"); },
                    () => attempt == current, r => download = r), e => crashed = e.GetType().Name + ": " + e.Message);
                if(attempt != current) yield break;
                if(download == null || download.error != null) {
                    Log.Warn(Defines.CLIENT, "Mod sync: " + name + " (mod.io " + mod.modioId + ") comes from the host instead: " + (download == null ? crashed ?? "the download stopped" : download.error));
                    continue;
                }

                string actualHash = null;
                ModEntry expected = mod;
                Task<CacheEntry> commit = Task.Run(() => cache.Commit(download.folder, expected, download.fileId, out actualHash)); // hashing: off the main thread
                while(!commit.IsCompleted) yield return null;
                ModioClient.TryDelete(download.root);
                string file = "file " + download.fileId + " (" + download.fileName + ", version " + download.version + ")";
                if(commit.IsFaulted || commit.Result == null) {
                    Log.Warn(Defines.CLIENT, "Mod sync: the mod.io " + file + " of " + name + " isn't the host's version ("
                        + (commit.IsFaulted ? commit.Exception.GetBaseException().Message : "hash " + actualHash + ", host " + mod.hash) + "); the host sends it instead.");
                    continue;
                }
                Log.Info(Defines.CLIENT, "Mod sync: " + name + " downloaded from mod.io " + file + ", same files as the host's.");
            }
            if(attempt == current) next();
        }

        private static void DownloadFromHost(SyncPlan plan, HostModSet set, SwapMode mode, List<ModEntry> mods, int current) {
            ModTransferClient.Download(mods,
                (received, total, name) => {
                    if(attempt == current) Progress("Downloading " + name + "...\n" + SyncPlan.Megabytes(received) + " of " + SyncPlan.Megabytes(total));
                },
                (ok, error) => {
                    if(attempt != current) return;
                    if(!ok) { Fail(error); return; }
                    if(InGame) { ApplyInGame(plan, set, mode); return; }
                    try {
                        ApplyAfterDownload(plan, set, mode);
                    } catch(Exception e) {
                        Log.Err(Defines.CLIENT, "Mod swap: couldn't start the helper. " + e);
                        DropPlannedJournal();
                        Fail("Couldn't start the mod swap: " + e.Message);
                    }
                });
        }

        private static void ApplyAfterDownload(SyncPlan plan, HostModSet set, SwapMode mode) {
            ApplyJournal(PlanJournal(plan, set, mode), Progress, "Closing the game to install the mods...\nIt reopens and reconnects by itself.", Fail);
        }

        private static SwapJournal PlanJournal(SyncPlan plan, HostModSet set, SwapMode mode) {
            string host;
            int port;
            bool canRejoin = RejoinAddress(out host, out port);
            SwapJournal planned = JournalPlanner.Build(plan, set.loadOrder, host, port, ModManager.joinPassword, mode);
            planned.rejoinPending = canRejoin; // a Steam lobby can't be rejoined by address: the player joins again by hand (PC only, see CanDownload)
            return planned;
        }

        private const float QuitDelay = 5f; // the player reads "start the game again" before it closes
        private static bool closingForRestart; // the game closes to load the swapped mods: OnQuit must not undo them

        internal static bool ClosingForRestart { get; private set; } // Quest: the game closes by itself in a moment (tests)
        internal static bool RestoredAtStartup { get; private set; } // Quest: this start undid a swap the last session left behind (tests)
        internal static bool ClosePending => RestoredAtStartup || ClosingForRestart; // the game is about to close: no hosting or joining
        internal const string ClosePendingText = "Blade & Sorcery closes in a moment to load your own mods.\nStart it again, then join.";

        // Quest: the journal is applied right here (off the main thread, renames only), then the game closes; the next start rejoins.
        // Unverified on a headset: tested on PC with -amp-simulate-platform android and JSON-only mods (Application.Quit / quitting on Quest).
        private static void ApplyInGame(SyncPlan plan, HostModSet set, SwapMode mode) {
            SwapJournal planned;
            try {
                planned = PlanJournal(plan, set, mode);
                Save(planned);
            } catch(Exception e) {
                Log.Err(Defines.CLIENT, "Mod swap: couldn't write the journal. " + e);
                Fail("Couldn't start the mod swap: " + e.Message);
                return;
            }
            Progress("Installing the mods...");
            ModManager.instance.StartCoroutine(ApplyInGameRoutine(planned));
        }

        private static IEnumerator ApplyInGameRoutine(SwapJournal planned) {
            string mods = ModSyncPaths.ModsFolder, cacheRoot = ModSyncPaths.CacheFolder, journalPath = JournalPath;
            Task<bool> apply = Task.Run(() => JournalApplier.Apply(planned, mods, cacheRoot, Path.GetDirectoryName(journalPath), j => j.SaveTo(journalPath)));
            while(!apply.IsCompleted) yield return null;
            journal = planned;
            if(apply.IsFaulted || !apply.Result) {
                string error = apply.IsFaulted ? apply.Exception.GetBaseException().Message : planned.lastError;
                Log.Err(Defines.CLIENT, "Mod swap: installing in-game failed (" + planned.state + "). " + error);
                if(planned.state == SwapState.Restored) DeleteJournal(); // rolled back completely: nothing to remember
                Fail("Couldn't install the mods: " + error + (planned.state == SwapState.Restored ? "\nNothing was changed." : "\nBook > Mods > AMP > Restore my mods."));
                yield break;
            }
            Log.Info(Defines.CLIENT, "Mod swap: applied in-game (" + planned.ops.Count + " changes), closing the game for the restart.");
            yield return CloseForRestart("Mods installed.\nRestart Blade & Sorcery to finish: it reconnects by itself.");
        }

        private static IEnumerator CloseForRestart(string text) {
            closingForRestart = true;
            ClosingForRestart = true;
            Progress(text);
            ModSyncClient.ShowNotice(text);
            yield return new WaitForSecondsRealtime(QuitDelay);
            Application.Quit();
        }

        // Quest: undoes the swap in-game (renames only). Called on quit, at startup when the last quit didn't get to it, and by "Restore my mods".
        private static bool RestoreInGame() {
            journal = Load();
            if(journal == null) return true;
            journal.state = SwapState.RestorePending;
            Save(journal);
            string journalPath = JournalPath;
            bool ok = JournalApplier.Restore(journal, ModSyncPaths.ModsFolder, ModSyncPaths.CacheFolder, Path.GetDirectoryName(journalPath), j => j.SaveTo(journalPath));
            Debug.Log("[AMP] Mod swap: in-game restore " + (ok ? "done" : "failed: " + journal.lastError) + (JournalApplier.LastNote.Length > 0 ? " (" + JournalApplier.LastNote + ")" : ""));
            return ok;
        }

        // Saves the journal, starts the helper and closes the game once Windows confirms the helper runs (the helper applies the journal and relaunches).
        // Also used for AMP's own updates. Throws if the helper can't be prepared; onFailed gets the message when it didn't start (nothing was changed).
        internal static void ApplyJournal(SwapJournal planned, Action<string> progress, string closingText, Action<string> onFailed) {
            Save(planned);

            progress("Starting the helper...");
            Process launcher;
            try {
                launcher = LaunchHelper("apply", true);
            } catch(Exception) {
                DropPlannedJournal(); // the helper can't start: don't leave a Planned journal behind
                throw;
            }
            ModManager.instance.StartCoroutine(QuitWhenHelperStarted(launcher, progress, closingText, () => {
                DropPlannedJournal();
                onFailed(HelperNotStarted);
            }));
        }

        private const string HelperNotStarted = "Couldn't start the helper (blocked by security software?). Nothing was changed.";

        // The launcher is a short-lived powershell that asks WMI to create the helper and exits with 0 once Windows has done so.
        // Only then is it safe to close the game: the helper waits for it and takes over.
        private static IEnumerator QuitWhenHelperStarted(Process launcher, Action<string> progress, string closingText, Action onFailed) {
            float waited = 0f;
            while(launcher != null && !launcher.HasExited && waited < 10f) {
                yield return new WaitForSecondsRealtime(0.2f);
                waited += 0.2f;
            }
            if(launcher != null && launcher.HasExited && launcher.ExitCode == 0) {
                progress(closingText);
                Application.Quit();
                yield break;
            }
            Log.Err(Defines.CLIENT, "Mod swap: the helper launcher " + (launcher != null && launcher.HasExited ? "exited with code " + launcher.ExitCode : "didn't finish in 10 s"));
            try { if(launcher != null && !launcher.HasExited) launcher.Kill(); } catch(Exception) { }
            helperLaunched = false; // nothing is running that would handle the quit
            onFailed();
        }

        private static void Progress(string text) {
            UI.IngameModUI ui = UI.IngameModUI.currentUI;
            if(ui != null) ui.ShowModSyncProgress(text);
        }

        internal const string ConnectionLostText = "The connection to the host was lost during the download.\nJoin again to continue: mods that finished downloading are kept.";

        // Main thread, when the connection ends (ModManager.StopClientImmediatly). A download that was still running stops silently
        // (ModTransferClient.Cancel), so without this the setup would wait forever; once the helper runs, the game is closing anyway.
        internal static void ConnectionLost() {
            if(!busy || closingForRestart || helperLaunched) return;
            attempt++; // a mod.io download of this setup stops at its next step
            Fail(ConnectionLostText);
        }

        private static void Fail(string error) {
            busy = false;
            LastError = error ?? "Download failed.";
            retryAt = Time.realtimeSinceStartup + RETRY_COOLDOWN;
            Log.Err(Defines.CLIENT, "Mod swap: " + LastError);
            UI.IngameModUI ui = UI.IngameModUI.currentUI;
            if(ui != null && ModSyncClient.Plan != null && ModSyncClient.HostSet != null) {
                ui.ShowModSync(ModSyncClient.Plan.Describe(), ModSyncClient.HostSet.mode != ModSyncMode.Enforce);
            }
        }

        // The helper never ran: the journal only says "planned", nothing changed
        private static void DropPlannedJournal() {
            if(journal != null && journal.state == SwapState.Planned) DeleteJournal();
        }

        #endregion

        #region Startup, quit, restore

        internal static void OnStartup() {
            journal = Load();
            ReportHelperLog();
            if(journal == null) return;

            // The helper may have died halfway: "Planned" with a touched op is not "nothing happened"
            if(journal.state == SwapState.Planned && journal.ops.Any(o => o.done || o.started)) {
                journal.state = SwapState.RestorePending;
                Save(journal);
            }

            switch(JournalPlanner.OnStartup(journal, InGame)) {
                case StartupAction.Forget: // the helper never ran, the swap is undone, or AMP's own update finished: nothing to remember
                    if(journal.state == SwapState.Applied) { journal.state = SwapState.Restored; Save(journal); }
                    DeleteJournal();
                    break;

                case StartupAction.OfferRestore:
                    Notice(CanForget
                        ? "Your mods couldn't be restored automatically, twice.\nBook > Mods > AMP > Forget swap leaves the folders where they are."
                        : "Your mods couldn't be restored automatically.\nBook > Mods > AMP > Restore my mods.");
                    break;

                case StartupAction.Rejoin:
                    journal.rejoinPending = false;
                    RejoinStarted = true;
                    Save(journal);
                    ModManager.instance.StartCoroutine(RejoinRoutine(journal.host, journal.port, journal.password));
                    break;

                case StartupAction.RestoreAndClose:
                    // Quest: the last session ended without the restore on quit (Android may end an app without it), or a restore stopped halfway.
                    // This start loaded the swapped folders, so once they are back the game must close to load the player's own mods.
                    // Unverified on a headset.
                    if(RestoreInGame()) {
                        RestoredAtStartup = true;
                        closingForRestart = true; // nothing left for OnQuit to undo
                        ModSyncClient.ShowNotice(ClosePendingText);
                        ModManager.instance.StartCoroutine(CloseAfterStartupRestore());
                    } else {
                        Notice(CanForget
                            ? "Your mods couldn't be restored automatically, twice.\nBook > Mods > AMP > Forget swap leaves the folders where they are."
                            : "Your mods couldn't be restored automatically:\n" + journal.lastError + "\nBook > Mods > AMP > Restore my mods.");
                    }
                    break;
                case StartupAction.None:
                    // PC: a Temporary swap is undone when the game quits normally. After a crash this start runs with the host's mods
                    // and the multiplayer save without a word; say so (quitting normally or "Restore my mods" brings the player's mods back).
                    if(!InGame && journal.state == SwapState.Applied && journal.mode == SwapMode.Temporary) {
                        LeftOverAtStartup = true;
                        Log.Warn(Defines.CLIENT, "Mod swap: the last session ended without quitting normally, the host's mods are still installed.");
                        Notice(LeftOverText);
                    }
                    break;
            }
        }

        internal static bool LeftOverAtStartup { get; private set; } // PC: this start found a Temporary swap the last session didn't undo (tests)
        internal const string LeftOverText = "The last multiplayer session didn't close normally: the host's mods are still installed.\n"
                                           + "Book > Mods > AMP > Restore my mods (or quit the game normally) to get your own mods back.";

        private const float StartupCloseWait = 120f; // how long the player gets to load into a level and read why the game closes

        private static IEnumerator CloseAfterStartupRestore() {
            float waited = 0f;
            while(!InPlayableLevel() && waited < StartupCloseWait) {
                yield return new WaitForSecondsRealtime(1f);
                waited += 1f;
            }
            Log.Info(Defines.CLIENT, "Mod swap: restored in-game at startup, the game closes to load the player's own mods.");
            yield return CloseForRestart("Your own mods were put back after the last multiplayer session.\nThe game closes now: start it again to load them.");
        }

        // Application.quitting: an applied swap (Temporary or Keep) is undone once the game has closed
        internal static void OnQuit() {
            try {
                // The file is the truth (a helper may have changed it since this game started), never a stale in-memory copy
                if(helperLaunched) return; // RestoreNow / the download already started the helper that handles this
                if(closingForRestart) return; // Quest: the swap was just applied (or undone) and the player starts the game again
                journal = Load();
                if(journal == null || (journal.state != SwapState.Applied && journal.state != SwapState.RestorePending)) return;
                if(InGame) { RestoreInGame(); return; } // Quest: undone right here; unverified on a headset that quitting always fires
                journal.state = SwapState.RestorePending;
                Save(journal);
                // Temporary puts everything back; Keep only re-enables what was disabled and restores the load order
                Process launcher = LaunchHelper("restore", false);
                if(launcher != null) {
                    bool exited = launcher.WaitForExit(5000); // once it exits with 0, the WMI-created helper lives on without the game
                    Debug.Log("[AMP] Mod swap: restore launcher " + (exited ? "exited with code " + launcher.ExitCode : "didn't finish in 5 s"));
                }
            } catch(Exception e) {
                Debug.Log("[AMP] Mod swap: couldn't start the restore helper on quit. " + e.Message); // state is RestorePending: the next start offers the button
            }
        }

        internal static void RestoreNow() {
            if(busy) return;
            if(CanForget) { ForgetSwap(); return; }
            busy = true;
            SwapState before = SwapState.Applied;
            try {
                journal = Load(); // never save a stale in-memory copy over what the helper wrote
                if(!CanRestore) { busy = false; return; }
                if(InGame) {
                    // Quest: undone in-game; the mods loaded now stay until the game restarts
                    if(RestoreInGame()) ModManager.instance.StartCoroutine(CloseForRestart("Your mods are restored.\nRestart Blade & Sorcery to load them."));
                    else { busy = false; Notice("Your mods couldn't be restored completely:\n" + journal.lastError); }
                    return;
                }
                before = journal.state;
                journal.state = SwapState.RestorePending;
                Save(journal);
                Notice("Starting the helper to restore your mods...");
                Process launcher = LaunchHelper("restore", true);
                ModManager.instance.StartCoroutine(QuitWhenHelperStarted(launcher, Progress, "Closing the game to restore your mods...", () => {
                    RestoreState(before);
                    busy = false;
                    Notice(HelperNotStarted);
                }));
            } catch(Exception e) {
                busy = false;
                RestoreState(before);
                Log.Err(Defines.CLIENT, "Mod swap: couldn't start the restore. " + e);
                Notice("Couldn't start the restore: " + e.Message + "\nNothing was changed.");
            }
        }

        // The restore didn't start: the journal goes back to what it said
        private static void RestoreState(SwapState before) {
            try {
                journal = Load();
                if(journal != null && journal.state == SwapState.RestorePending) {
                    journal.state = before;
                    Save(journal);
                }
            } catch(Exception e) {
                Log.Err(Defines.CLIENT, "Mod swap: couldn't reset the journal. " + e.Message);
            }
        }

        // After two restores that didn't finish: "Forget swap" (second click confirms). The folders stay where they are, the notice says where.
        internal static bool CanForget { get { return journal != null && journal.restoreFailures >= 2 && journal.state == SwapState.RestorePending; } }

        internal static string RestoreButtonLabel {
            get { return !CanForget ? "Restore my mods" : (Time.realtimeSinceStartup < forgetArmedUntil ? "Click again to forget" : "Forget swap"); }
        }

        private static float forgetArmedUntil;

        internal static void ForgetSwap() {
            if(!CanForget) return;
            string where = LeftoverFolders();
            if(Time.realtimeSinceStartup >= forgetArmedUntil) {
                forgetArmedUntil = Time.realtimeSinceStartup + 10f;
                Notice("Forgetting this swap leaves your folders where they are:\n" + where + "\nClick again within 10 s to confirm.");
                return;
            }
            forgetArmedUntil = 0f;
            DeleteJournal();
            Notice("Swap forgotten. Folders left in place:\n" + where);
        }

        // Where a stuck swap left things, so the player can move them back by hand
        private static string LeftoverFolders() {
            var found = new List<string>();
            try {
                foreach(string dir in Directory.GetDirectories(ModSyncPaths.ModsFolder)) {
                    string name = Path.GetFileName(dir);
                    if(name.StartsWith(JournalApplier.OffPrefix) || name.StartsWith(JournalApplier.OldPrefix) || name.StartsWith(JournalApplier.ConflictPrefix)) found.Add(name + " (in Mods)");
                }
                if(journal != null) {
                    string old = JournalApplier.OldFolder(ModSyncPaths.CacheFolder, journal);
                    if(Directory.Exists(old)) found.Add(old);
                    found.AddRange(journal.conflicts.Where(c => !c.StartsWith(ModSyncPaths.ModsFolder, StringComparison.OrdinalIgnoreCase)));
                }
            } catch(Exception e) {
                Log.Warn(Defines.CLIENT, "Mod swap: couldn't list the leftover folders. " + e.Message);
            }
            return found.Count == 0 ? "(none found)" : string.Join("\n", found);
        }

        private static IEnumerator RejoinRoutine(string host, int port, string password) {
            yield return WaitForLevel();
            if(!InPlayableLevel()) yield break;
            yield return new WaitForSecondsRealtime(3f); // let the level settle
            if(ModManager.clientInstance != null || ModManager.serverInstance != null) yield break; // the player connected by hand
            Notice("Reconnecting to " + host + "...");
            GUIManager.JoinServer(host, port.ToString(), password, false);
        }

        private static IEnumerator WaitForLevel() {
            float waited = 0f;
            while(!InPlayableLevel() && waited < LEVEL_WAIT_MAX) {
                yield return new WaitForSecondsRealtime(1f);
                waited += 1f;
            }
        }

        // Same rule the book uses for joining: a loaded level that isn't the menu, character select or a dungeon, with the player spawned
        internal static bool InPlayableLevel() {
            if(Level.current == null || !Level.current.loaded || Level.current.data == null || Level.current.data.id == null) return false;
            string id = Level.current.data.id;
            if(id == "CharacterSelection" || id == "MainMenu" || id.StartsWith("Dungeon")) return false;
            return Player.currentCreature != null;
        }

        internal static bool RejoinStarted { get; private set; } // this start reconnects by itself, so the player is already told what is going on

        internal static void Notice(string text) {
            if(ModManager.instance == null) return;
            ModManager.instance.StartCoroutine(NoticeRoutine(text));
        }

        private static IEnumerator NoticeRoutine(string text) {
            yield return WaitForLevel();
            if(InPlayableLevel()) ModSyncClient.ShowNotice(text);
        }

        // The helper's last word, shown once: it tells the player when the swap failed and was rolled back
        private static void ReportHelperLog() {
            string log = Path.Combine(HelperFolder, "helper.log");
            try {
                if(!File.Exists(log)) return;
                List<string> lines = File.ReadAllLines(log).Where(l => l.Trim().Length > 0).ToList();
                File.Delete(log);
                int start = lines.FindLastIndex(l => l.Contains("helper start"));
                Func<string, string> text = l => { int space = l.IndexOf(' '); return space >= 0 ? l.Substring(space + 1) : l; };
                List<string> run = lines.Skip(Math.Max(start, 0)).Select(text).ToList();
                Log.Info(Defines.CLIENT, "Mod swap helper: " + string.Join(" | ", run.ToArray())); // the file is gone now: keep it in Player.log
                string problem = run.LastOrDefault(t => t.StartsWith("failed") || t.StartsWith("error"));
                string note = run.LastOrDefault(t => t.StartsWith("note:"));
                if(problem != null) Notice("The mod swap didn't work:\n" + problem);
                if(note != null) Notice(note.Substring(5).Trim());
            } catch(Exception e) {
                Log.Warn(Defines.CLIENT, "Mod swap: couldn't read the helper log. " + e.Message);
            }
        }

        #endregion

        #region Helper and journal files

        // Starts a short-lived powershell that asks WMI to create the real helper (outside the game's process tree)
        private static bool helperLaunched; // a helper was started in this session: OnQuit must not start a second one

        private static Process LaunchHelper(string action, bool relaunch) {
            string ampFolder = FindAmpFolder();
            if(ampFolder == null) throw new FileNotFoundException("AMP's own mod folder wasn't found.");
            string ampDll = Path.Combine(ampFolder, "AMP.dll");

            // Newtonsoft.Json.dll ships next to AMP.dll; the loaded assembly's location is the fallback
            string jsonDll = Path.Combine(ampFolder, "Newtonsoft.Json.dll");
            if(!File.Exists(jsonDll)) jsonDll = typeof(Newtonsoft.Json.JsonConvert).Assembly.Location;
            if(!File.Exists(ampDll) || !File.Exists(jsonDll)) throw new FileNotFoundException("AMP.dll or Newtonsoft.Json.dll wasn't found.");

            // A fresh folder per launch: the helper loads these copies, never the files it is about to swap, and an older helper may still hold its own
            string runFolder = Path.Combine(HelperFolder, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(runFolder);
            string ampCopy  = Path.Combine(runFolder, "AMP.dll");
            string jsonCopy = Path.Combine(runFolder, "Newtonsoft.Json.dll");
            string script   = Path.Combine(runFolder, "helper.ps1");
            File.Copy(ampDll, ampCopy, true);
            File.Copy(jsonDll, jsonCopy, true);
            File.WriteAllText(script, HelperScript.Script());
            CleanOldRuns(runFolder);

            string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            // End-to-end tests started the game through Steam with test arguments: the relaunch has to do the same
            string relaunchSpec = relaunch && Autopilot.Active ? Autopilot.RelaunchSpec() : null;
            string arguments = HelperScript.LauncherArguments(script, Process.GetCurrentProcess().Id, ampCopy, jsonCopy, JournalPath,
                                                              ModSyncPaths.ModsFolder, ModSyncPaths.CacheFolder, action, relaunch, Path.Combine(HelperFolder, "helper.log"), relaunchSpec);
            Process launcher = Process.Start(new ProcessStartInfo(powershell, arguments) { UseShellExecute = false, CreateNoWindow = true });
            helperLaunched = true;
            return launcher;
        }

        private static void CleanOldRuns(string keep) {
            foreach(string dir in Directory.GetDirectories(HelperFolder)) {
                if(string.Equals(dir, keep, StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(dir, true); } catch(Exception) { } // a helper may still be running from it
            }
        }

        // The enabled mod folder that holds the AMP that is running now
        internal static string FindAmpFolder() {
            System.Reflection.Assembly mine = typeof(ModSwapClient).Assembly;
            foreach(ThunderRoad.ModManager.ModData mod in ThunderRoad.ModManager.loadedMods) {
                if(mod.assemblies != null && mod.assemblies.Contains(mine) && !string.IsNullOrEmpty(mod.fullPath) && File.Exists(Path.Combine(mod.fullPath, "AMP.dll"))) return mod.fullPath;
            }
            // Same test the mod inventory uses for its isAmp entry
            foreach(string dir in Directory.GetDirectories(ModSyncPaths.ModsFolder)) {
                if(Path.GetFileName(dir).StartsWith("_")) continue;
                if(File.Exists(Path.Combine(dir, "AMP.dll"))) return dir;
            }
            return null;
        }

        private static SwapJournal Load() {
            return SwapJournal.LoadFrom(JournalPath); // also uses a leftover .tmp file when the real one is unreadable
        }

        private static void Save(SwapJournal toSave) {
            toSave.SaveTo(JournalPath);
            journal = toSave;
        }

        private static void DeleteJournal() {
            try {
                File.Delete(JournalPath);
                File.Delete(JournalPath + ".tmp");
            } catch(Exception e) {
                Log.Warn(Defines.CLIENT, "Mod swap: couldn't delete the journal. " + e.Message);
            }
            journal = null;
        }

        #endregion
    }
}
