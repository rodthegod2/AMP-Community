# Mod Sync Phase 3a (Host → Joiner Mod Transfer + Cache) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A PC joiner can download mod folders from the host over the existing game connection into a verified local cache, without starving gameplay traffic. (Applying the downloads — swap, restart, profile — is phase 3b.)

**Architecture:** Game-independent core in `ModSync/`: `TransferRules` (safe paths, file lists, manifest DTOs), `TransferSender` (reads files, 32 KB chunks, credit window + rate limit, runs on its own thread), `TransferReceiver` (validates and writes chunks to a staging folder), `ModCache` (verify hash, commit, evict). All four are tested headlessly, including a sender↔receiver loop. Thin game glue: `Blob` + `BlobConverter` (fast byte payloads for Netamite), `ModFilePacket` (chunk), new `ModSyncPacket` kinds for control messages, `ModTransferHost`, `ModTransferClient`.

**Tech Stack:** C# 7.3 / net472, Netamite, Newtonsoft.Json.

**Research this relies on** (`C:\Projects\bns-mp\research\filexfer`, verified): `byte[]` SyncedVar fields are serialized per element (~1.5 MB/s) — never use them; a class field with a custom `DataConverterInterface<T>` is ~500 MB/s. Netamite packets have a `ushort` length prefix: a packet over 65,535 bytes silently corrupts the TCP stream. The reliable send queue is unbounded and shared with gameplay packets; an ack window of 4–8 × 32 KB keeps added gameplay latency under ~40 ms with a slow joiner. Packet handlers run on network threads.

## Conventions

Same as phases 1–2 (`docs/superpowers/plans/2026-10-09-mod-sync-phase2-settings.md` "Conventions"): repo `C:\Projects\bns-mp\amp`, branch `fix/review-pass`; C# 7.3; new files added to `Adammantium Multiplayer Mod.csproj`; build only with `bash /c/Projects/bns-mp/build.sh`; tests `Tests/ModSyncTests` (`dotnet build -nologo -v:q -nodeReuse:false -p:UseSharedCompilation=false && ./bin/Debug/net472/ModSyncTests.exe`), packettest (`OK, 52 packets` after this phase), policytest 15 PASS; one build at a time; never `dotnet build-server shutdown`; never write to the game folder; Edit tool for existing files; commits with `git -c user.name=amp-review -c user.email=noreply@example.invalid commit` and a Co-Authored-By trailer naming your model.

The test project references only pure files. `Blob.cs` references Netamite, so it is **not** added to the test project; the sender/receiver tests pass raw `byte[]`.

## File structure

| File | Responsibility |
|---|---|
| `ModSync/TransferRules.cs` (create) | `TransferFile`, `TransferManifest`, `ModsRequest`, `TransferEnd` DTOs; `NormalizeRelativePath`; `ListTransferFiles`. Pure. |
| `ModSync/ModInventory.cs` (modify) | Expose `IsHashedFile(relativePath)` so transfer and hash agree on which files count. |
| `ModSync/TransferSender.cs` (create) | Chunked file reader with credit window, rate limit, cancel, ack timeout. Pure (threads only). |
| `ModSync/TransferReceiver.cs` (create) | Validates manifest + chunks, writes staging files. Pure. |
| `ModSync/ModCache.cs` (create) | Staging dirs, verify-and-commit by hash, find, touch, evict, clean partials. Pure. |
| `ModSync/Blob.cs` (create) | `Blob` payload + `BlobConverter` for Netamite. |
| `Network/Packets/Implementation/ModFilePacket.cs` (create) | One file chunk (packet id 112). |
| `Network/Packets/PacketType.cs` (modify) | `MOD_FILE`. |
| `Network/Packets/Implementation/ModSyncPacket.cs` (modify) | Kinds 5–9 + routing. |
| `ModSync/ModTransferHost.cs` (create) | Host: serve requested mods, one sender thread per joiner, acks, cancel on disconnect. |
| `ModSync/ModTransferClient.cs` (create) | Joiner: request, receive, ack, commit to cache, progress callbacks. |
| `ModSync/ModSyncHost.cs` (modify) | Keep the scanned host mod list for `ModTransferHost`; forget transfers on disconnect. |
| `Data/SafeFile.cs` (modify) | `modUploadKBps`, `maxModTransferMB`, `modSettings.modCacheMaxMB`. |

---

### Task 1: TransferRules + hashed-file rule (pure, TDD)

- [ ] **Step 1: Tests.** Add `<Compile Include="..\..\ModSync\TransferRules.cs" />` to the test csproj. In `Program.cs` add `TransferRulesTests();` to `Main` and:

