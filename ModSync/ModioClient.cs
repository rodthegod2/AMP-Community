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
        private const float CancelSeconds = 30f; // how long a cancelled download may take to stop writing before it is abandoned

        internal class DownloadResult {
            internal string error;  // null on success
            internal string folder; // the extracted mod folder (has manifest.json), inside `root`
            internal string root;   // what the caller deletes when done (its staging folder)
            internal long   fileId; // the mod.io file that was downloaded
            internal string fileName = "";
            internal string version  = "";
            internal bool   cached;  // alreadyCached said yes for this file: nothing was downloaded (folder and root are null)
            internal bool   unavailable; // with error: mod.io has no file of this mod for this platform, or the mod is gone (not worth retrying)
        }

        // A mod.io mod as this platform would download it (LookUpForThisPlatform)
        internal class PlatformFile {
            internal string error;       // null on success
            internal bool   unavailable; // with error: no file for this platform, or the mod is gone
            internal string title = "";
            internal long   fileId;
            internal long   bytes;
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
                    problem = VerifyIsolation(Path.Combine(isolated.ToString(), "modio"));
                    if(problem != null) return "mod.io's data folders are not isolated for the test, not initialising: " + problem;
                    isolatedForTest = true;
                }
                if(ModIOUnity.IsInitialized()) return null;

                string user;
                try { user = Environment.UserName; } catch(Exception) { user = "User"; } // the game falls back the same way
                Result result = ModIOUnity.InitializeForUser(string.IsNullOrEmpty(user) ? "User" : user);
                if(!result.Succeeded()) return "mod.io didn't initialise: " + Describe(result);
                if(isolatedForTest) {
                    string root = UserDataRoot();
                    if(!IsInside(root, Path.Combine(isolated.ToString(), "modio"))) return "mod.io's user data isn't in the isolated folder: " + root;
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

        // Before InitializeForUser touches anything: every redirected field reads back inside the isolated folder, and the plugin's own compiled
        // code builds its paths from the new values (a fresh SystemIODataService's temp init only computes a path, it writes nothing; an
        // inlined copy of an old value would show up here). Null when isolated.
        private static string VerifyIsolation(string root) {
            Type service = typeof(ModIOUnity).Assembly.GetType("ModIO.Implementation.Platform.SystemIODataService");
            if(service == null) return "SystemIODataService not found";
            foreach(string name in new[] { "PersistentDataRootDirectory", "UserRootDirectory", "GlobalSettingsFilePath", "TempRootDirectory" }) {
                string value = service.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) as string;
                if(!IsInside(value, root)) return name + " is " + (value ?? "unknown");
            }
            try {
                object probe = Activator.CreateInstance(service, true);
                Type temp = typeof(ModIOUnity).Assembly.GetType("ModIO.Implementation.Platform.ITempDataService");
                MethodInfo init = temp?.GetMethod("Initialize");
                if(init == null) return "ITempDataService.Initialize not found";
                object[] arguments = init.GetParameters().Select(p => p.ParameterType == typeof(long) ? (object) 3852L
                                                                    : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
                init.Invoke(probe, arguments);
                string built = service.GetProperty("RootDirectory")?.GetValue(probe, null) as string;
                if(!IsInside(built, root)) return "the plugin builds its temp folder as " + (built ?? "unknown");
            } catch(Exception e) {
                return "the plugin's paths couldn't be checked: " + e.GetBaseException().Message;
            }
            return null;
        }

        private static bool IsInside(string path, string root) {
            if(string.IsNullOrEmpty(path)) return false;
            string a = path.Replace('\\', '/').TrimEnd('/'), b = root.Replace('\\', '/').TrimEnd('/');
            return a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + "/", StringComparison.OrdinalIgnoreCase);
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
            GetMod(id, false, (p, e, gone) => done(p, e));
        }

        // fresh: asks mod.io even when the plugin has this mod in its response cache (kept about a minute, maybe from another platform's
        // request). gone: the error says the mod doesn't exist (any more), see ModioRules.IsGone.
        internal static void GetMod(long id, bool fresh, Action<ModProfile?, string, bool> done) {
            string problem = EnsureInitialized();
            if(problem != null) { Dispatcher.Enqueue(() => done(null, problem, false)); return; }
            if(fresh) ForgetCached(id);
            ModIOUnity.GetMod(new ModId(id), r => {
                bool ok = r != null && r.result.Succeeded();
                ModProfile? profile = ok ? r.value : (ModProfile?) null;
                string error = ok ? null : (r == null ? "no answer" : Describe(r.result));
                bool gone = !ok && r != null && ModioRules.IsGone(r.result.errorCode, r.result.apiCode);
                Dispatcher.Enqueue(() => done(profile, error, gone));
            });
        }

        // The plugin's ResponseCache.ClearModFromCache (internal). If it can't be found the cached answer is used, which is only ever a minute old.
        private static void ForgetCached(long id) {
            try {
                MethodInfo clear = typeof(ModIOUnity).Assembly.GetType("ModIO.Implementation.API.ResponseCache")
                                   ?.GetMethod("ClearModFromCache", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if(clear != null) clear.Invoke(null, new object[] { new ModId(id) });
                else Log.Warn(Defines.CLIENT, "mod.io: ResponseCache.ClearModFromCache not found, using the plugin's cached answer.");
            } catch(Exception e) {
                Log.Warn(Defines.CLIENT, "mod.io: couldn't clear the cached answer for " + id + ". " + e.GetBaseException().Message);
            }
        }

        // Coroutine: what this platform would download for the mod (title, live file and its size), asked fresh. done always gets a result.
        internal static IEnumerator LookUpForThisPlatform(long id, Action<PlatformFile> done) {
            string previousHeader = ModSyncPaths.Simulated ? SetPlatformHeader(ModSyncPaths.Platform) : null;
            var result = new PlatformFile();
            try {
                ModProfile? profile = null;
                string error = null;
                bool answered = false, gone = false;
                GetMod(id, true, (p, e, g) => { profile = p; error = e; gone = g; answered = true; });
                float askedAt = Time.realtimeSinceStartup;
                while(!answered && Time.realtimeSinceStartup - askedAt < StallSeconds) yield return null;
                if(!answered) error = "no answer in " + StallSeconds + " s";
                if(error != null) {
                    result.error = gone ? "mod.io doesn't have mod " + id + " (any more): " + error : error;
                    result.unavailable = gone;
                } else {
                    result.title  = profile.Value.name ?? "";
                    result.fileId = profile.Value.modfile.id;
                    result.bytes  = profile.Value.modfile.filesize;
                    if(result.fileId <= 0) { result.error = "mod " + id + " has no file for this platform on mod.io"; result.unavailable = true; }
                }
            } finally {
                if(previousHeader != null) SetPlatformHeader(previousHeader);
            }
            done(result);
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
        // alreadyCached (optional): asked with the live file's id first; true = that file is in the cache already, nothing is downloaded.
        // A PC test simulating another platform (-amp-simulate-platform) asks mod.io as that platform, so it gets that platform's file.
        internal static IEnumerator Download(long id, ModCache cache, long maxBytes, Action<float> progress, Func<bool> stillWanted, Action<DownloadResult> done, Func<long, bool> alreadyCached = null) {
            string previousHeader = ModSyncPaths.Simulated ? SetPlatformHeader(ModSyncPaths.Platform) : null;
            try {
                IEnumerator inner = DownloadFile(id, cache, maxBytes, progress, stillWanted, done, alreadyCached);
                while(inner.MoveNext()) yield return inner.Current;
            } finally {
                if(previousHeader != null) SetPlatformHeader(previousHeader);
            }
        }

        // The platform the plugin tells mod.io it is (header x-modio-platform, PlatformConfiguration.RESTAPI_HEADER, "Windows" on PC). It is global:
        // only a simulating test changes it, and puts the previous value back. Returns the previous value. On a Quest the plugin is built for
        // Android and should send its own platform, so nothing is forced there (unverified on a headset).
        private static string SetPlatformHeader(string platform) {
            FieldInfo field = typeof(ModIOUnity).Assembly.GetType("ModIO.Implementation.Platform.PlatformConfiguration")?.GetField("RESTAPI_HEADER", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            string previous = field?.GetValue(null) as string ?? RestApiPlatform.Windows.ToString();
            RestApiPlatform value;
            if(!Enum.TryParse(platform, true, out value)) throw new ArgumentException("unknown mod.io platform " + platform);
            ModIO.Util.Utility.ForceSetPlatformHeader(value);
            Debug.Log("[AMP] mod.io: platform header " + previous + " -> " + value);
            return previous;
        }

        private static IEnumerator DownloadFile(long id, ModCache cache, long maxBytes, Action<float> progress, Func<bool> stillWanted, Action<DownloadResult> done, Func<long, bool> alreadyCached) {
            var result = new DownloadResult();

            // Fresh, not the plugin's cached answer: the live file decides whether the cached copy is still current, and its size the limit
            ModProfile? profile = null;
            string error = null;
            bool answered = false, gone = false;
            GetMod(id, true, (p, e, g) => { profile = p; error = e; gone = g; answered = true; });
            float askedAt = Time.realtimeSinceStartup;
            while(!answered && Time.realtimeSinceStartup - askedAt < StallSeconds) yield return null;
            if(!stillWanted()) yield break;
            if(!answered) error = "no answer in " + StallSeconds + " s";
            if(error != null) { result.error = "mod.io doesn't have mod " + id + ": " + error; result.unavailable = gone; done(result); yield break; }

            ModIO.Implementation.API.Objects.Modfile file = profile.Value.modfile;
            result.fileId   = file.id;
            result.fileName = file.filename ?? "";
            result.version  = file.version ?? "";
            if(file.id <= 0) { result.error = "mod " + id + " has no file for this platform on mod.io"; result.unavailable = true; done(result); yield break; }
            if(alreadyCached != null && alreadyCached(file.id)) { result.cached = true; done(result); yield break; }
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
            // A cancelled download still writes to the stream until its task ends; one that never ends (stuck asking for a link) is abandoned
            float cancelledAt = Time.realtimeSinceStartup;
            while(!operation.task.IsCompleted && Time.realtimeSinceStartup - cancelledAt < CancelSeconds) yield return null;
            if(!operation.task.IsCompleted) {
                zip.Dispose();
                Log.Warn(Defines.CLIENT, "mod.io: the download of " + id + " didn't stop within " + CancelSeconds + " s, abandoned.");
                if(!stillWanted()) { TryDelete(root); yield break; }
                fail(stalled ? "the mod.io download stalled for " + StallSeconds + " s" : "the mod.io download didn't finish");
                yield break;
            }
            if(!stillWanted()) { zip.Dispose(); TryDelete(root); yield break; }
            if(stalled) { zip.Dispose(); fail("the mod.io download stalled for " + StallSeconds + " s"); yield break; }
            if(!operation.IsSucceeded) {
                zip.Dispose();
                fail("the mod.io download failed: " + (operation.FailedMessage ?? (operation.IsCompleted ? "unknown error" : "no download link")));
                yield break;
            }
            // The file the operation really fetched (it asks mod.io itself): its size counts, not the one looked up before
            ServedFile(operation, result);
            if(zip.Length > maxBytes) {
                zip.Dispose();
                fail("the mod.io file is larger than expected (" + zip.Length + " bytes)");
                yield break;
            }

            // Extracting can take a while for big mods: off the main thread
            string extracted = Path.Combine(root, "x");
            Task<string> unpack = Task.Run(() => {
                zip.Position = 0;
                ReleasePackage.ExtractAll(zip, extracted, maxBytes);
                // Never let an id.modio (any spelling) through: the game would treat the folder as a mod.io install of the player's
                ModInventory.StripModioIds(extracted);
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

        // The file the operation really downloaded (it asks mod.io itself, GetMod above may come from the plugin's cache)
        private static void ServedFile(ModIO.Util.DownloadModToStreamOperation operation, DownloadResult result) {
            try {
                object served = typeof(ModIO.Util.DownloadModToStreamOperation).GetField("modfileObject", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(operation);
                if(served == null) return;
                Type type = served.GetType();
                long fileId = (long) type.GetField("id").GetValue(served);
                if(fileId <= 0) return;
                result.fileId   = fileId;
                result.fileName = type.GetField("filename").GetValue(served) as string ?? result.fileName;
                result.version  = type.GetField("version").GetValue(served) as string ?? result.version;
            } catch(Exception e) {
                Log.Warn(Defines.CLIENT, "mod.io: couldn't read which file was downloaded. " + e.Message);
            }
        }

        // Runs a nested coroutine so that an exception in it ends only it (reported to failed) and not the coroutine that waits for it
        internal static IEnumerator Guarded(IEnumerator inner, Action<Exception> failed) {
            while(true) {
                object current;
                try {
                    if(!inner.MoveNext()) yield break;
                    current = inner.Current;
                } catch(Exception e) {
                    failed(e);
                    yield break;
                }
                yield return current;
            }
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
