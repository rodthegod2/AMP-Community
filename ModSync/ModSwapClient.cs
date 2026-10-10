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
using ThunderRoad;
using UnityEngine;
using ClientType = AMP.Network.Client.Client;
using Debug = UnityEngine.Debug;

namespace AMP.ModSync {
    // PC joiner: downloads the host's mods, closes the game, lets the helper swap the folders (JournalApplier, outside the game because files are locked),
    // reconnects on the next start, and puts the player's own mods back (Temporary swaps on quit, or through "Restore my mods").
    // Everything here runs on the main thread. JournalApplier is never called in-game on PC.
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

        // PC only; the host's files are the source for everything (the host has them, mod.io isn't needed in this phase)
        internal static bool CanDownload(SyncPlan plan) {
            HostModSet set = ModSyncClient.HostSet;
            if(plan == null || set == null || !ModSyncPaths.Available || ModSyncPaths.Platform != "windows") return false;
            if(!string.Equals(set.platform, ModSyncPaths.Platform, StringComparison.OrdinalIgnoreCase)) return false;
            if(!plan.BlocksJoin) return false;
            return plan.Of(SyncAction.Add).Concat(plan.Of(SyncAction.Replace))
                       .All(s => s.hostMod != null && !string.IsNullOrEmpty(s.hostMod.hash) && !string.IsNullOrEmpty(s.hostMod.folder));
        }

        // A new connection starts: nothing of a half-finished download may leak into it. The journal stays.
        internal static void Reset() {
            attempt++;
            busy = false;
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

            List<ModEntry> mods = plan.Of(SyncAction.Replace).Concat(plan.Of(SyncAction.Add)).Select(s => s.hostMod).ToList();
            ModTransferClient.Download(mods,
                (received, total, name) => {
                    if(attempt == current) Progress("Downloading " + name + "...\n" + SyncPlan.Megabytes(received) + " of " + SyncPlan.Megabytes(total));
                },
                (ok, error) => {
                    if(attempt != current) return;
                    if(!ok) { Fail(error); return; }
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
            ClientType client = ModManager.clientInstance;
            bool isIp = client != null && client.netclient is IPClient;
            string host = isIp ? ModManager.guiManager.join_ip : "";
            int port;
            int.TryParse(ModManager.guiManager.join_port, out port);
            string password = ModManager.joinPassword;

            SwapJournal planned = JournalPlanner.Build(plan, set.loadOrder, host, port, password, mode);
            planned.rejoinPending = isIp && port > 0; // a Steam lobby can't be rejoined by address: the player joins again by hand
            ApplyJournal(planned, Progress, "Closing the game to install the mods...\nIt reopens and reconnects by itself.", Fail);
        }

        // Saves the journal, starts the helper and closes the game once Windows confirms the helper runs (the helper applies the journal and relaunches).
        // Also used for AMP's own updates. Throws if the helper can't be prepared; onFailed gets the message when it didn't start (nothing was changed).
        internal static void ApplyJournal(SwapJournal planned, Action<string> progress, string closingText, Action<string> onFailed) {
            Save(planned);

            progress("Starting the helper...");
            Process launcher = LaunchHelper("apply", true);
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

            switch(journal.state) {
                case SwapState.Planned:   // the helper never ran
                case SwapState.Restored:  // done, nothing to remember
                    DeleteJournal();
                    break;

                case SwapState.RestorePending:
                    Notice(CanForget
                        ? "Your mods couldn't be restored automatically, twice.\nBook > Mods > AMP > Forget swap leaves the folders where they are."
                        : "Your mods couldn't be restored automatically.\nBook > Mods > AMP > Restore my mods.");
                    break;

                case SwapState.Applied:
                    if(journal.rejoinPending) {
                        journal.rejoinPending = false;
                        RejoinStarted = true;
                        Save(journal);
                        ModManager.instance.StartCoroutine(RejoinRoutine(journal.host, journal.port, journal.password));
                    }
                    // Temporary: restored on quit (OnQuit). A crash leaves Applied behind, "Restore my mods" handles it.
                    break;
            }
        }

        // Application.quitting: an applied swap (Temporary or Keep) is undone once the game has closed
        internal static void OnQuit() {
            try {
                // The file is the truth (a helper may have changed it since this game started), never a stale in-memory copy
                if(helperLaunched) return; // RestoreNow / the download already started the helper that handles this
                journal = Load();
                if(journal == null || (journal.state != SwapState.Applied && journal.state != SwapState.RestorePending)) return;
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
        private static bool InPlayableLevel() {
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
            string arguments = HelperScript.LauncherArguments(script, Process.GetCurrentProcess().Id, ampCopy, jsonCopy, JournalPath,
                                                              ModSyncPaths.ModsFolder, ModSyncPaths.CacheFolder, action, relaunch, Path.Combine(HelperFolder, "helper.log"));
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