```csharp
    private static void TransferRulesTests() {
        T("plain path is kept", TransferRules.NormalizeRelativePath("Items/sword.json") == "Items/sword.json");
        T("backslashes become slashes", TransferRules.NormalizeRelativePath("Items\\sword.json") == "Items/sword.json");
        T("parent dir is rejected", TransferRules.NormalizeRelativePath("../evil.dll") == null);
        T("hidden parent dir is rejected", TransferRules.NormalizeRelativePath("Items/../../evil.dll") == null);
        T("rooted path is rejected", TransferRules.NormalizeRelativePath("C:/Windows/evil.dll") == null && TransferRules.NormalizeRelativePath("/etc/x") == null);
        T("drive-relative path is rejected", TransferRules.NormalizeRelativePath("C:evil.dll") == null);
        T("empty and dot segments are rejected", TransferRules.NormalizeRelativePath("") == null && TransferRules.NormalizeRelativePath("a//b") == null && TransferRules.NormalizeRelativePath("./a") == null);
        T("invalid file name chars are rejected", TransferRules.NormalizeRelativePath("a|b.json") == null);
        T("reserved Windows names are rejected", TransferRules.NormalizeRelativePath("Items/CON.json") == null && TransferRules.NormalizeRelativePath("nul") == null);

        using(var f = new TestFolders()) {
            string mod = f.Mod("ModA");
            f.File_(mod, "Items/sword.json", "{}");
            f.File_(mod, "id.modio", "{\"id\":1}");
            f.File_(mod, "Thumbs.db", "x");
            f.File_(mod, "log.log", "x");
            var files = TransferRules.ListTransferFiles(mod);
            T("transfer list has the hashed files only", files.Select(x => x.path).OrderBy(p => p).SequenceEqual(new[] { "Items/sword.json", "manifest.json" }));
            T("transfer list has sizes", files.All(x => x.size > 0));
        }
    }
```

- [ ] **Step 2: Run** → fails to build.

- [ ] **Step 3: Implement.** In `ModSync/ModInventory.cs`, move the existing ignore checks used by `HashFolder` into one method and use it there (behaviour unchanged):

```csharp
        // Files that count for a mod's hash (and therefore get transferred)
        internal static bool IsHashedFile(string relativePath) {
            string name = Path.GetFileName(relativePath);
            if(HashIgnoredFiles.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
            if(name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
```

(Adapt to the actual current ignore logic in `HashFolder` — read it first; `HashFolder` must call `IsHashedFile` so both stay identical.)

`ModSync/TransferRules.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AMP.ModSync {
    public class TransferFile {
        public string path; // relative, '/' separated, validated
        public long   size;
    }

    // Host -> joiner: one mod folder about to be sent
    public class TransferManifest {
        public int                transferId;
        public string             folder;
        public string             hash;
        public long               modioId;
        public List<TransferFile> files = new List<TransferFile>();

        public long TotalBytes { get { return files.Sum(f => f.size); } }
    }

    // Joiner -> host
    public class ModsRequest {
        public List<string> folders = new List<string>();
    }

    // Host -> joiner: a transfer finished or failed on the host side
    public class TransferEnd {
        public int    transferId;
        public bool   ok;
        public string error = "";
    }

    public static class TransferRules {
        private static readonly string[] ReservedNames = { "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

        // A path from the network, made safe to combine with a target folder; null if it could escape it or isn't a valid file name
        public static string NormalizeRelativePath(string path) {
            if(string.IsNullOrEmpty(path)) return null;
            path = path.Replace('\\', '/');
            if(path.StartsWith("/") || path.Contains(":")) return null;

            string[] parts = path.Split('/');
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach(string part in parts) {
                if(part.Length == 0 || part == "." || part == "..") return null;
                if(part.IndexOfAny(invalid) >= 0) return null;
                if(part.EndsWith(".") || part.EndsWith(" ")) return null;
                string stem = part.Split('.')[0];
                if(ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase)) return null;
            }
            return string.Join("/", parts);
        }

        public static List<TransferFile> ListTransferFiles(string folder) {
            return Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                            .Select(f => new { full = f, rel = ModInventory.RelativePath(folder, f) })
                            .Where(f => ModInventory.IsHashedFile(f.rel))
                            .OrderBy(f => f.rel, StringComparer.Ordinal)
                            .Select(f => new TransferFile { path = f.rel, size = new FileInfo(f.full).Length })
                            .ToList();
        }
    }
}
```

- [ ] **Step 4: Run tests** → all pass (existing ones too).
- [ ] **Step 5:** Add `ModSync\TransferRules.cs` to the mod csproj, build, commit `Mod transfer: safe paths and file lists`.

---

### Task 2: TransferSender + TransferReceiver (pure, TDD)

- [ ] **Step 1: Tests.** Add both files to the test csproj. Add `TransferLoopTests();` to `Main` and:

