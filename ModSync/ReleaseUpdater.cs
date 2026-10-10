using AMP.Data;
using AMP.Logging;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace AMP.ModSync {
    // PC only: asks GitHub for the latest release of the community fork and installs it through the same journal + helper swap the mod sync uses.
    // Installs that came from mod.io are left to mod.io. Everything here runs on the main thread; only hashing and unpacking run on a worker.
    // Every address is checked with ReleaseInfo.IsTrustedUrl, redirects are followed by hand so each hop is checked too.
    internal static class ReleaseUpdater {
        private const string UserAgent     = "AMP-Community-Updater";
        private const float  StartupDelay  = 10f;
        private const int    ApiTimeout    = 15;
        private const int    DownloadTimeout = 120;
        private const int    MaxRedirects  = 5;
        private const long   MaxApiBytes   = 1L << 20;    // release JSON
        private const long   MaxSumsBytes  = 64L << 10;   // SHA256SUMS.txt
        private const long   MaxZipBytes   = 150L << 20;
        private const long   MaxUnpackedBytes = 400L << 20;
        private const string SumsName      = "SHA256SUMS.txt";

        internal static ReleaseInfo Available { get; private set; }
        internal static bool        Busy      { get; private set; }
        internal static string      Status    { get; private set; } = ""; // progress or the last error, shown on the server list page

        private static string CheckFile  { get { return Path.Combine(ModSyncPaths.CacheFolder, "update-check.json"); } }
        private static string UpdatesDir { get { return Path.Combine(ModSyncPaths.CacheFolder, "updates"); } }

        private class CheckState {
            public long   checkedTicks;
            public string json;      // the last release answer, so "available" survives restarts between checks
        }

        private class Fetched {
            public string text;
            public string error;
            public bool   ok { get { return error == null; } }
        }

        #region Check

        // Why updating isn't possible right now; null when it is
        internal static string CannotUpdateReason() {
            if(Busy) return "An update is already running.";
            if(ModManager.safeFile == null || !ModManager.safeFile.modSettings.checkForUpdates) return "Update checks are turned off.";
            if(!ModSyncPaths.Available || ModSyncPaths.Platform != "windows") return "Updating is only available on PC.";
            if(ModSwapClient.CanRestore || ModSwapClient.Busy) return "A mod swap is active. Restore your mods first.";
            if(ModManager.clientInstance != null || ModManager.serverInstance != null) return "Disconnect first.";
            if(InstalledFromModio()) return "AMP was installed from mod.io; mod.io updates it.";
            return null;
        }

        private static bool InstalledFromModio() {
            string folder = ModSwapClient.FindAmpFolder();
            return folder == null || File.Exists(Path.Combine(folder, "id.modio"));
        }

        // Whether the update button is offered
        internal static bool CanOfferUpdate { get { return Available != null && CannotUpdateReason() == null; } }

        internal static IEnumerator CheckRoutine() {
            yield return new WaitForSecondsRealtime(StartupDelay);
            string repo = ModManager.safeFile != null ? ModManager.safeFile.modSettings.updateRepo : null;
            if(!ReleaseInfo.IsValidRepo(repo)) {
                if(ModManager.safeFile != null && ModManager.safeFile.modSettings.checkForUpdates) Log.Warn(Defines.AMP, "Update check skipped: updateRepo must look like owner/name.");
                yield break;
            }
            // The button is hidden while the reason exists, but there is no point asking GitHub at all for these
            string reason = CannotUpdateReason();
            if(reason != null) yield break;

            CheckState state = ReadState();
            if(state != null && !ReleaseInfo.CheckDue(state.checkedTicks, DateTime.UtcNow.Ticks)) {
                Consider(ReleaseInfo.Parse(state.json));
                yield break;
            }

            var result = new Fetched();
            yield return Fetch("https://api.github.com/repos/" + repo + "/releases/latest", null, MaxApiBytes, ApiTimeout, null, result);
            if(!result.ok) {
                Log.Warn(Defines.AMP, "Update check failed: " + result.error); // offline or rate limited: try again at the next start
                yield break;
            }
            WriteState(new CheckState { checkedTicks = DateTime.UtcNow.Ticks, json = result.text });
            Consider(ReleaseInfo.Parse(result.text));
        }

        private static void Consider(ReleaseInfo info) {
            if(info == null || ReleaseInfo.ZipAsset(info) == null) return;
            if(ReleaseInfo.CompareVersions(info.version, Defines.COMMUNITY_VERSION) <= 0) return;
            Available = info;
            Log.Info(Defines.AMP, "AMP " + info.version + " is available (this is " + Defines.COMMUNITY_VERSION + ").");
            ModSwapClient.Notice("AMP " + info.version + " is available:\nBook > Mods > AMP > Update AMP");
            Changed();
        }

        private static CheckState ReadState() {
            try {
                return File.Exists(CheckFile) ? JsonConvert.DeserializeObject<CheckState>(File.ReadAllText(CheckFile)) : null;
            } catch(Exception e) when (e is JsonException || e is IOException || e is UnauthorizedAccessException) {
                return null;
            }
        }

        private static void WriteState(CheckState state) {
            try {
                Directory.CreateDirectory(ModSyncPaths.CacheFolder);
                File.WriteAllText(CheckFile, JsonConvert.SerializeObject(state));
            } catch(Exception e) when (e is IOException || e is UnauthorizedAccessException) {
                Log.Warn(Defines.AMP, "Couldn't save the update check time. " + e.Message);
            }
        }

        #endregion

        #region Update

        internal static void UpdateNow() {
            string reason = CannotUpdateReason();
            if(reason != null) { SetStatus(reason); return; }
            if(Available == null) return;
            Busy = true;
            SetStatus("Contacting GitHub...");
            ModManager.instance.StartCoroutine(UpdateRoutine(Available));
        }

        private static IEnumerator UpdateRoutine(ReleaseInfo release) {
            string error = null;
            bool launched = false;
            try {
                ReleaseAsset zip  = ReleaseInfo.ZipAsset(release);
                ReleaseAsset sums = release.assets.FirstOrDefault(a => a.name == SumsName);
                if(zip == null || sums == null) {
                    error = "The release has no " + (zip == null ? "package" : SumsName) + ".";
                } else if(zip.size > MaxZipBytes) {
                    error = "The release package is unexpectedly large.";
                } else {
                    var work = new UpdateWork { cacheRoot = ModSyncPaths.CacheFolder };
                    yield return Download(release, zip, sums, work);
                    error = work.error;
                    if(error == null) yield return Unpack(work);
                    error = work.error;
                    if(error == null) {
                        error = Launch(work); // the game closes shortly after this; Busy stays set until then
                        launched = error == null;
                    }
                }
            } finally {
                if(!launched) Busy = false;
            }
            if(error != null) SetStatus(error);
        }

        private class UpdateWork {
            public string    cacheRoot;
            public string    zipPath;
            public string    expectedSha;
            public CacheEntry entry;
            public string    version;
            public string    error;
        }

        private static IEnumerator Download(ReleaseInfo release, ReleaseAsset zip, ReleaseAsset sums, UpdateWork work) {
            try {
                if(Directory.Exists(UpdatesDir)) Directory.Delete(UpdatesDir, true);
                Directory.CreateDirectory(UpdatesDir);
            } catch(Exception e) when (e is IOException || e is UnauthorizedAccessException) {
                work.error = "Couldn't prepare the download folder: " + e.Message;
                yield break;
            }

            var sumsResult = new Fetched();
            yield return Fetch(sums.url, null, MaxSumsBytes, ApiTimeout, null, sumsResult);
            if(!sumsResult.ok) { work.error = "Couldn't download the checksums: " + sumsResult.error; yield break; }
            work.expectedSha = ReleaseInfo.ExpectedSha256(sumsResult.text, zip.name);
            if(work.expectedSha == null) { work.error = "The checksums don't list " + zip.name + "."; yield break; }

            work.zipPath = Path.Combine(UpdatesDir, "release.zip"); // a fixed local name: the asset name came from the network
            var zipResult = new Fetched();
            yield return Fetch(zip.url, work.zipPath, MaxZipBytes, DownloadTimeout, (done, total) => {
                SetStatus("Downloading AMP " + release.version + "... " + SyncPlan.Megabytes(done) + (total > 0 ? " of " + SyncPlan.Megabytes(total) : ""));
            }, zipResult);
            if(!zipResult.ok) { work.error = "Couldn't download AMP " + release.version + ": " + zipResult.error; yield break; }
            work.version = release.version;
        }

        // Hashing, unpacking and the cache commit on a worker thread
        private static IEnumerator Unpack(UpdateWork work) {
            SetStatus("Checking the download...");
            Task task = Task.Run(() => {
                string actual = FileSha256(work.zipPath);
                if(actual != work.expectedSha) {
                    File.Delete(work.zipPath);
                    throw new InvalidDataException("The download doesn't match its checksum. It was deleted.");
                }
                var cache = new ModCache(work.cacheRoot);
                cache.CleanPartial(TimeSpan.FromHours(1));
                string staging = cache.NewStagingFolder();
                try {
                    string folder = ReleasePackage.ExtractAmpFolder(work.zipPath, staging, MaxUnpackedBytes);
                    string[] files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
                    ModEntry entry = ModInventory.ReadEntry(folder, files);
                    if(entry == null) throw new InvalidDataException("The release has no readable manifest.json.");
                    entry.hash = ModInventory.HashFolder(folder, files, HashCache.Load(Path.Combine(staging, "nohashcache.json")));
                    work.entry = cache.Commit(folder, entry);
                    if(work.entry == null) throw new IOException("Couldn't store the new AMP in the cache.");
                } finally {
                    try { if(Directory.Exists(staging)) Directory.Delete(staging, true); } catch(Exception) { }
                }
                try { File.Delete(work.zipPath); } catch(Exception) { }
            });
            while(!task.IsCompleted) yield return null;
            if(task.IsFaulted) {
                Exception e = task.Exception.GetBaseException();
                Log.Err(Defines.AMP, "AMP update: " + e);
                work.error = "The update couldn't be unpacked: " + e.Message;
            }
        }

        // Journal with AMP's replacement, then the shared helper launch (it closes the game and relaunches it)
        private static string Launch(UpdateWork work) {
            string reason = CannotUpdateReason2();
            if(reason != null) return reason;
            try {
                string ampFolder = ModSwapClient.FindAmpFolder();
                ModEntry local = ampFolder == null ? null : ModInventory.ReadEntry(ampFolder, new string[0]);
                if(local == null) return "AMP's own mod folder wasn't found.";
                var installed = new ModEntry { folder = local.folder, name = work.entry.name, hash = work.entry.hash, isAmp = true, enabled = true };
                SwapJournal journal = JournalPlanner.BuildAmpUpdate(installed, local);
                SetStatus("Starting the update...");
                ModSwapClient.ApplyJournal(journal, SetStatus, "Closing the game to install AMP " + work.version + "...\nIt reopens by itself.", message => {
                    Busy = false; // the helper didn't start: nothing changed, the player may try again
                    SetStatus(message);
                });
                return null;
            } catch(Exception e) {
                Log.Err(Defines.AMP, "AMP update: couldn't start the helper. " + e);
                return "Couldn't start the update: " + e.Message;
            }
        }

        // The checks that can change while downloading (this routine already holds Busy)
        private static string CannotUpdateReason2() {
            if(ModSwapClient.CanRestore || ModSwapClient.Busy) return "A mod swap became active. Update cancelled.";
            if(ModManager.clientInstance != null || ModManager.serverInstance != null) return "You connected to a server. Update cancelled.";
            return null;
        }

        private static string FileSha256(string path) {
            using(var stream = File.OpenRead(path))
            using(SHA256 sha = SHA256.Create()) return HashCache.Hex(sha.ComputeHash(stream));
        }

        #endregion

        #region Network

        // GETs a trusted address, following redirects by hand (each hop must be trusted too). saveTo == null: the body is returned as text.
        private static IEnumerator Fetch(string url, string saveTo, long maxBytes, int timeout, Action<long, long> progress, Fetched result) {
            for(int hop = 0; hop <= MaxRedirects; hop++) {
                if(!ReleaseInfo.IsTrustedUrl(url)) { result.error = "Refused an untrusted address."; yield break; }
                using(var request = UnityWebRequest.Get(url)) {
                    request.timeout = timeout;
                    request.redirectLimit = 0;
                    request.SetRequestHeader("User-Agent", UserAgent);
                    if(saveTo == null) request.SetRequestHeader("Accept", "application/vnd.github+json");
                    else request.downloadHandler = new DownloadHandlerFile(saveTo) { removeFileOnAbort = true };

                    UnityWebRequestAsyncOperation op = request.SendWebRequest();
                    while(!op.isDone) {
                        long got = (long) request.downloadedBytes;
                        if(got > maxBytes) { request.Abort(); result.error = "The download is larger than expected."; yield break; }
                        if(progress != null) progress(got, ContentLength(request));
                        yield return null;
                    }

                    long code = request.responseCode;
                    if(code >= 300 && code < 400) {
                        string location = request.GetResponseHeader("Location");
                        Uri next;
                        if(string.IsNullOrEmpty(location) || !Uri.TryCreate(new Uri(url), location, out next)) { result.error = "Bad redirect."; yield break; }
                        url = next.AbsoluteUri;
                        continue;
                    }
                    if(request.result != UnityWebRequest.Result.Success) {
                        result.error = code == 403 || code == 429 ? "GitHub is limiting requests right now. Try again later." : request.error;
                        yield break;
                    }
                    if(saveTo == null) {
                        if(request.downloadedBytes > (ulong) maxBytes) { result.error = "The answer is larger than expected."; yield break; }
                        result.text = request.downloadHandler.text;
                    } else if(new FileInfo(saveTo).Length > maxBytes) {
                        result.error = "The download is larger than expected.";
                        yield break;
                    }
                    yield break;
                }
            }
            result.error = "Too many redirects.";
        }

        private static long ContentLength(UnityWebRequest request) {
            long length;
            return long.TryParse(request.GetResponseHeader("Content-Length"), out length) ? length : 0;
        }

        #endregion

        private static void SetStatus(string text) {
            Status = text ?? "";
            Changed();
        }

        private static void Changed() {
            UI.IngameModUI ui = UI.IngameModUI.currentUI;
            if(ui != null) ui.RefreshUpdateAmp();
        }
    }
}
