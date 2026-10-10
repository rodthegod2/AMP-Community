using AMP.Data;
using AMP.Logging;
using AMP.Threading;
using ModIO;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using UnityEngine;

namespace AMP.ModSync {
    // Read-only access to mod.io through the game's own mod.io plugin (legacy ModIO SDK, game 3852): mod info, search, download.
    // No login, no subscriptions: the game's mod browser stays the only thing that changes the player's mod.io state, and nothing
    // here ever writes id.modio (the game deletes Mods folders whose id.modio isn't one of the player's subscriptions).
    // Main thread only; callbacks always arrive on the main thread.
    internal static class ModioClient {
        internal const int SearchPageSize = 20;
        private const float StallSeconds = 60f; // a download whose progress doesn't move for this long is given up

        internal class DownloadResult {
            internal string error;  // null on success
            internal string folder; // the extracted mod folder (has manifest.json), inside `root`
            internal string root;   // what the caller deletes when done (its staging folder)
            internal long   fileId; // the mod.io file that was downloaded
            internal string fileName = "";
            internal string version  = "";
        }

        private static bool isolatedForTest;

        // Null when the plugin is ready, otherwise why not. Initialises it like the game's mod browser does when the game hasn't yet.
        internal static string EnsureInitialized() {
            try {
                object isolated = AppDomain.CurrentDomain.GetData("AMP_TEST_ISOLATED");
                if(isolated != null && !isolatedForTest) {
                    // A test must never read or write the player's real mod.io data (login token, subscriptions, installed mods)
                    if(ModIOUnity.IsInitialized()) return "mod.io was initialised before its data folders could be isolated for the test";
                    string problem = IsolateDataFolders(Path.Combine(isolated.ToString(), "modio"));
                    if(problem != null) return "can't isolate mod.io's data folders for the test: " + problem;
                    isolatedForTest = true;
                }
                if(ModIOUnity.IsInitialized()) return null;

                string user;
                try { user = Environment.UserName; } catch(Exception) { user = "User"; } // the game falls back the same way
                Result result = ModIOUnity.InitializeForUser(string.IsNullOrEmpty(user) ? "User" : user);
                if(!result.Succeeded()) return "mod.io didn't initialise: " + Describe(result);
                if(isolatedForTest) {
                    string root = UserDataRoot();
                    if(root == null || !root.Replace('\\', '/').StartsWith(Path.Combine(isolated.ToString(), "modio").Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                        return "mod.io's user data isn't in the isolated folder: " + root;
                    Debug.Log("[AMP] mod.io: initialised for the test, user data in " + root);
                }
                return null;
            } catch(Exception e) {
                return "mod.io isn't available: " + e.GetType().Name + ": " + e.Message;
            }
        }

        // The plugin keeps its folders in static readonly fields (%LOCALAPPDATA%\mod.io, %PUBLIC%\mod.io, the temp cache); in a test they
        // are pointed into the isolated save folder before the plugin first uses them. Null when done.
        private static string IsolateDataFolders(string root) {
            Type service = typeof(ModIOUnity).Assembly.GetType("ModIO.Implementation.Platform.SystemIODataService");
            if(service == null) return "SystemIODataService not found";
            RuntimeHelpers.RunClassConstructor(service.TypeHandle);
            string user = Path.Combine(root, "user").Replace('\\', '/');
            var values = new Dictionary<string, string> {
                { "PersistentDataRootDirectory", Path.Combine(root, "public").Replace('\\', '/') },
                { "UserRootDirectory",           user },
                { "GlobalSettingsFilePath",      user + "/globalsettings.json" },
                { "TempRootDirectory",           Path.Combine(root, "temp").Replace('\\', '/') },
            };
            foreach(KeyValuePair<string, string> v in values) {
                FieldInfo field = service.GetField(v.Key, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if(field == null) return v.Key + " not found";
                Directory.CreateDirectory(v.Key == "GlobalSettingsFilePath" ? user : v.Value);
                field.SetValue(null, v.Value);
                if((string) field.GetValue(null) != v.Value) return v.Key + " can't be changed";
            }
            return null;
        }

        // Where the initialised plugin keeps the user's data (DataStorage.user.RootDirectory), null if unknown
        private static string UserDataRoot() {
            Type storage = typeof(ModIOUnity).Assembly.GetType("ModIO.Implementation.DataStorage");
            object user = storage?.GetField("user", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
            return user?.GetType().GetProperty("RootDirectory")?.GetValue(user, null) as string;
        }

        internal static string Describe(Result result) {
            return result.message + " (code " + result.errorCode + (result.apiCode != 0 ? ", api " + result.apiCode : "") + ")";
        }

        internal static void GetMod(long id, Action<ModProfile?, string> done) {
            string problem = EnsureInitialized();
            if(problem != null) { Dispatcher.Enqueue(() => done(null, problem)); return; }
            ModIOUnity.GetMod(new ModId(id), r => {
                bool ok = r != null && r.result.Succeeded();
                ModProfile? profile = ok ? r.value : (ModProfile?) null;
                string error = ok ? null : (r == null ? "no answer" : Describe(r.result));
                Dispatcher.Enqueue(() => done(profile, error));
            });
        }

        // First page (SearchFilter page size 20) of a full-text search, as mod.io orders it
        internal static void Search(string phrase, Action<List<ModProfile>, string> done) {
            string problem = EnsureInitialized();
            if(problem != null) { Dispatcher.Enqueue(() => done(null, problem)); return; }
            var filter = new SearchFilter(0, SearchPageSize);
            filter.AddSearchPhrase(phrase);
            ModIOUnity.GetMods(filter, r => {
                bool ok = r != null && r.result.Succeeded();
                List<ModProfile> mods = ok ? (r.value.modProfiles ?? new ModProfile[0]).ToList() : null;
                string error = ok ? null : (r == null ? "no answer" : Describe(r.result));
                Dispatcher.Enqueue(() => done(mods, error));
            });
        }

        internal static ModioAvailability PlatformAvailability(ModProfile profile) {
            return ModioRules.Availability((profile.platforms ?? new ModIO.Implementation.API.Objects.ModPlatform[0]).Select(p => new KeyValuePair<string, long>(p.platform, p.modfileLive)));
        }

        // Coroutine: downloads the mod's current file (the one mod.io serves this platform) into a new folder of the cache's partial area,
        // checks it (the plugin verifies the MD5) and extracts it safely. done always gets a result; on success the caller owns result.root
        // and must delete it after committing result.folder (on failure nothing is left behind). stillWanted() false: stop and clean up, done is not called.
        internal static IEnumerator Download(long id, ModCache cache, long maxBytes, Action<float> progress, Func<bool> stillWanted, Action<DownloadResult> done) {
            var result = new DownloadResult();

            ModProfile? profile = null;
            string error = null;
            bool answered = false;
            GetMod(id, (p, e) => { profile = p; error = e; answered = true; });
            float askedAt = Time.realtimeSinceStartup;
            while(!answered && Time.realtimeSinceStartup - askedAt < StallSeconds) yield return null;
            if(!stillWanted()) yield break;
            if(!answered) error = "no answer in " + StallSeconds + " s";
            if(error != null) { result.error = "mod.io doesn't have mod " + id + ": " + error; done(result); yield break; }

            ModIO.Implementation.API.Objects.Modfile file = profile.Value.modfile;
            result.fileId   = file.id;
            result.fileName = file.filename ?? "";
            result.version  = file.version ?? "";
            if(file.id <= 0) { result.error = "mod " + id + " has no file for this platform on mod.io"; done(result); yield break; }
            if(file.filesize > maxBytes) { result.error = "the mod.io file is larger than expected (" + file.filesize + " bytes)"; done(result); yield break; }

            string root = cache.NewStagingFolder();
            Action<string> fail = message => { TryDelete(root); result.error = message; done(result); };
            FileStream zip = null;
            ModIO.Util.DownloadModToStreamOperation operation = null;
            try {
                zip = new FileStream(Path.Combine(root, "download.zip"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                operation = ModIO.Util.Utility.DownloadModToStream(new ModId(id), zip, false);
            } catch(Exception e) {
                zip?.Dispose();
                fail("couldn't start the mod.io download: " + e.Message);
                yield break;
            }

            float lastProgress = -1f, movedAt = Time.realtimeSinceStartup;
            // The operation stays "requesting" forever when mod.io has no download link: its task ending first means that
            bool stalled = false;
            while(!operation.IsCompleted && !operation.task.IsCompleted) {
                if(!stillWanted()) { operation.Cancel(); break; }
                if(operation.DownloadProgress != lastProgress) { lastProgress = operation.DownloadProgress; movedAt = Time.realtimeSinceStartup; progress(lastProgress); }
                if(Time.realtimeSinceStartup - movedAt > StallSeconds) { stalled = true; operation.Cancel(); break; }
                yield return null;
            }
            while(!operation.task.IsCompleted) yield return null; // a cancelled download still writes to the stream until its task ends
            if(!stillWanted()) { zip.Dispose(); TryDelete(root); yield break; }
            if(stalled) { zip.Dispose(); fail("the mod.io download stalled for " + StallSeconds + " s"); yield break; }
            if(!operation.IsSucceeded) {
                zip.Dispose();
                fail("the mod.io download failed: " + (operation.FailedMessage ?? (operation.IsCompleted ? "unknown error" : "no download link")));
                yield break;
            }

            // Extracting can take a while for big mods: off the main thread
            string extracted = Path.Combine(root, "x");
            Task<string> unpack = Task.Run(() => {
                zip.Position = 0;
                ReleasePackage.ExtractAll(zip, extracted, maxBytes);
                // Never let an id.modio through: the game would treat the folder as a mod.io install of the player's
                foreach(string idFile in Directory.GetFiles(extracted, "id.modio", SearchOption.AllDirectories)) File.Delete(idFile);
                return ReleasePackage.FindModFolder(extracted);
            });
            while(!unpack.IsCompleted) yield return null;
            zip.Dispose();
            if(unpack.IsFaulted) {
                fail("the mod.io file couldn't be unpacked: " + unpack.Exception.GetBaseException().Message);
                yield break;
            }
            result.root   = root;
            result.folder = unpack.Result;
            done(result);
        }

        internal static void TryDelete(string dir) {
            if(string.IsNullOrEmpty(dir)) return;
            Task.Run(() => {
                try {
                    if(Directory.Exists(dir)) Directory.Delete(dir, true);
                } catch(Exception e) {
                    Log.Warn(Defines.CLIENT, "mod.io: couldn't delete " + dir + ". " + e.Message);
                }
            });
        }
    }
}