```csharp
    // Runs a real sender thread against a receiver, acks flowing back like over the network
    private static bool Loop(TestFolders f, string source, TransferManifest manifest, int window, out string target, Func<int, bool> dropAck = null) {
        target = Path.Combine(f.Root, "staging-" + Guid.NewGuid().ToString("N"));
        var receiver = TransferReceiver.Start(manifest, target, long.MaxValue);
        TransferSender sender = null;
        int chunkNo = 0;
        sender = new TransferSender(manifest, source, window, 0, (file, offset, data) => {
            if(receiver.OnChunk(file, offset, data) && (dropAck == null || !dropAck(chunkNo++))) sender.Ack();
        }) { AckTimeoutMs = 500 };
        bool ok = sender.Run();
        receiver.Dispose();
        return ok && receiver.Complete;
    }

    private static void TransferLoopTests() {
        using(var f = new TestFolders()) {
            string mod = f.Mod("Big");
            var rng = new Random(7);
            byte[] big = new byte[TransferSender.ChunkSize * 3 + 123];
            rng.NextBytes(big);
            File.WriteAllBytes(Path.Combine(mod, "assets.bundle"), big);
            f.File_(mod, "Items/empty.json", "");
            var manifest = new TransferManifest { transferId = 1, folder = "Big", files = TransferRules.ListTransferFiles(mod) };

            string target;
            T("multi-chunk transfer completes", Loop(f, mod, manifest, 4, out target));
            T("bytes are identical", File.ReadAllBytes(Path.Combine(target, "assets.bundle")).SequenceEqual(big));
            T("empty files are created", File.Exists(Path.Combine(target, "Items", "empty.json")) && new FileInfo(Path.Combine(target, "Items", "empty.json")).Length == 0);
            T("window of 1 still completes", Loop(f, mod, manifest, 1, out target));
            T("missing acks time out instead of hanging", !Loop(f, mod, manifest, 2, out target, n => n >= 1));

            var receiver = TransferReceiver.Start(manifest, Path.Combine(f.Root, "r2"), long.MaxValue);
            T("chunk for unknown file is refused", !receiver.OnChunk(99, 0, new byte[1]));
            T("out-of-order chunk is refused", !receiver.OnChunk(0, 5, new byte[1]));
            T("chunk past the file size is refused", !receiver.OnChunk(1, 0, new byte[1]));
            receiver.Dispose();

            bool threw = false;
            try { TransferReceiver.Start(new TransferManifest { files = { new TransferFile { path = "../x.dll", size = 1 } } }, Path.Combine(f.Root, "r3"), long.MaxValue); }
            catch(InvalidDataException) { threw = true; }
            T("unsafe path in manifest is refused", threw);

            threw = false;
            try { TransferReceiver.Start(manifest, Path.Combine(f.Root, "r4"), 10); }
            catch(InvalidDataException) { threw = true; }
            T("manifest over the size limit is refused", threw);

            var cancelSender = new TransferSender(manifest, mod, 1, 0, (a, b, c) => { }) { AckTimeoutMs = 5000 };
            var thread = new System.Threading.Thread(() => cancelSender.Run());
            thread.Start();
            System.Threading.Thread.Sleep(100);
            cancelSender.Cancel();
            T("cancel stops a waiting sender quickly", thread.Join(2000));
        }
    }
```

- [ ] **Step 2: Run** → fails to build.

- [ ] **Step 3: Implement `ModSync/TransferSender.cs`:**

```csharp
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace AMP.ModSync {
    // Sends one mod folder as 32 KB chunks. Blocking: run it on its own thread.
    // At most `window` chunks are unacknowledged at any time, so the shared reliable queue (and gameplay latency) stays small.
    public sealed class TransferSender {
        public const int ChunkSize = 32 * 1024; // packet stays far below Netamite's 64 KB limit

        public int AckTimeoutMs = 30 * 1000;

        private readonly TransferManifest manifest;
        private readonly string sourceFolder;
        private readonly int window;
        private readonly int bytesPerSecond;               // 0 = unlimited
        private readonly Action<int, long, byte[]> sendChunk; // (file index, offset, data)
        private readonly object gate = new object();
        private int inFlight;
        private volatile bool cancelled;

        public TransferSender(TransferManifest manifest, string sourceFolder, int window, int bytesPerSecond, Action<int, long, byte[]> sendChunk) {
            this.manifest       = manifest;
            this.sourceFolder   = sourceFolder;
            this.window         = Math.Max(1, window);
            this.bytesPerSecond = Math.Max(0, bytesPerSecond);
            this.sendChunk      = sendChunk;
        }

        public void Ack() {
            lock(gate) {
                if(inFlight > 0) inFlight--;
                Monitor.PulseAll(gate);
            }
        }

        public void Cancel() {
            cancelled = true;
            lock(gate) Monitor.PulseAll(gate);
        }

        // True when every chunk was sent; false when cancelled, the joiner stopped acking, or a file changed
        public bool Run() {
            var clock = Stopwatch.StartNew();
            long sent = 0;
            for(int index = 0; index < manifest.files.Count; index++) {
                TransferFile file = manifest.files[index];
                using(var stream = new FileStream(Path.Combine(sourceFolder, file.path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, ChunkSize)) {
                    long offset = 0;
                    do {
                        int length = (int) Math.Min(ChunkSize, file.size - offset);
                        byte[] buffer = new byte[length];
                        if(!ReadExactly(stream, buffer)) return false; // file shrank since the manifest was made
                        if(!WaitForCredit()) return false;
                        sendChunk(index, offset, buffer);
                        offset += length;
                        sent   += length;
                        Throttle(sent, clock);
                    } while(offset < file.size);
                }
            }
            return !cancelled;
        }

        private bool WaitForCredit() {
            lock(gate) {
                while(inFlight >= window && !cancelled) {
                    if(!Monitor.Wait(gate, AckTimeoutMs)) return false;
                }
                if(cancelled) return false;
                inFlight++;
                return true;
            }
        }

        private void Throttle(long sent, Stopwatch clock) {
            if(bytesPerSecond == 0) return;
            long due = sent * 1000L / bytesPerSecond;
            long wait = due - clock.ElapsedMilliseconds;
            if(wait > 0) Thread.Sleep((int) Math.Min(wait, 1000));
        }

        private static bool ReadExactly(Stream stream, byte[] buffer) {
            int read = 0;
            while(read < buffer.Length) {
                int n = stream.Read(buffer, read, buffer.Length - read);
                if(n <= 0) return false;
                read += n;
            }
            return true;
        }
    }
}
```

