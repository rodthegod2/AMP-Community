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
            if(busy || Time.realtimeSinceStartup < retryAt) return;
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
            Save(planned);

            Progress("Closing the game to install the mods...\nIt reopens and reconnects by itself.");
            LaunchHelper("apply", true);
            ModManager.instance.StartCoroutine(QuitSoon());
        }

        // The launcher asks WMI to create the helper; give that a moment before the game process goes away
        private static IEnumerator QuitSoon() {
            yield return new WaitForSecondsRealtime(1f);
            Application.Quit();
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

            switch(journal.state) {
                case SwapState.Planned:   // the helper never ran
                case SwapState.Restored:  // done, nothing to remember
                    DeleteJournal();
                    break;

                case SwapState.RestorePending:
                    Notice("Your mods couldn't be restored automatically.\nBook > Mods > AMP > Restore my mods.");
                    break;

                case SwapState.Applied:
                    if(journal.rejoinPending) {
                        journal.rejoinPending = false;
                        Save(journal);
                        ModManager.instance.StartCoroutine(RejoinRoutine(journal.host, journal.port, journal.password));
                    }
                    // Temporary: restored on quit (OnQuit). A crash leaves Applied behind, "Restore my mods" handles it.
                    break;
            }
        }

        // Application.quitting: a Temporary swap puts the player's mods back once the game has closed
        internal static void OnQuit() {
            try {
                if(journal == null || journal.state != SwapState.Applied || journal.mode != SwapMode.Temporary) return;
                journal.state = SwapState.RestorePending;
                Save(journal);
                LaunchHelper("restore", false);
            } catch(Exception e) {
                Debug.Log("[AMP] Mod swap: couldn't start the restore helper on quit. " + e.Message); // state is RestorePending: the next start offers the button
            }
        }

        internal static void RestoreNow() {
            if(busy || !CanRestore) return;
            busy = true;
            try {
                journal.state = SwapState.RestorePending;
                Save(journal);
                Notice("Closing the game to restore your mods.\nIt reopens by itself.");
                LaunchHelper("restore", true);
                ModManager.instance.StartCoroutine(QuitSoon());
            } catch(Exception e) {
                busy = false;
                Log.Err(Defines.CLIENT, "Mod swap: couldn't start the restore. " + e);
                Notice("Couldn't start the restore: " + e.Message);
            }
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

        private static void Notice(string text) {
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
                string problem = lines.Skip(Math.Max(start, 0)).LastOrDefault(l => {
                    int space = l.IndexOf(' ');
                    string text = space >= 0 ? l.Substring(space + 1) : l;
                    return text.StartsWith("failed") || text.StartsWith("error");
                });
                if(problem != null) Notice("The mod swap didn't work:\n" + problem.Substring(problem.IndexOf(' ') + 1));
            } catch(Exception e) {
                Log.Warn(Defines.CLIENT, "Mod swap: couldn't read the helper log. " + e.Message);
            }
        }

        #endregion

        #region Helper and journal files

        // Starts a short-lived powershell that asks WMI to create the real helper (outside the game's process tree)
        private static void LaunchHelper(string action, bool relaunch) {
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
            Process.Start(new ProcessStartInfo(powershell, arguments) { UseShellExecute = false, CreateNoWindow = true });
        }

        private static void CleanOldRuns(string keep) {
            foreach(string dir in Directory.GetDirectories(HelperFolder)) {
                if(string.Equals(dir, keep, StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(dir, true); } catch(Exception) { } // a helper may still be running from it
            }
        }

        // The enabled mod folder that holds the AMP that is running now
        private static string FindAmpFolder() {
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
            try {
                return File.Exists(JournalPath) ? SwapJournal.FromJson(File.ReadAllText(JournalPath)) : null;
            } catch(Exception e) {
                Log.Err(Defines.CLIENT, "Mod swap: couldn't read the journal. " + e.Message);
                return null;
            }
        }

        // Written to a side file first so a crash can't leave half a journal
        private static void Save(SwapJournal toSave) {
            Directory.CreateDirectory(ModSyncPaths.CacheFolder);
            string temp = JournalPath + ".tmp";
            File.WriteAllText(temp, toSave.ToJson());
            if(File.Exists(JournalPath)) File.Replace(temp, JournalPath, null);
            else File.Move(temp, JournalPath);
            journal = toSave;
        }

        private static void DeleteJournal() {
            try { File.Delete(JournalPath); } catch(Exception e) { Log.Warn(Defines.CLIENT, "Mod swap: couldn't delete the journal. " + e.Message); }
            journal = null;
        }

        #endregion
    }
}
