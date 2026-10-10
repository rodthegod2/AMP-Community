using AMP.Data;
using AMP.Logging;
using System;
using System.IO;
using System.Reflection;
using ThunderRoad;

namespace AMP.ModSync {
    // While a mod swap is active the game reads and writes characters and Options.opt in Saves/AMP_Multiplayer instead of Saves/Default,
    // so the player's own characters never lose items of mods that are switched off, and their own mod options are never cleaned.
    // Main thread, once at startup (AMP's own config stays where it was loaded from).
    internal static class ProfileSwitch {
        internal static bool Active { get; private set; }

        private const BindingFlags Fields = BindingFlags.NonPublic | BindingFlags.Instance;

        internal static void ApplyAtStartup() {
            FieldInfo partialField = null;
            string oldPartial = null;
            PlatformBase platform = null;
            try {
                if(Active || !ModSwapClient.CanRestore) return; // CanRestore: the journal is Applied or RestorePending
                if(ModSyncPaths.Platform != "windows") { Log.Info(Defines.AMP, "Multiplayer profile not supported on Quest yet: your own characters are used."); return; }

                platform = GameManager.platform;
                if(!(platform is PlatformWindows)) { Log.Info(Defines.AMP, "Multiplayer profile: not a Windows platform, skipped."); return; }

                partialField = typeof(PlatformWindows).GetField("savePartialPath", Fields);
                if(typeof(PlatformWindows).GetField("rootPath", Fields) == null || typeof(PlatformWindows).GetField("altPath", Fields) == null || partialField == null) {
                    Log.Warn(Defines.AMP, "Multiplayer profile unavailable (game fields not found).");
                    return;
                }
                oldPartial = (string) partialField.GetValue(platform);
                string folderName = Path.GetFileName(oldPartial);
                if(string.IsNullOrEmpty(oldPartial) || folderName == ProfileSeeder.ProfileFolderName) return;

                string defaultFolder;
                if(!platform.TryGetSavePath(out defaultFolder)) { Log.Warn(Defines.AMP, "Multiplayer profile unavailable (save path not found)."); return; }
                string profilePartial = Path.Combine(Path.GetDirectoryName(oldPartial), ProfileSeeder.ProfileFolderName);
                string profileFolder = Path.Combine(Path.GetDirectoryName(defaultFolder.TrimEnd('/', '\\')), ProfileSeeder.ProfileFolderName);

                if(!ProfileSeeder.EnsureSeeded(defaultFolder, profileFolder)) { Log.Warn(Defines.AMP, "Multiplayer profile unavailable (couldn't create " + profileFolder + ")."); return; }

                partialField.SetValue(platform, profilePartial);
                string now;
                if(!platform.TryGetSavePath(out now) || !SamePath(now, profileFolder)) {
                    partialField.SetValue(platform, oldPartial);
                    Log.Warn(Defines.AMP, "Multiplayer profile unavailable (the game's save path didn't change as expected).");
                    return;
                }

                try {
                    Options.LoadCoroutine().AsSynchronous();
                } catch(Exception e) {
                    partialField.SetValue(platform, oldPartial);
                    try { Options.LoadCoroutine().AsSynchronous(); } catch(Exception) { }
                    Log.Err(Defines.AMP, "Multiplayer profile: loading its options failed, using the normal saves. " + e.Message);
                    return;
                }

                Active = true;
                Log.Info(Defines.AMP, "Multiplayer profile active: " + profileFolder);
                Log.Info(Defines.AMP, "Multiplayer profile: graphics options are not re-applied, they take effect on the next normal start.");
                if(!ModSwapClient.RejoinStarted) ModSwapClient.Notice("You're playing on multiplayer copies of your characters.\nBook > Mods > AMP > Restore my mods to go back.");
            } catch(Exception e) {
                // Whatever went wrong, the normal saves must be the ones in use
                try { if(!Active && partialField != null && oldPartial != null) partialField.SetValue(platform, oldPartial); } catch(Exception) { }
                Log.Err(Defines.AMP, "Multiplayer profile unavailable. " + e.Message);
            }
        }

        private static bool SamePath(string a, string b) {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
    }
}