`ModSync/TransferReceiver.cs`:

```csharp
using System;
using System.IO;
using System.Linq;

namespace AMP.ModSync {
    // Writes one incoming mod folder into a staging folder. Chunks must arrive in order (the sender sends them in order over a reliable stream).
    public sealed class TransferReceiver : IDisposable {
        private readonly TransferManifest manifest;
        private readonly string stagingFolder;
        private readonly string[] safePaths;
        private readonly long[] written;
        private readonly bool[] finished;
        private int remaining;
        private int openIndex = -1;
        private FileStream open;

        public long TotalBytes    { get; private set; }
        public long ReceivedBytes { get; private set; }
        public bool Complete      { get; private set; }

        private TransferReceiver(TransferManifest manifest, string stagingFolder, string[] safePaths) {
            this.manifest      = manifest;
            this.stagingFolder = stagingFolder;
            this.safePaths     = safePaths;
            written            = new long[manifest.files.Count];
            finished           = new bool[manifest.files.Count];
            remaining          = manifest.files.Count;
            TotalBytes         = manifest.TotalBytes;
            Complete           = remaining == 0;
        }

        // Throws InvalidDataException for a manifest we must not accept
        public static TransferReceiver Start(TransferManifest manifest, string stagingFolder, long maxBytes) {
            if(manifest == null || manifest.files == null) throw new InvalidDataException("No file list.");
            string[] safe = manifest.files.Select(f => f == null ? null : TransferRules.NormalizeRelativePath(f.path)).ToArray();
            if(safe.Any(p => p == null)) throw new InvalidDataException("Unsafe file path in mod.");
            if(safe.Distinct(StringComparer.OrdinalIgnoreCase).Count() != safe.Length) throw new InvalidDataException("Duplicate file in mod.");
            if(manifest.files.Any(f => f.size < 0)) throw new InvalidDataException("Invalid file size.");
            if(manifest.TotalBytes > maxBytes) throw new InvalidDataException("Mod is larger than the allowed download size.");

            if(Directory.Exists(stagingFolder)) Directory.Delete(stagingFolder, true);
            Directory.CreateDirectory(stagingFolder);
            return new TransferReceiver(manifest, stagingFolder, safe);
        }

        // True if the chunk was written (acknowledge it); false if it doesn't fit the manifest (abort the transfer)
        public bool OnChunk(int fileIndex, long offset, byte[] data) {
            if(Complete || data == null || fileIndex < 0 || fileIndex >= written.Length) return false;
            if(finished[fileIndex]) return false; // a repeated chunk must never reopen (and truncate) a finished file
            if(offset != written[fileIndex]) return false;
            if(offset + data.Length > manifest.files[fileIndex].size) return false;
            if(fileIndex < openIndex) return false;

            if(fileIndex != openIndex) {
                CloseOpen();
                string path = Path.Combine(stagingFolder, safePaths[fileIndex].Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                open = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                openIndex = fileIndex;
            }

            open.Write(data, 0, data.Length);
            written[fileIndex] += data.Length;
            ReceivedBytes      += data.Length;
            if(written[fileIndex] == manifest.files[fileIndex].size) {
                CloseOpen();
                finished[fileIndex] = true;
                remaining--;
            }

            Complete = remaining == 0;
            return true;
        }

        private void CloseOpen() {
            if(open != null) open.Dispose();
            open = null;
            openIndex = -1;
        }

        public void Dispose() { CloseOpen(); }
    }
}
```

Note for the implementer: a zero-length file is sent as one empty chunk at offset 0; `OnChunk` opens (creates) the file, writes nothing and closes it because `written == size`. Make sure the test for empty files passes with that path; files the sender skips would otherwise never be created. Finished files are tracked so a repeated chunk can't truncate them; add a test: after a full transfer, `OnChunk(0, 0, new byte[0])` returns false.

- [ ] **Step 4: Run tests** → all pass. **Step 5:** add both files to the mod csproj, build, commit `Mod transfer: chunk sender with ack window and validating receiver`.

---

### Task 3: ModCache (pure, TDD)

- [ ] **Step 1: Tests** (add `ModCache.cs` to the test csproj; `CacheTests();` in `Main`):

```csharp
    private static void CacheTests() {
        using(var f = new TestFolders()) {
            string mod = f.Mod("Cached");
            f.File_(mod, "Items/a.json", "{\"a\":1}");
            ModEntry entry = ModInventory.Scan(f.Mods, f.Cache, true).Single(m => m.folder == "Cached");
            var cache = new ModCache(Path.Combine(f.Root, "cacheroot"));

            string staging = cache.NewStagingFolder();
            foreach(TransferFile file in TransferRules.ListTransferFiles(mod)) {
                string dst = Path.Combine(staging, file.path);
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Copy(Path.Combine(mod, file.path), dst);
            }
            CacheEntry committed = cache.Commit(staging, entry);
            T("matching hash is committed", committed != null && committed.hash == entry.hash);
            T("staging folder is gone after commit", !Directory.Exists(staging));
            T("committed entry is found by hash", cache.Find(entry.hash) != null && Directory.Exists(cache.ContentFolder(entry.hash)));

            string bad = cache.NewStagingFolder();
            File.WriteAllText(Path.Combine(bad, "manifest.json"), "{}");
            T("wrong content is rejected and deleted", cache.Commit(bad, entry) == null && !Directory.Exists(bad));

            string again = cache.NewStagingFolder();
            File.Copy(Path.Combine(mod, "manifest.json"), Path.Combine(again, "manifest.json"));
            Directory.CreateDirectory(Path.Combine(again, "Items"));
            File.Copy(Path.Combine(mod, "Items", "a.json"), Path.Combine(again, "Items", "a.json"));
            T("committing an already cached hash keeps one copy", cache.Commit(again, entry) != null && !Directory.Exists(again));

            cache.Evict(0, new[] { entry.hash });
            T("protected entries survive eviction", cache.Find(entry.hash) != null);
            cache.Evict(0, new string[0]);
            T("eviction removes entries over the limit", cache.Find(entry.hash) == null);

            Directory.CreateDirectory(cache.NewStagingFolder());
            cache.CleanPartial();
            T("leftover partial downloads are cleaned", !Directory.EnumerateDirectories(Path.Combine(f.Root, "cacheroot", "partial")).Any());
        }
    }
```

- [ ] **Step 2: Run** → fails. **Step 3: Implement `ModSync/ModCache.cs`:**

```csharp
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AMP.ModSync {
    public class CacheEntry {
        public string hash;
        public string folder;
        public string name;
        public long   modioId;
        public long   sizeBytes;
        public long   lastUsedTicks;
    }

    // Downloaded mods, stored by content hash outside the Mods folder so the game never loads them:
    //   <root>/mods/<hash>/content/...   the mod folder's files
    //   <root>/mods/<hash>/entry.json    CacheEntry
    //   <root>/partial/<guid>/           downloads in progress
    public sealed class ModCache {
        private readonly string root;

        public ModCache(string root) { this.root = root; }

        private string ModsRoot    { get { return Path.Combine(root, "mods"); } }
        private string PartialRoot { get { return Path.Combine(root, "partial"); } }

        public string ContentFolder(string hash) { return Path.Combine(ModsRoot, hash, "content"); }
        private string EntryFile(string hash)   { return Path.Combine(ModsRoot, hash, "entry.json"); }

        public string NewStagingFolder() {
            string path = Path.Combine(PartialRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        // Verifies the staged files against the expected mod hash and moves them into the cache. Null (and staging deleted) on mismatch.
        public CacheEntry Commit(string stagingFolder, ModEntry expected) {
            try {
                string[] files = Directory.GetFiles(stagingFolder, "*", SearchOption.AllDirectories);
                string hash = ModInventory.HashFolder(stagingFolder, files, HashCache.Load(Path.Combine(stagingFolder, "..", "nohashcache.json")));
                if(!IsSafeKey(expected.hash) || hash != expected.hash) {
                    Directory.Delete(stagingFolder, true);
                    return null;
                }

                string content = ContentFolder(hash);
                if(Directory.Exists(content)) {
                    Directory.Delete(stagingFolder, true);
                } else {
                    Directory.CreateDirectory(Path.GetDirectoryName(content));
                    Directory.Move(stagingFolder, content);
                }

                var entry = new CacheEntry {
                    hash          = hash,
                    folder        = expected.folder,
                    name          = expected.name,
                    modioId       = expected.modioId,
                    sizeBytes     = files.Sum(f => new FileInfo(Path.Combine(content, ModInventory.RelativePath(stagingFolder, f))).Length),
                    lastUsedTicks = DateTime.UtcNow.Ticks,
                };
                File.WriteAllText(EntryFile(hash), JsonConvert.SerializeObject(entry));
                return entry;
            } catch(IOException) {
                TryDelete(stagingFolder);
                return null;
            } catch(UnauthorizedAccessException) {
                TryDelete(stagingFolder);
                return null;
            }
        }

        public CacheEntry Find(string hash) {
            if(!IsSafeKey(hash) || !Directory.Exists(ContentFolder(hash)) || !File.Exists(EntryFile(hash))) return null;
            try {
                return JsonConvert.DeserializeObject<CacheEntry>(File.ReadAllText(EntryFile(hash)));
            } catch(JsonException) {
                return null;
            } catch(IOException) {
                return null;
            }
        }

        public void Touch(string hash) {
            CacheEntry entry = Find(hash);
            if(entry == null) return;
            entry.lastUsedTicks = DateTime.UtcNow.Ticks;
            try { File.WriteAllText(EntryFile(hash), JsonConvert.SerializeObject(entry)); } catch(IOException) { }
        }

        // Deletes least recently used entries until the cache is at most maxBytes; `keep` hashes are never deleted
        public void Evict(long maxBytes, ICollection<string> keep) {
            if(!Directory.Exists(ModsRoot)) return;
            List<CacheEntry> entries = Directory.GetDirectories(ModsRoot).Select(d => Find(Path.GetFileName(d))).Where(e => e != null).ToList();
            long total = entries.Sum(e => e.sizeBytes);
            foreach(CacheEntry entry in entries.OrderBy(e => e.lastUsedTicks)) {
                if(total <= maxBytes) break;
                if(keep != null && keep.Contains(entry.hash)) continue;
                if(TryDelete(Path.Combine(ModsRoot, entry.hash))) total -= entry.sizeBytes;
            }
        }

        public void CleanPartial() {
            if(!Directory.Exists(PartialRoot)) return;
            foreach(string dir in Directory.GetDirectories(PartialRoot)) TryDelete(dir);
        }

        // Hashes come from the network: only accept 64 lowercase hex chars as folder names
        private static bool IsSafeKey(string hash) {
            return hash != null && hash.Length == 64 && hash.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
        }

        private static bool TryDelete(string dir) {
            try {
                if(Directory.Exists(dir)) Directory.Delete(dir, true);
                return true;
            } catch(IOException) {
                return false;
            } catch(UnauthorizedAccessException) {
                return false;
            }
        }
    }
}
```

Check `ModInventory.HashFolder`'s current signature (it takes the folder, its file list and a `HashCache`) and the `HashCache.Load` behaviour for a missing file (returns an empty cache) — adapt the call if names differ. The throwaway hash cache path must not be saved into the staging folder (it would change the hash); if `HashCache` only saves on `Save()`, never call `Save()` here.

- [ ] **Step 4: Run tests.** **Step 5:** add to the mod csproj, build, commit `Mod transfer: verified download cache`.

---

### Task 4: Wire it into the game (packets, host, joiner)

This task is game glue; it is verified by building, by packettest (new packet round-trips) and by review.

- [ ] **Step 1: `ModSync/Blob.cs`** (mod csproj only):

```csharp
using Netamite.Converter;
using System;

namespace AMP.ModSync {
    // Byte payload for packets. A plain byte[] field is serialized one boxed element at a time (~1.5 MB/s);
    // a class field with its own converter is copied in one go. Netamite finds the converter by scanning loaded assemblies.
    public class Blob {
        public byte[] bytes = new byte[0];
    }

    public class BlobConverter : DataConverterInterface<Blob> {
        public static new byte[] GetBytes(Blob value, bool lowPrecision = false) {
            byte[] source = value == null || value.bytes == null ? new byte[0] : value.bytes;
            if(source.Length > ushort.MaxValue) throw new ArgumentException("Blob too large for one packet.");
            byte[] output = new byte[2 + source.Length];
            BitConverter.GetBytes((ushort) source.Length).CopyTo(output, 0);
            Buffer.BlockCopy(source, 0, output, 2, source.Length);
            return output;
        }

        public static new Blob FromBytes(byte[] value, out int bytesRead, int startIndex = 0, bool lowPrecision = false) {
            int length = BitConverter.ToUInt16(value, startIndex);
            byte[] bytes = new byte[length];
            Buffer.BlockCopy(value, startIndex + 2, bytes, 0, length);
            bytesRead = 2 + length;
            return new Blob { bytes = bytes };
        }
    }
}
```

Verify the namespace of `DataConverterInterface<T>` in `C:\Projects\bns-mp\netamite\Netamite.Base\Converter\DataConverterInterface.cs` and the experiment `C:\Projects\bns-mp\research\filexfer\Blob.cs` (which works).

- [ ] **Step 2: Packet.** `PacketType.cs`: add `MOD_FILE,` right after `MOD_SYNC,` (=112). Create `Network/Packets/Implementation/ModFilePacket.cs`:

```csharp
using AMP.ModSync;
using AMP.Network.Data;
using Netamite.Client.Definition;
using Netamite.Network.Packet;
using Netamite.Network.Packet.Attributes;
using Netamite.Server.Definition;

namespace AMP.Network.Packets.Implementation {
    // One 32 KB piece of a mod file, host -> joiner
    [PacketDefinition((byte) PacketType.MOD_FILE)]
    public class ModFilePacket : AMPPacket {
        [SyncedVar] public int  transferId;
        [SyncedVar] public int  fileIndex;
        [SyncedVar] public long offset;
        [SyncedVar] public Blob data;

        public ModFilePacket() { }

        public ModFilePacket(int transferId, int fileIndex, long offset, byte[] bytes) {
            this.transferId = transferId;
            this.fileIndex  = fileIndex;
            this.offset     = offset;
            this.data       = new Blob { bytes = bytes };
        }

        public override bool ProcessClient(NetamiteClient client) {
            ModTransferClient.OnChunk(this);
            return true;
        }

        public override bool ProcessServer(NetamiteServer server, ClientData client) {
            return true; // chunks only go host -> joiner
        }
    }
}
```

Confirm `long` is a supported SyncedVar type (`LongConverter` exists in Netamite). packettest's random filler must handle a `Blob` field: it fills class fields with a default constructor recursively and `Blob` has no `[SyncedVar]` fields, so it round-trips an empty Blob — run packettest and confirm `OK, 52 packets`; if packettest can't handle it, extend `packettest/Program.cs` `RandomValue` with a `Blob` case (random 100 bytes) and compare `bytes` in `Same`.

- [ ] **Step 3: Settings** (`Data/SafeFile.cs`): in `HostingSettings` add

```csharp
            // Mod downloads from the host: upload speed limit per joiner (KB/s, 0 = unlimited) and largest download a joiner may request
            public int      modUploadKBps          = 4096;
            public int      maxModTransferMB       = 4096;
```

and in `ModSettings` add

```csharp
            // Size limit of the downloaded-mods cache (AMP_ModCache)
            public int      modCacheMaxMB          = 8192;
```

- [ ] **Step 4: Control messages** in `ModSyncPacket.cs`: add kinds

```csharp
        public const byte MODS_REQUEST     = 5; // joiner -> host: ModsRequest JSON
        public const byte TRANSFER_START   = 6; // host -> joiner: TransferManifest JSON
        public const byte CHUNK_ACK        = 7; // joiner -> host: transferId, no data
        public const byte TRANSFER_END     = 8; // host -> joiner: TransferEnd JSON
        public const byte TRANSFER_CANCEL  = 9; // joiner -> host: stop sending
```

Routing: `ProcessClient` sends `TRANSFER_START`/`TRANSFER_END` to `ModTransferClient.OnPacket(this)`; `ProcessServer` sends `MODS_REQUEST`/`CHUNK_ACK`/`TRANSFER_CANCEL` to `ModTransferHost.OnPacket(client, this)` and everything else to `ModSyncHost.OnPacket` as today. `CHUNK_ACK` is a single packet (`parts = 1`, `data = ""`) and must not go through any `MessageAssembler`. Note `ModSyncHost.OnPacket` currently ignores packets with `parts` outside 1..10 — keep that for JOINER_REPORT only.

- [ ] **Step 5: `ModSync/ModTransferHost.cs`.** Requirements (write it in the style of `ModSyncHost`):
  - `ModSyncHost` keeps the enabled mod list from `Prepare`'s scan in `internal static List<ModEntry> HostMods` (set inside the Prepare task; null until done). Also keep the Mods folder path captured in Prepare.
  - `OnPacket(ClientData client, ModSyncPacket p)` on network threads:
    - `MODS_REQUEST`: assemble with its own `MessageAssembler` keyed `clientId:transferId` (max 10 parts), parse `ModsRequest`. Ignore unless `ModSyncHost.IsPlaying(client)` is false-and-state is SETUP/CHECKING… simpler rule: accept requests only from connected clients whose `modSyncState` is "setup" or "checking" (they're the ones who need mods) — add `ModSyncHost.IsSettingUp(client)`. Only serve folders that are in `HostMods` (match `folder` exactly), not AMP's own folder (that's phase 3b self-update), ignore unknown names. If the joiner already has an active transfer, cancel it first.
    - Start one background `Thread` (IsBackground = true, name "AMP mod upload <id>") per joiner that, for each requested folder in order: builds a `TransferManifest` (`transferId` = new id from an `Interlocked` counter, `folder`, `hash`, `modioId` from HostMods, `files = TransferRules.ListTransferFiles(path)`), refuses (TransferEnd ok=false, "too large") if `TotalBytes` > `maxModTransferMB` MB, sends `TRANSFER_START` (JSON via `ModSyncPacket.Create`), runs `TransferSender` with window 8 and `modUploadKBps * 1024`, each chunk sent with `netamiteServer.SendTo(client, new ModFilePacket(...))`, then sends `TRANSFER_END` (ok = sender result). Stops on failure or cancel. Catches all exceptions inside the thread (log, send TRANSFER_END ok=false).
    - `CHUNK_ACK`: `sender.Ack()` for that client's active sender if `p.transferId` matches its current transfer.
    - `TRANSFER_CANCEL`: cancel that client's sender.
  - `OnClientDisconnected(client)` (call it from `ModSyncHost.OnClientDisconnected`): cancel and forget that client's transfer. `StopHost` path: cancel all (add `ModTransferHost.StopAll()` and call it where `SettingsSyncHost.Stop()` is called).
  - Keep per-client state in a `ConcurrentDictionary<int, ActiveUpload>` (`ActiveUpload`: sender, current transferId, thread, client reference).

- [ ] **Step 6: `ModSync/ModTransferClient.cs`.** Requirements:
  - `internal static void Download(List<ModEntry> mods, Action<long, long, string> progress, Action<bool, string> done)` (main thread): ignores mods already in the cache (`ModCache.Find(mod.hash)` → touch); for the rest sends `MODS_REQUEST` with their folders; remembers the expected `ModEntry` by folder; `progress(received, total, modName)` and `done(ok, error)` are always invoked on the main thread via `Dispatcher.Enqueue`. If nothing needs downloading, call `done(true, "")` immediately.
  - Cache: `new ModCache(ModSyncPaths.CacheFolder)`; on first use call `CleanPartial()`.
  - `OnPacket` (network thread): `TRANSFER_START` → assemble, parse manifest, check `manifest.folder` is one we asked for and `manifest.hash` equals the expected hash; `TransferReceiver.Start(manifest, cache.NewStagingFolder(), maxBytes)` where maxBytes = expected `sizeBytes` × 2 + 1 MB (the host can't push more than about what it advertised). On `InvalidDataException`: send `TRANSFER_CANCEL`, `done(false, message)`.
  - `OnChunk(ModFilePacket p)` (network thread): if `p.transferId` matches the active receiver, `receiver.OnChunk(p.fileIndex, p.offset, p.data.bytes)`; if accepted send a `CHUNK_ACK` (`new ModSyncPacket(CHUNK_ACK, p.transferId, 0, 1, "")` with `SendToServerReliable`) and report progress at most every 250 ms; if refused: cancel + `done(false, "...")`. When `receiver.Complete`: dispose it, `cache.Commit(staging, expected)`; null → `done(false, "<mod> failed verification")` and cancel; else continue with the next manifest. When every requested mod is committed → `cache.Evict(modCacheMaxMB MB, hashes of this download)` then `done(true, "")`.
  - `TRANSFER_END` with ok=false → `done(false, error)`.
  - Commit runs hashing on the network thread; that's acceptable for phase 3a (a few seconds for big mods). Note it as a concern if you see a better place.
  - `Cancel()` (main thread): send `TRANSFER_CANCEL`, dispose the receiver, clean the staging folder. Call it from `ModSyncClient.Reset()` and on disconnect (`ModManager.StopClientImmediatly`).
  - Capture `ModManager.clientInstance` at `Download` and ignore packets once it changed (same pattern as `ModSyncClient`).
  - Strip nothing yet; `id.modio` is not transferred at all because it isn't a hashed file (Task 1). Keep the mod.io id in `CacheEntry.modioId` (already).

- [ ] **Step 7:** csproj entries for `Blob.cs`, `ModFilePacket.cs`, `ModTransferHost.cs`, `ModTransferClient.cs`. Build, run ModSyncTests, packettest (`OK, 52 packets`), policytest. Commit `Mod transfer: host serves mods to joiners over the game connection`.

### Task 5: Verify, review, push

- [ ] All suites + nettest; final review (superpowers:code-reviewer, emphasis on thread safety, memory, malicious input from either side); fix; push `fork fix/review-pass:community`.

No user-facing button yet: phase 3b adds "Download & restart" to the mod sync page and applies the cache. Phase 3b's in-game test covers this transfer end to end.
