using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using AMP.ModSync;

internal static class Program {
    private static int fails;

    private static void T(string name, bool ok) {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if(!ok) fails++;
    }

    private static int Main() {
        InventoryTests();
        PlannerTests();
        ChunkTests();
        SettingsTests();
        TransferRulesTests();
        TransferLoopTests();
        CacheTests();
        HardeningTests();
        JournalTests();
        ApplierTests();
        HelperTests();
        ProfileTests();
        ReleaseInfoTests();
        ReleasePackageTests();
        AutopilotTests();
        RelaunchTests();
        ModioTests();
        Console.WriteLine(fails == 0 ? "ALL PASSED" : fails + " FAILED");
        return fails;
    }

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
            T("chunk past the file size is refused", !receiver.OnChunk(0, 0, new byte[1]));
            receiver.Dispose();

            bool threw = false;
            try { TransferReceiver.Start(new TransferManifest { files = { new TransferFile { path = "../x.dll", size = 1 } } }, Path.Combine(f.Root, "r3"), long.MaxValue); }
            catch(InvalidDataException) { threw = true; }
            T("unsafe path in manifest is refused", threw);

            threw = false;
            try { TransferReceiver.Start(manifest, Path.Combine(f.Root, "r4"), 10); }
            catch(InvalidDataException) { threw = true; }
            T("manifest over the size limit is refused", threw);

            var done = TransferReceiver.Start(new TransferManifest { files = { new TransferFile { path = "e.json", size = 0 } } }, Path.Combine(f.Root, "r5"), long.MaxValue);
            T("empty file is created by its single empty chunk", done.OnChunk(0, 0, new byte[0]) && done.Complete);
            T("repeated chunk after completion is refused", !done.OnChunk(0, 0, new byte[0]));
            done.Dispose();

            var cancelSender = new TransferSender(manifest, mod, 1, 0, (a, b, c) => { }) { AckTimeoutMs = 5000 };
            var thread = new System.Threading.Thread(() => cancelSender.Run());
            thread.Start();
            System.Threading.Thread.Sleep(100);
            cancelSender.Cancel();
            T("cancel stops a waiting sender quickly", thread.Join(2000));
        }
    }

    private static void HardeningTests() {
        byte[] empty = TransferRules.DecodeChunk("");
        T("empty chunk decodes to no bytes", empty != null && empty.Length == 0);
        T("null chunk counts as empty", TransferRules.DecodeChunk(null).Length == 0);
        T("valid chunk decodes", TransferRules.DecodeChunk(Convert.ToBase64String(new byte[] { 1, 2, 3 })).Length == 3);
        T("invalid chunk is null", TransferRules.DecodeChunk("not base64!") == null);

        var huge = new TransferManifest { files = { new TransferFile { path = "a", size = long.MaxValue }, new TransferFile { path = "b", size = long.MaxValue } } };
        using(var f = new TestFolders()) {
            bool rejected = false;
            try { TransferReceiver.Start(huge, Path.Combine(f.Root, "stage"), 1000); } catch(InvalidDataException) { rejected = true; }
            T("sizes that would overflow the sum are rejected", rejected);

            var cache = new ModCache(Path.Combine(f.Root, "cacheroot2"));
            Directory.CreateDirectory(cache.NewStagingFolder());
            cache.CleanPartial(TimeSpan.FromHours(1));
            T("recent partial downloads survive a timed clean", Directory.EnumerateDirectories(Path.Combine(f.Root, "cacheroot2", "partial")).Any());
        }
    }

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

    private static void InventoryTests() {
        using(var f = new TestFolders()) {
            string a = f.Mod("ModA", "Mod A", "Alice", "2.1");
            f.File_(a, "Items/sword.json", "{}");
            string b = f.Mod("ModB");
            f.File_(b, "ModB.dll", "not really a dll");
            File.WriteAllText(Path.Combine(b, "id.modio"), "{\"id\":5966899,\"lastChecked\":\"2026-04-17\",\"dependencies\":[]}");
            string legacy = f.Mod("Legacy");
            File.WriteAllText(Path.Combine(legacy, "id.modio"), "123456");
            f.Mod("_Disabled");
            Directory.CreateDirectory(Path.Combine(f.Mods, "NoManifest"));
            string amp = f.Mod("MultiplayerMod", "AMP");
            f.File_(amp, "AMP.dll", "amp build 1");
            string nullId = f.Mod("NullId");
            File.WriteAllText(Path.Combine(nullId, "id.modio"), "{\"id\":null}");

            var mods = ModInventory.Scan(f.Mods, f.Cache, true);
            var byFolder = mods.ToDictionary(m => m.folder);

            T("folders without manifest are skipped", !byFolder.ContainsKey("NoManifest"));
            T("manifest fields are read", byFolder["ModA"].name == "Mod A" && byFolder["ModA"].author == "Alice" && byFolder["ModA"].version == "2.1");
            T("mod.io id from json id.modio", byFolder["ModB"].modioId == 5966899);
            T("mod.io id from legacy id.modio", byFolder["Legacy"].modioId == 123456);
            T("no id.modio means id 0", byFolder["ModA"].modioId == 0);
            T("null id in id.modio means id 0", byFolder["NullId"].modioId == 0);
            T("dll means hasCode", byFolder["ModB"].hasCode && !byFolder["ModA"].hasCode);
            T("underscore folder is disabled", !byFolder["_Disabled"].enabled && byFolder["ModA"].enabled);
            T("AMP folder is recognised", byFolder["MultiplayerMod"].isAmp && !byFolder["ModA"].isAmp);
            T("sizes are summed", byFolder["ModA"].sizeBytes > 0);
            T("hashes are computed", mods.All(m => m.hash.Length == 64));
            T("hash cache file is written", File.Exists(Path.Combine(f.Cache, "hashcache.json")));

            string hashB = byFolder["ModB"].hash;
            File.WriteAllText(Path.Combine(b, "id.modio"), "{\"id\":5966899,\"lastChecked\":\"2026-10-09\",\"dependencies\":[]}");
            T("id.modio changes don't change the hash", ModInventory.Scan(f.Mods, f.Cache, true).First(m => m.folder == "ModB").hash == hashB);

            string hashA = byFolder["ModA"].hash;
            f.File_(a, "Items/sword.json", "{ \"changed\": true }");
            T("changing a file changes the hash", ModInventory.Scan(f.Mods, f.Cache, true).First(m => m.folder == "ModA").hash != hashA);

            var noHash = ModInventory.Scan(f.Mods, f.Cache, false);
            T("without hashing only AMP is hashed", noHash.First(m => m.folder == "ModA").hash == "" && noHash.First(m => m.isAmp).hash.Length == 64);

            T("missing loadorder.json gives empty order", ModInventory.ReadLoadOrder(f.Mods).Length == 0);
            File.WriteAllText(Path.Combine(f.Mods, "loadorder.json"), "{\"modNames\":[\"ModB\",\"ModA\"]}");
            T("loadorder.json is read", ModInventory.ReadLoadOrder(f.Mods).SequenceEqual(new[] { "ModB", "ModA" }));

            string held = f.Mod("Held");
            f.File_(held, "Items/held.json", "held");
            using(var writer = new FileStream(Path.Combine(held, "Items", "held.json"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) {
                ModEntry heldMod = ModInventory.Scan(f.Mods, f.Cache, true).FirstOrDefault(m => m.folder == "Held");
                T("file open for writing by another handle still hashes", heldMod != null && heldMod.hash.Length == 64);
            }

            string cachePath = Path.Combine(f.Cache, "hashcache.json");
            ModInventory.Scan(f.Mods, f.Cache, false);
            T("partial scan keeps non-AMP entries in the hash cache", File.ReadAllText(cachePath).Contains("ModB.dll"));
            T("hash cache holds a folder before it is deleted", File.ReadAllText(cachePath).Contains("Legacy"));
            Directory.Delete(legacy, true);
            ModInventory.Scan(f.Mods, f.Cache, true);
            T("deleted mod folder is dropped from the hash cache", !File.ReadAllText(cachePath).Contains("Legacy"));

            string junkHash = ModInventory.Scan(f.Mods, f.Cache, true).First(m => m.folder == "ModA").hash;
            f.File_(a, "Thumbs.db", "thumbs");
            f.File_(a, "Items/desktop.ini", "[.ShellClassInfo]");
            f.File_(a, "Items/.DS_Store", "mac");
            f.File_(a, "output_log.log", "log line");
            f.File_(a, "Deep/er/THUMBS.DB", "upper case");
            T("Thumbs.db, desktop.ini, .DS_Store and .log files don't change the hash", ModInventory.Scan(f.Mods, f.Cache, true).First(m => m.folder == "ModA").hash == junkHash);
            f.File_(a, "Items/notes.txt", "real file");
            T("a normal new file still changes the hash", ModInventory.Scan(f.Mods, f.Cache, true).First(m => m.folder == "ModA").hash != junkHash);

            string huge = f.Mod("HugeId");
            File.WriteAllText(Path.Combine(huge, "id.modio"), "{\"id\":123456789012345678901234567890}");
            ModEntry hugeMod = null;
            Exception hugeError = null;
            try {
                hugeMod = ModInventory.Scan(f.Mods, f.Cache, true).FirstOrDefault(m => m.folder == "HugeId");
            } catch(Exception e) {
                hugeError = e;
            }
            T("id too big for a long gives id 0 and the scan doesn't throw", hugeError == null && hugeMod != null && hugeMod.modioId == 0);
        }
    }

    private static void JournalTests() {
        var plan = new SyncPlan();
        plan.steps.Add(new SyncStep { action = SyncAction.Add,     hostMod = M("New", hash: new string('a', 64)), source = "host" });
        plan.steps.Add(new SyncStep { action = SyncAction.Replace, hostMod = M("Old", hash: new string('b', 64)), localMod = M("Old"), source = "host" });
        plan.steps.Add(new SyncStep { action = SyncAction.Disable, localMod = M("Extra") });
        plan.steps.Add(new SyncStep { action = SyncAction.Unavailable, hostMod = M("Nope") });
        plan.loadOrderDiffers = true;

        SwapJournal j = JournalPlanner.Build(plan, new[] { "Old", "New" }, "1.2.3.4", 26950, "pw", SwapMode.Temporary);
        T("journal keeps session info", j.host == "1.2.3.4" && j.port == 26950 && j.password == "pw" && j.mode == SwapMode.Temporary && j.state == SwapState.Planned);
        T("disable op", j.ops.Any(o => o.kind == SwapOpKind.Disable && o.folder == "Extra"));
        T("replace = move aside then install", j.ops.FindIndex(o => o.kind == SwapOpKind.MoveAside && o.folder == "Old") < j.ops.FindIndex(o => o.kind == SwapOpKind.Install && o.folder == "Old"));
        T("install op carries the cache hash", j.ops.Any(o => o.kind == SwapOpKind.Install && o.folder == "New" && o.hash == new string('a', 64)));
        T("unavailable mods produce no op", !j.ops.Any(o => o.folder == "Nope"));
        T("load order op with host order", j.ops.Any(o => o.kind == SwapOpKind.LoadOrder) && j.hostLoadOrder.SequenceEqual(new[] { "Old", "New" }));
        T("disables come before installs", j.ops.FindLastIndex(o => o.kind == SwapOpKind.Disable) < j.ops.FindIndex(o => o.kind == SwapOpKind.Install));

        var ampPlan = new SyncPlan();
        ampPlan.steps.Add(new SyncStep { action = SyncAction.Replace, hostMod = M("AMPNew", hash: new string('d', 64), amp: true), localMod = M("AMPOld", amp: true), source = "host" });
        ampPlan.steps.Add(new SyncStep { action = SyncAction.Replace, hostMod = M("Old", hash: new string('b', 64)), localMod = M("Old"), source = "host" });
        SwapJournal aj = JournalPlanner.Build(ampPlan, null, "h", 1, "", SwapMode.Temporary);
        T("AMP: move-aside uses the joiner's folder, install the host's, both kept",
            aj.ops.Any(o => o.kind == SwapOpKind.MoveAside && o.folder == "AMPOld" && o.keep) && aj.ops.Any(o => o.kind == SwapOpKind.Install && o.folder == "AMPNew" && o.keep));
        int amove = aj.ops.FindIndex(o => o.kind == SwapOpKind.MoveAside && o.keep);
        T("AMP: install directly follows the move-aside, before other installs", aj.ops[amove + 1].kind == SwapOpKind.Install && aj.ops[amove + 1].keep
            && aj.ops.FindIndex(o => o.kind == SwapOpKind.Install && o.folder == "Old") > amove + 1);
        T("other mods' ops are not kept", aj.ops.Where(o => o.folder == "Old").All(o => !o.keep));

        string json = j.ToJson();
        SwapJournal back = SwapJournal.FromJson(json);
        T("journal round-trips through JSON", back.ops.Count == j.ops.Count && back.mode == SwapMode.Temporary && back.id == j.id);
        T("garbage JSON gives null", SwapJournal.FromJson("{not json") == null);
    }

    private static void ApplierTests() {
        int savedRetryMs = JournalApplier.RetryMs;
        JournalApplier.RetryMs = 200;
        using(var f = new TestFolders()) {
            string old = f.Mod("Old"); f.File_(old, "v.txt", "mine");
            f.Mod("Extra");
            File.WriteAllText(Path.Combine(f.Mods, "loadorder.json"), "{\"modNames\":[\"Extra\"]}");
            string cacheRoot = Path.Combine(f.Root, "cache");
            string hNew = new string('a', 64), hOld = new string('b', 64);
            Directory.CreateDirectory(Path.Combine(cacheRoot, "mods", hNew, "content"));
            File.WriteAllText(Path.Combine(cacheRoot, "mods", hNew, "content", "manifest.json"), "{\"Name\":\"New\"}");
            Directory.CreateDirectory(Path.Combine(cacheRoot, "mods", hOld, "content"));
            File.WriteAllText(Path.Combine(cacheRoot, "mods", hOld, "content", "v.txt"), "host");

            var j = new SwapJournal { mode = SwapMode.Temporary, hostLoadOrder = new[] { "Old", "New" } };
            j.ops.Add(new SwapOp { kind = SwapOpKind.Disable, folder = "Extra" });
            j.ops.Add(new SwapOp { kind = SwapOpKind.MoveAside, folder = "Old" });
            j.ops.Add(new SwapOp { kind = SwapOpKind.Install, folder = "Old", hash = hOld });
            j.ops.Add(new SwapOp { kind = SwapOpKind.Install, folder = "New", hash = hNew });
            j.ops.Add(new SwapOp { kind = SwapOpKind.LoadOrder });
            string journalDir = Path.Combine(f.Root, "journal");

            T("apply succeeds", JournalApplier.Apply(j, f.Mods, cacheRoot, journalDir) && j.state == SwapState.Applied);
            T("extra mod disabled", Directory.Exists(Path.Combine(f.Mods, "_AMPoff_Extra")) && !Directory.Exists(Path.Combine(f.Mods, "Extra")));
            T("old mod moved aside, host version installed", File.ReadAllText(Path.Combine(cacheRoot, "old", j.id, "Old", "v.txt")) == "mine" && File.ReadAllText(Path.Combine(f.Mods, "Old", "v.txt")) == "host");
            T("new mod installed from cache", File.Exists(Path.Combine(f.Mods, "New", "manifest.json")));
            T("host load order written", File.ReadAllText(Path.Combine(f.Mods, "loadorder.json")).Contains("New"));

            T("temporary restore succeeds", JournalApplier.Restore(j, f.Mods, cacheRoot, journalDir) && j.state == SwapState.Restored);
            T("everything is back", File.ReadAllText(Path.Combine(f.Mods, "Old", "v.txt")) == "mine" && Directory.Exists(Path.Combine(f.Mods, "Extra"))
                                     && !Directory.Exists(Path.Combine(f.Mods, "New")) && !Directory.Exists(Path.Combine(cacheRoot, "old", j.id, "Old")));
            T("cache entries are back", File.Exists(Path.Combine(cacheRoot, "mods", hNew, "content", "manifest.json")));
            T("original load order is back", File.ReadAllText(Path.Combine(f.Mods, "loadorder.json")).Contains("Extra") && !File.ReadAllText(Path.Combine(f.Mods, "loadorder.json")).Contains("New"));

            var keep = SwapJournal.FromJson(j.ToJson());
            keep.mode = SwapMode.Keep; keep.state = SwapState.Planned; keep.ops.ForEach(o => o.done = false);
            T("keep apply succeeds", JournalApplier.Apply(keep, f.Mods, cacheRoot, journalDir));
            T("keep restore succeeds", JournalApplier.Restore(keep, f.Mods, cacheRoot, journalDir));
            T("keep: extra re-enabled, host mods stay", Directory.Exists(Path.Combine(f.Mods, "Extra")) && Directory.Exists(Path.Combine(f.Mods, "New"))
                                                       && File.ReadAllText(Path.Combine(f.Mods, "Old", "v.txt")) == "host" && Directory.Exists(Path.Combine(cacheRoot, "old", keep.id, "Old")));
            T("keep: the helper is told where the old version is", JournalApplier.LastNote.Contains("previous version of Old") && JournalApplier.LastNote.Contains(keep.id));

            // Failure in the middle rolls back what was done
            using(var g = new TestFolders()) {
                g.Mod("A");
                var bad = new SwapJournal { mode = SwapMode.Temporary };
                bad.ops.Add(new SwapOp { kind = SwapOpKind.Disable, folder = "A" });
                bad.ops.Add(new SwapOp { kind = SwapOpKind.Install, folder = "B", hash = new string('c', 64) }); // not in cache
                T("apply with a missing cache entry fails", !JournalApplier.Apply(bad, g.Mods, Path.Combine(g.Root, "cache"), Path.Combine(g.Root, "j")));
                T("...and rolls back the disable", Directory.Exists(Path.Combine(g.Mods, "A")) && bad.state == SwapState.Restored && bad.lastError.Length > 0);
            }

            // Rollback that can't finish must not claim "Restored"
            using(var g = new TestFolders()) {
                var stuck = new SwapJournal { mode = SwapMode.Temporary };
                stuck.ops.Add(new SwapOp { kind = SwapOpKind.Disable, folder = "A", done = true }); // claims A is disabled, but neither A nor _AMPoff_A exists: undo is impossible
                stuck.ops.Add(new SwapOp { kind = SwapOpKind.Install, folder = "B", hash = new string('c', 64) }); // not in cache
                T("apply fails when a later op can't run", !JournalApplier.Apply(stuck, g.Mods, Path.Combine(g.Root, "cache"), Path.Combine(g.Root, "j")));
                T("...and a failed rollback leaves RestorePending", stuck.state == SwapState.RestorePending);
                JournalApplier.Restore(stuck, g.Mods, Path.Combine(g.Root, "cache"), Path.Combine(g.Root, "j"));
                T("a restore that can't finish is counted", stuck.restoreFailures == 1 && stuck.state == SwapState.RestorePending);
            }

            using(var g = new TestFolders()) {
                string oldAmp = g.Mod("AMPOld"); g.File_(oldAmp, "v.txt", "old-amp");
                g.Mod("Extra");
                string cr = Path.Combine(g.Root, "cache");
                string hAmp = new string('e', 64);
                Directory.CreateDirectory(Path.Combine(cr, "mods", hAmp, "content"));
                File.WriteAllText(Path.Combine(cr, "mods", hAmp, "content", "v.txt"), "new-amp");
                var aj = new SwapJournal { mode = SwapMode.Temporary };
                aj.ops.Add(new SwapOp { kind = SwapOpKind.Disable, folder = "Extra" });
                aj.ops.Add(new SwapOp { kind = SwapOpKind.MoveAside, folder = "AMPOld", keep = true });
                aj.ops.Add(new SwapOp { kind = SwapOpKind.Install, folder = "AMPNew", hash = hAmp, keep = true });
                string jd = Path.Combine(g.Root, "j");
                T("AMP swap applies", JournalApplier.Apply(aj, g.Mods, cr, jd));
                T("Temporary restore leaves the new AMP installed", JournalApplier.Restore(aj, g.Mods, cr, jd) && aj.state == SwapState.Restored
                    && File.ReadAllText(Path.Combine(g.Mods, "AMPNew", "v.txt")) == "new-amp");
                T("...the old AMP stays in old/<id>/ and the other mods are back",
                    File.ReadAllText(Path.Combine(cr, "old", aj.id, "AMPOld", "v.txt")) == "old-amp" && !Directory.Exists(Path.Combine(g.Mods, "AMPOld")) && Directory.Exists(Path.Combine(g.Mods, "Extra")));
                T("a second restore changes nothing", JournalApplier.Restore(aj, g.Mods, cr, jd) && Directory.Exists(Path.Combine(g.Mods, "AMPNew")));
            }

            using(var g = new TestFolders()) {
                g.Mod("AMPOld"); g.Mod("Extra");
                string cr = Path.Combine(g.Root, "cache");
                string hAmp = new string('e', 64);
                Directory.CreateDirectory(Path.Combine(cr, "mods", hAmp, "content"));
                var aj = new SwapJournal { mode = SwapMode.Temporary };
                aj.ops.Add(new SwapOp { kind = SwapOpKind.MoveAside, folder = "AMPOld", keep = true });
                aj.ops.Add(new SwapOp { kind = SwapOpKind.Install, folder = "AMPNew", hash = hAmp, keep = true });
                aj.ops.Add(new SwapOp { kind = SwapOpKind.Install, folder = "B", hash = new string('c', 64) }); // not in cache: Precheck fails nothing moved
                T("a failed Apply moves nothing", !JournalApplier.Apply(aj, g.Mods, cr, Path.Combine(g.Root, "j")) && Directory.Exists(Path.Combine(g.Mods, "AMPOld")));

                // Fails after the AMP ops ran (a locked file stops the Disable): the rollback must undo the kept ops
                var bj = new SwapJournal { mode = SwapMode.Temporary };
                bj.ops.Add(new SwapOp { kind = SwapOpKind.MoveAside, folder = "AMPOld", keep = true });
                bj.ops.Add(new SwapOp { kind = SwapOpKind.Install, folder = "AMPNew", hash = hAmp, keep = true });
                bj.ops.Add(new SwapOp { kind = SwapOpKind.Disable, folder = "Extra" });
                bool applied;
                using(new FileStream(Path.Combine(g.Mods, "Extra", "manifest.json"), FileMode.Open, FileAccess.Read, FileShare.None)) {
                    applied = JournalApplier.Apply(bj, g.Mods, cr, Path.Combine(g.Root, "j2"));
                }
                T("a failed Apply rolls the AMP ops back", !applied && bj.state == SwapState.Restored && Directory.Exists(Path.Combine(g.Mods, "AMPOld")) && !Directory.Exists(Path.Combine(g.Mods, "AMPNew")));
            }

            using(var g = new TestFolders()) {
                // Keep mode, AMP update interrupted between move-aside and install: restoring brings AMP back
                string cr = Path.Combine(g.Root, "cache");
                Directory.CreateDirectory(Path.Combine(cr, "old", "ab12", "AMPOld"));
                File.WriteAllText(Path.Combine(cr, "old", "ab12", "AMPOld", "v.txt"), "old-amp");
                var kj = new SwapJournal { id = "ab12", mode = SwapMode.Keep, state = SwapState.RestorePending };
                kj.ops.Add(new SwapOp { kind = SwapOpKind.MoveAside, folder = "AMPOld", keep = true, done = true });
                kj.ops.Add(new SwapOp { kind = SwapOpKind.Install, folder = "AMPNew", hash = new string('e', 64), keep = true });
                T("Keep-mode restore of an incomplete AMP update brings the old AMP back", JournalApplier.Restore(kj, g.Mods, cr, Path.Combine(g.Root, "j"))
                    && File.ReadAllText(Path.Combine(g.Mods, "AMPOld", "v.txt")) == "old-amp");
            }

            CrashSafetyTests();

            using(var g = new TestFolders()) {
                var evil = new SwapJournal();
                evil.ops.Add(new SwapOp { kind = SwapOpKind.Disable, folder = "..\\x" });
                T("unsafe folder names are refused", !JournalApplier.Apply(evil, g.Mods, Path.Combine(g.Root, "cache"), Path.Combine(g.Root, "j")));
            }
        }
        JournalApplier.RetryMs = savedRetryMs;
    }

    private static void CrashSafetyTests() {
        using(var f = new TestFolders()) {
            f.Mod("A"); f.Mod("B");
            var j = new SwapJournal { mode = SwapMode.Temporary };
            j.ops.Add(new SwapOp { kind = SwapOpKind.Disable, folder = "A" });
            j.ops.Add(new SwapOp { kind = SwapOpKind.Disable, folder = "B" });
            int saves = 0;
            JournalApplier.Apply(j, f.Mods, Path.Combine(f.Root, "cache"), Path.Combine(f.Root, "j"), x => { saves++; });
            T("the journal is saved before and after every op, and at the end", saves >= 2 * 2 + 1);
        }

        // A save that fails in the middle of an apply: everything done so far is rolled back
        using(var g = new TestFolders()) {
            g.Mod("A"); g.Mod("B");
            var k = new SwapJournal { mode = SwapMode.Temporary };
            k.ops.Add(new SwapOp { kind = SwapOpKind.Disable, folder = "A" });
            k.ops.Add(new SwapOp { kind = SwapOpKind.Disable, folder = "B" });
            int calls = 0;
            bool ok = JournalApplier.Apply(k, g.Mods, Path.Combine(g.Root, "c"), Path.Combine(g.Root, "j"), x => { if(++calls == 3) throw new IOException("disk full"); });
            T("a failing save rolls back and nothing is lost", !ok && Directory.Exists(Path.Combine(g.Mods, "A")) && Directory.Exists(Path.Combine(g.Mods, "B")) && k.state == SwapState.Restored);
        }

        // Crash right after the move but before "done" was saved: restore still finds it
        using(var g = new TestFolders()) {
            g.Mod("A"); g.Mod("B");
            Directory.Move(Path.Combine(g.Mods, "A"), Path.Combine(g.Mods, "_AMPoff_A")); // moved, journal says only "started"
            var crashed = new SwapJournal { mode = SwapMode.Temporary };
            crashed.ops.Add(new SwapOp { kind = SwapOpKind.Disable, folder = "A", started = true });
            crashed.ops.Add(new SwapOp { kind = SwapOpKind.Disable, folder = "B", started = true }); // started, never moved
            T("restore after a crash puts both back", JournalApplier.Restore(crashed, g.Mods, Path.Combine(g.Root, "c"), Path.Combine(g.Root, "j"))
                                                     && Directory.Exists(Path.Combine(g.Mods, "A")) && Directory.Exists(Path.Combine(g.Mods, "B")));
            T("restoring again is harmless (idempotent)", JournalApplier.Restore(crashed, g.Mods, Path.Combine(g.Root, "c"), Path.Combine(g.Root, "j")) && crashed.state == SwapState.Restored);
        }

        // Something is in the way of the restore: it is moved aside, not deleted
        using(var g = new TestFolders()) {
            string a = g.Mod("A"); g.File_(a, "mine.txt", "mine");
            var tj = new SwapJournal { mode = SwapMode.Temporary };
            tj.ops.Add(new SwapOp { kind = SwapOpKind.Disable, folder = "A" });
            string c = Path.Combine(g.Root, "c"), jd = Path.Combine(g.Root, "j");
            JournalApplier.Apply(tj, g.Mods, c, jd);
            string blocker = g.Mod("A"); g.File_(blocker, "theirs.txt", "theirs"); // a new A appeared meanwhile
            T("restore with a blocker succeeds", JournalApplier.Restore(tj, g.Mods, c, jd));
            T("...the original is back", File.ReadAllText(Path.Combine(g.Mods, "A", "mine.txt")) == "mine");
            string aside = Directory.GetDirectories(g.Mods, "_AMPconflict_A_*").FirstOrDefault();
            T("...the blocker is kept under a conflict name and recorded", aside != null && File.Exists(Path.Combine(aside, "theirs.txt")) && tj.conflicts.Contains(aside));
            T("...and the note says so", JournalApplier.LastNote.Contains("_AMPconflict_A_"));
        }

        // Keep: an installed mod has the name (different case) of a disabled one
        using(var g = new TestFolders()) {
            g.Mod("Same");
            var kj = new SwapJournal { mode = SwapMode.Keep };
            kj.ops.Add(new SwapOp { kind = SwapOpKind.Disable, folder = "Same" });
            string c = Path.Combine(g.Root, "c"), jd = Path.Combine(g.Root, "j");
            JournalApplier.Apply(kj, g.Mods, c, jd);
            g.File_(g.Mod("same"), "host.txt", "host");
            T("keep restore with a same-named installed mod keeps both", JournalApplier.Restore(kj, g.Mods, c, jd)
                && Directory.GetDirectories(g.Mods, "_AMPconflict_*").Length == 1 && Directory.Exists(Path.Combine(g.Mods, "Same")));
        }

        // Crash after the load order op was marked started, but before hadLoadOrder was saved: the player's file survives the undo
        using(var g = new TestFolders()) {
            string file = Path.Combine(g.Mods, "loadorder.json");
            File.WriteAllText(file, "{\"modNames\":[\"Mine\"]}");
            var lj = new SwapJournal { mode = SwapMode.Temporary }; // hadLoadOrder is false (never saved)
            lj.ops.Add(new SwapOp { kind = SwapOpKind.LoadOrder, started = true });
            T("undo of a started load order op keeps the player's file", JournalApplier.Restore(lj, g.Mods, Path.Combine(g.Root, "c"), Path.Combine(g.Root, "j"))
                                                                         && File.ReadAllText(file).Contains("Mine"));
        }

        // The backup is saved before the op starts, and used for the undo
        using(var g = new TestFolders()) {
            string file = Path.Combine(g.Mods, "loadorder.json");
            File.WriteAllText(file, "{\"modNames\":[\"Mine\"]}");
            var lj = new SwapJournal { mode = SwapMode.Temporary, hostLoadOrder = new[] { "Host" } };
            lj.ops.Add(new SwapOp { kind = SwapOpKind.LoadOrder });
            bool backupFirst = false;
            JournalApplier.Apply(lj, g.Mods, Path.Combine(g.Root, "c"), Path.Combine(g.Root, "j"), x => {
                if(x.ops[0].started && !x.ops[0].done) return;
                if(!x.ops[0].started && !x.ops[0].done && x.hadLoadOrder && File.Exists(Path.Combine(g.Root, "j", "loadorder.backup.json"))) backupFirst = true;
            });
            T("backup and hadLoadOrder are saved before the op is marked started", backupFirst);
            File.WriteAllText(file, "{\"modNames\":[\"Host\"]}");
            T("restore brings the player's load order back from the backup", JournalApplier.Restore(lj, g.Mods, Path.Combine(g.Root, "c"), Path.Combine(g.Root, "j")) && File.ReadAllText(file).Contains("Mine"));
        }

        // Early failure: a name is taken, nothing has moved
        using(var g = new TestFolders()) {
            g.Mod("A"); g.Mod("B"); g.Mod("_AMPoff_B");
            var pj = new SwapJournal { mode = SwapMode.Temporary };
            pj.ops.Add(new SwapOp { kind = SwapOpKind.Disable, folder = "A" });
            pj.ops.Add(new SwapOp { kind = SwapOpKind.Disable, folder = "B" });
            T("a taken name fails before anything moves", !JournalApplier.Apply(pj, g.Mods, Path.Combine(g.Root, "c"), Path.Combine(g.Root, "j"))
                && Directory.Exists(Path.Combine(g.Mods, "A")) && pj.lastError.Contains("_AMPoff_B") && pj.state == SwapState.Restored && !pj.ops.Any(o => o.done || o.started));
        }

        // Journal file: atomic save, leftover temp file, lock
        using(var g = new TestFolders()) {
            string path = Path.Combine(g.Root, "j", "session.json");
            var sj = new SwapJournal { mode = SwapMode.Keep, host = "h" };
            sj.SaveTo(path);
            sj.host = "h2";
            sj.SaveTo(path);
            T("journal saves and loads", SwapJournal.LoadFrom(path).host == "h2" && !File.Exists(path + ".tmp"));
            File.WriteAllText(path + ".tmp", sj.ToJson());
            File.WriteAllText(path, "{garbage");
            T("an unreadable journal falls back to the temp file", SwapJournal.LoadFrom(path) != null && SwapJournal.LoadFrom(path).host == "h2");

            File.WriteAllText(path, new SwapJournal().ToJson());
            string lockPath = Path.Combine(g.Root, "j", "session.lock");
            using(new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
                T("a second helper gets busy", JournalApplier.RunFromHelper(path, g.Mods, Path.Combine(g.Root, "c"), "apply") == "busy");
            }
            T("without the lock it runs", JournalApplier.RunFromHelper(path, g.Mods, Path.Combine(g.Root, "c"), "apply") == "");
            T("...and wrote the journal", SwapJournal.LoadFrom(path).state == SwapState.Applied);
        }

        // Quoting: a Mods folder with a trailing backslash must not swallow the closing quote
        string args = HelperScript.LauncherArguments("C:\\x y\\h.ps1", 1, "C:\\x y\\AMP.dll", "C:\\x y\\N.dll", "C:\\x y\\session.json", "C:\\x y\\Mods\\", "C:\\x y\\Cache/", "apply", false, "C:\\x y\\h.log");
        string decoded = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(args.Substring(args.LastIndexOf(' ') + 1)));
        T("trailing separators are trimmed in the helper command", decoded.Contains("-Mods \"C:\\x y\\Mods\" -Cache \"C:\\x y\\Cache\" -Action apply") && decoded.Contains("exit ([int]$r.ReturnValue)"));
    }

    private static void HelperTests() {
        string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if(!File.Exists(ps)) { T("powershell not available (skipped)", true); return; }

        using(var f = new TestFolders()) {
            f.Mod("Extra");
            string cache = Path.Combine(f.Root, "cache");
            string journalPath = Path.Combine(f.Root, "journal", "session.json");
            Directory.CreateDirectory(Path.GetDirectoryName(journalPath));
            var j = new SwapJournal { mode = SwapMode.Temporary };
            j.ops.Add(new SwapOp { kind = SwapOpKind.Disable, folder = "Extra" });
            File.WriteAllText(journalPath, j.ToJson());

            string script = Path.Combine(f.Root, "helper.ps1");
            File.WriteAllText(script, HelperScript.Script());
            string ampDll  = typeof(JournalApplier).Assembly.Location;           // the test exe contains the same source
            string jsonDll = typeof(Newtonsoft.Json.JsonConvert).Assembly.Location;
            string log = Path.Combine(f.Root, "helper.log");

            // Wait on a short-lived dummy process instead of the game
            var dummy = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping -n 2 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false });
            var launcher = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ps,
                HelperScript.LauncherArguments(script, dummy.Id, ampDll, jsonDll, journalPath, f.Mods + "\\", cache + "\\", "apply", false, log)) { CreateNoWindow = true, UseShellExecute = false });
            launcher.WaitForExit(30000);
            T("launcher exits 0 when Windows created the helper", launcher.HasExited && launcher.ExitCode == 0);

            DateTime until = DateTime.UtcNow.AddSeconds(60);
            while(DateTime.UtcNow < until) {
                string text = "";
                try { if(File.Exists(log)) text = File.ReadAllText(log); } catch(IOException) { } // PowerShell may be appending right now
                if(text.Contains("done") || text.Contains("fail")) break;
                System.Threading.Thread.Sleep(500);
            }

            T("helper ran through WMI and applied the journal", Directory.Exists(Path.Combine(f.Mods, "_AMPoff_Extra")));
            T("helper wrote the updated journal", SwapJournal.FromJson(File.ReadAllText(journalPath)).state == SwapState.Applied);
        }

        // The helper must also load the REAL built mod DLLs under Windows PowerShell 5.1
        string realAmp = @"C:\Projects\bns-mp\build\AMP.dll", realJson = @"C:\Projects\bns-mp\build\Newtonsoft.Json.dll";
        if(File.Exists(realAmp) && File.Exists(realJson)) {
            using(var f = new TestFolders()) {
                f.Mod("Extra");
                string journalPath = Path.Combine(f.Root, "journal", "session.json");
                var j = new SwapJournal { mode = SwapMode.Temporary };
                j.ops.Add(new SwapOp { kind = SwapOpKind.Disable, folder = "Extra" });
                j.SaveTo(journalPath);
                string script = Path.Combine(f.Root, "helper.ps1"), log = Path.Combine(f.Root, "helper.log");
                File.WriteAllText(script, HelperScript.Script());
                var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ps,
                    "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" -GamePid 0 -AmpDll \"" + realAmp + "\" -JsonDll \"" + realJson + "\" -Journal \"" + journalPath
                    + "\" -Mods \"" + f.Mods + "\" -Cache \"" + Path.Combine(f.Root, "cache") + "\" -Action apply -Relaunch 0 -Log \"" + log + "\"") { CreateNoWindow = true, UseShellExecute = false });
                p.WaitForExit(60000);
                string logText = File.Exists(log) ? File.ReadAllText(log) : "(no log)";
                T("helper loads the real built AMP.dll and applies (" + logText.Trim().Replace("\r\n", " | ") + ")", Directory.Exists(Path.Combine(f.Mods, "_AMPoff_Extra")) && logText.Contains("done"));
            }
        } else {
            T("real built DLLs not found (skipped)", true);
        }
    }

    private static ModEntry M(string folder, string hash = "h", long modio = 0, string name = null, bool enabled = true, bool amp = false) {
        return new ModEntry { folder = folder, name = name ?? folder, author = "a", version = "1", modioId = modio, hash = hash, enabled = enabled, isAmp = amp, sizeBytes = 1024 * 1024 };
    }

    private static HostModSet Host(string platform, params ModEntry[] mods) {
        return new HostModSet { platform = platform, mode = ModSyncMode.Warn, mods = mods.ToList(), personalMods = new[] { "Tool" }, loadOrder = new string[0] };
    }

    private static SyncPlan P(HostModSet host, string platform, params ModEntry[] local) {
        return SyncPlanner.Plan(host, local.ToList(), new string[0], platform);
    }

    private static int Count(SyncPlan plan, SyncAction action) { return plan.Of(action).Count(); }

    private static void PlannerTests() {
        var same = P(Host("windows", M("A"), M("B")), "windows", M("A"), M("B"));
        T("identical mods: nothing to do", same.steps.Count == 0 && !same.BlocksJoin);

        var missing = P(Host("windows", M("A"), M("B", modio: 42)), "windows", M("A"));
        T("missing mod with mod.io id comes from mod.io", missing.Of(SyncAction.Add).Single().source == "modio");
        var missingHost = P(Host("windows", M("A")), "windows");
        T("missing mod without mod.io id comes from the host", missingHost.Of(SyncAction.Add).Single().source == "host");
        T("missing mods block joining", missingHost.BlocksJoin);

        var changed = P(Host("windows", M("A", hash: "1")), "windows", M("A", hash: "2"));
        T("different hash on the same platform means replace", Count(changed, SyncAction.Replace) == 1);

        var extra = P(Host("windows", M("A")), "windows", M("A"), M("Mine"));
        T("extra local mod gets disabled", extra.Of(SyncAction.Disable).Single().localMod.folder == "Mine");

        var personal = P(Host("windows", M("A")), "windows", M("A"), M("tool"));
        T("personal mods (case-insensitive) are left alone", personal.steps.Count == 0);

        var ampDiff = P(Host("windows", M("A"), M("MultiplayerMod", hash: "x", amp: true)), "windows", M("A"), M("MultiplayerMod", hash: "y", amp: true));
        T("AMP: same platform gets exactly one Replace from the host", ampDiff.steps.Count == 1 && ampDiff.steps[0].action == SyncAction.Replace
            && ampDiff.steps[0].hostMod.isAmp && ampDiff.steps[0].localMod.isAmp && ampDiff.steps[0].source == "host");
        T("AMP update says it stays installed", ampDiff.Describe().Contains("stays installed") && ampDiff.Describe().Contains("AMP_ModCache/old"));
        T("AMP update is described as the host's build", ampDiff.Describe().Contains("AMP update (host's build)"));
        var ampSame = P(Host("windows", M("MultiplayerMod", hash: "x", amp: true)), "windows", M("MultiplayerMod", hash: "x", amp: true));
        T("AMP: identical build gives no step", ampSame.steps.Count == 0);
        var ampCross = P(Host("windows", M("MultiplayerMod", hash: "x", amp: true)), "android", M("MultiplayerMod", hash: "y", amp: true));
        T("AMP: no step across platforms, but flagged", ampCross.steps.Count == 0 && ampCross.ampDiffers);
        var ampMissing = P(Host("windows", M("MultiplayerMod", hash: "x", amp: true)), "windows");
        T("AMP: no local AMP gives an Add", ampMissing.steps.Count == 1 && ampMissing.steps[0].action == SyncAction.Add);
        T("AMP is never disabled", !P(Host("windows", M("A")), "windows", M("A"), M("MultiplayerMod", hash: "y", amp: true)).Of(SyncAction.Disable).Any());
        T("different AMP build is flagged", ampDiff.ampDiffers);

        var disabledLocal = P(Host("windows", M("A")), "windows", M("A"), M("_Off", enabled: false));
        T("disabled local mods are ignored", disabledLocal.steps.Count == 0);

        var quest = P(Host("windows", M("A", modio: 7), M("NexusOnly")), "android", M("A", hash: "", modio: 7));
        T("cross-platform: same mod.io id matches without hash", Count(quest, SyncAction.Replace) == 0 && Count(quest, SyncAction.Add) == 0);
        T("cross-platform: mod without mod.io id is unavailable", quest.Of(SyncAction.Unavailable).Single().hostMod.folder == "NexusOnly");
        T("unavailable mods alone don't block joining", !quest.BlocksJoin);

        var questAdd = P(Host("windows", M("B", modio: 9)), "android");
        T("cross-platform: missing mod.io mod is added from mod.io", questAdd.Of(SyncAction.Add).Single().source == "modio");

        var questRenamed = P(Host("windows", M("JujutsuSorcerers", name: "Jujutsu Sorcerers")), "android", M("JJS_Nomad", hash: "", name: "jujutsu sorcerers"));
        T("cross-platform: names match ignoring case and spaces", questRenamed.steps.Count == 0);

        var questExtra = P(Host("windows", M("A", modio: 1)), "android", M("A", hash: "", modio: 1), M("QuestOnly", hash: ""));
        T("cross-platform: extra mods are still disabled", Count(questExtra, SyncAction.Disable) == 1);

        var orderHost = Host("windows", M("A"), M("B"));
        orderHost.loadOrder = new[] { "B", "A" };
        T("load order difference is detected", SyncPlanner.Plan(orderHost, new[] { M("A"), M("B") }.ToList(), new[] { "A", "B" }, "windows").loadOrderDiffers);
        T("load order is ignored across platforms", !SyncPlanner.Plan(orderHost, new[] { M("A", hash: ""), M("B", hash: "") }.ToList(), new[] { "A", "B" }, "android").loadOrderDiffers);

        // Load order: only mods both sides have enabled count, in each side's own order
        Func<string[], string[], ModEntry[], ModEntry[], bool> orderDiffers = (hostOrder, localOrder, hostMods, localMods) => {
            var h = Host("windows", hostMods);
            h.loadOrder = hostOrder;
            return SyncPlanner.Plan(h, localMods.ToList(), localOrder, "windows").loadOrderDiffers;
        };
        T("load order: no loadorder.json on the joiner means no difference",
            !orderDiffers(new[] { "B", "A" }, new string[0], new[] { M("A"), M("B") }, new[] { M("A"), M("B") }));
        T("load order: no loadorder.json on the host means no difference",
            !orderDiffers(new string[0], new[] { "B", "A" }, new[] { M("A"), M("B") }, new[] { M("A"), M("B") }));
        T("load order: unrelated extra names don't matter",
            !orderDiffers(new[] { "A", "HostOnly", "B" }, new[] { "LocalOnly", "A", "B", "Other" }, new[] { M("A"), M("B"), M("HostOnly") }, new[] { M("A"), M("B"), M("LocalOnly") }));
        T("load order: swapped shared mods differ",
            orderDiffers(new[] { "A", "X", "B" }, new[] { "B", "A" }, new[] { M("A"), M("B"), M("X") }, new[] { M("A"), M("B") }));
        T("load order: AMP in different positions doesn't matter",
            !orderDiffers(new[] { "MultiplayerMod", "A", "B" }, new[] { "A", "B", "MultiplayerMod" },
                          new[] { M("A"), M("B"), M("MultiplayerMod", amp: true) }, new[] { M("A"), M("B"), M("MultiplayerMod", amp: true) }));
        T("load order: personal mods in different positions don't matter",
            !orderDiffers(new[] { "Tool", "A", "B" }, new[] { "A", "Tool", "B" }, new[] { M("A"), M("B"), M("Tool") }, new[] { M("A"), M("B"), M("Tool") }));
        T("load order: a mod missing from one side's file is not compared",
            !orderDiffers(new[] { "A", "B", "C" }, new[] { "B", "C" }, new[] { M("A"), M("B"), M("C") }, new[] { M("A"), M("B"), M("C") }));

        string text = missing.Describe();
        T("summary names the mod and size", text.Contains("B") && text.Contains("1 MB"));

        var disabledAmp = P(Host("windows", M("A"), M("MultiplayerMod", hash: "x", amp: true)), "windows",
                            M("_MultiplayerMod_old", hash: "y", amp: true, enabled: false), M("A"), M("MultiplayerMod", hash: "x", amp: true));
        T("disabled AMP copies don't count as the AMP build", !disabledAmp.ampDiffers);

        var replaced = P(Host("windows", M("A", hash: "1", modio: 5)), "windows", M("A", hash: "2", modio: 5));
        T("same-platform replace downloads from the host, not mod.io", replaced.Of(SyncAction.Replace).Single().source == "host");

        var nullHost = Host("windows", M("A"));
        nullHost.mods.Add(null);
        T("null entries in host and local lists are ignored", P(nullHost, "windows", M("A"), null).steps.Count == 0);
    }

    private static void ProfileTests() {
        using(var f = new TestFolders()) {
            string def = Path.Combine(f.Root, "Saves", "Default");
            string prof = Path.Combine(f.Root, "Saves", ProfileSeeder.ProfileFolderName);
            Directory.CreateDirectory(def);
            File.WriteAllText(Path.Combine(def, "Options.opt"), "opt1");
            File.WriteAllText(Path.Combine(def, "a.chr"), "A");
            File.WriteAllText(Path.Combine(def, "b.chr"), "B");
            File.WriteAllText(Path.Combine(def, "steam_autocloud.vdf"), "x");
            T("seeding succeeds", ProfileSeeder.EnsureSeeded(def, prof));
            T("options and both characters are copied", File.ReadAllText(Path.Combine(prof, "Options.opt")) == "opt1"
                && File.ReadAllText(Path.Combine(prof, "a.chr")) == "A" && File.ReadAllText(Path.Combine(prof, "b.chr")) == "B");
            T("other files are not copied", !File.Exists(Path.Combine(prof, "steam_autocloud.vdf")));
            T("the originals are untouched", File.ReadAllText(Path.Combine(def, "a.chr")) == "A");

            File.WriteAllText(Path.Combine(def, "Options.opt"), "opt2");
            File.WriteAllText(Path.Combine(prof, "a.chr"), "A-mp");
            T("a second call leaves an existing profile alone", ProfileSeeder.EnsureSeeded(def, prof)
                && File.ReadAllText(Path.Combine(prof, "Options.opt")) == "opt1" && File.ReadAllText(Path.Combine(prof, "a.chr")) == "A-mp");

            string empty = Path.Combine(f.Root, "Saves", "Empty");
            T("a missing Default folder still gives an empty profile", ProfileSeeder.EnsureSeeded(Path.Combine(f.Root, "nope"), empty)
                && Directory.Exists(empty) && Directory.GetFiles(empty).Length == 0);

            string bare = Path.Combine(f.Root, "Saves", "Bare");
            Directory.CreateDirectory(bare);
            File.WriteAllText(Path.Combine(bare, "other.txt"), "keep");
            T("an existing folder without settings or characters gets seeded", ProfileSeeder.EnsureSeeded(def, bare)
                && File.Exists(Path.Combine(bare, "a.chr")) && File.ReadAllText(Path.Combine(bare, "other.txt")) == "keep");
            Directory.CreateDirectory(Path.Combine(f.Root, "Saves", "Half"));
            File.WriteAllText(Path.Combine(f.Root, "Saves", "Half", "b.chr"), "B-own");
            T("a folder with a character is left alone", ProfileSeeder.EnsureSeeded(def, Path.Combine(f.Root, "Saves", "Half"))
                && !File.Exists(Path.Combine(f.Root, "Saves", "Half", "a.chr")));

            string stale = Path.Combine(f.Root, "Saves", "Stale");
            Directory.CreateDirectory(stale + ".seeding");
            File.WriteAllText(Path.Combine(stale + ".seeding", "junk.chr"), "junk");
            T("a leftover .seeding folder is replaced", ProfileSeeder.EnsureSeeded(def, stale)
                && !File.Exists(Path.Combine(stale, "junk.chr")) && File.Exists(Path.Combine(stale, "a.chr")) && !Directory.Exists(stale + ".seeding"));
        }
    }

    private static void ChunkTests() {
        string big = new string('x', MessageChunks.MaxChars * 2 + 10);
        var parts = MessageChunks.Split(big);
        T("long text splits into 3 parts", parts.Count == 3 && parts.All(p => p.Length <= MessageChunks.MaxChars));
        T("short text is one part", MessageChunks.Split("hi").Count == 1);
        T("empty text is one empty part", MessageChunks.Split("").Count == 1 && MessageChunks.Split("")[0] == "");

        string emoji = new string('y', MessageChunks.MaxChars - 1) + "\U0001F600" + "z";
        var emojiParts = MessageChunks.Split(emoji);
        T("surrogate pairs are never split", emojiParts.All(p => p.Length == 0 || !char.IsHighSurrogate(p[p.Length - 1])) && string.Concat(emojiParts) == emoji);

        var assembler = new MessageAssembler();
        T("incomplete message returns null", assembler.Add("k", 1, 3, parts[1]) == null && assembler.Add("k", 2, 3, parts[2]) == null);
        T("out-of-order parts reassemble", assembler.Add("k", 0, 3, parts[0]) == big);
        T("separate keys don't mix", assembler.Add("a", 0, 2, "1") == null && assembler.Add("b", 0, 1, "solo") == "solo" && assembler.Add("a", 1, 2, "2") == "12");
        T("nonsense part numbers are ignored", assembler.Add("bad", 5, 2, "x") == null && assembler.Add("bad", 0, 0, "x") == null);

        // Bounded memory: 16 pending messages (oldest evicted), 2 minute expiry, at most 133 parts
        var now = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        var crowded = new MessageAssembler(() => now);
        for(int i = 1; i <= 17; i++) {
            crowded.Add(i.ToString(), 0, 2, "a");
            now = now.AddSeconds(1);
        }
        T("17th pending message evicts the oldest", crowded.Add("17", 1, 2, "b") == "ab" && crowded.Add("1", 1, 2, "b") == null);

        var aging = new MessageAssembler(() => now);
        aging.Add("old", 0, 2, "a");
        now = now.AddMinutes(3);
        T("pending message older than 2 minutes is dropped", aging.Add("old", 1, 2, "b") == null && aging.Add("old", 0, 2, "a") == "ab");

        var forget = new MessageAssembler();
        forget.Add("1:5", 0, 2, "a");
        forget.Add("2:5", 0, 2, "a");
        forget.Forget("1:");
        T("Forget removes only the matching prefix", forget.Add("1:5", 1, 2, "b") == null && forget.Add("2:5", 1, 2, "b") == "ab");

        string overlong = null;
        var capped = new MessageAssembler();
        for(int i = 0; i < 200; i++) overlong = capped.Add("big", i, 200, "x");
        T("messages over 133 parts are rejected", overlong == null);
    }

    private static void SettingsTests() {
        var sent = new SentSettings();
        Func<string, int, OptionValue> pvp = (title, index) => new OptionValue { mod = "MultiplayerMod", option = "PvP", title = title, index = index };
        sent.Remember(pvp("Enabled", 1));
        T("applying the value joiners already have is not a change", !sent.IsChange(pvp("Enabled", 1)));
        T("a different value is a change", sent.IsChange(pvp("Disabled", 0)));
        T("...and is remembered", !sent.IsChange(pvp("Disabled", 0)));
        T("an option never seen counts as a change", sent.IsChange(new OptionValue { mod = "Other", option = "PvP", title = "Disabled", index = 0 }));
        sent.Clear();
        T("after Clear everything is a change again", sent.IsChange(pvp("Disabled", 0)));
        T("null is never a change", !sent.IsChange(null));

        var personal  = new[] { "MyTool" };
        var overrides = new System.Collections.Generic.Dictionary<string, string> { { "SpellMod/Spell Volume", "server" }, { "SpellMod/Damage", "personal" } };

        T("gameplay option of a normal mod is server-side", SettingsRules.IsServerSide("SpellMod", "Fire Damage", "Combat", false, personal, overrides));
        T("volume option is personal", !SettingsRules.IsServerSide("SpellMod", "Music Volume", "", false, personal, overrides));
        T("category words count too", !SettingsRules.IsServerSide("SpellMod", "Size", "HUD", false, personal, overrides));
        T("'ui' only matches as a whole word", SettingsRules.IsServerSide("SpellMod", "Build Speed", "Guide", false, personal, overrides));
        T("haptics (longer word) is personal", !SettingsRules.IsServerSide("SpellMod", "Haptics Strength", "", false, personal, overrides));
        T("options of personal mods are never synced", !SettingsRules.IsServerSide("mytool", "Fire Damage", "", false, personal, overrides));
        T("override 'server' wins over the word list", SettingsRules.IsServerSide("SpellMod", "Spell Volume", "", false, personal, overrides));
        T("override 'personal' wins", !SettingsRules.IsServerSide("SpellMod", "Damage", "", false, personal, overrides));
        T("AMP: only the Host settings category", SettingsRules.IsServerSide("MultiplayerMod", "PvP", SettingsRules.AmpHostCategory, true, personal, overrides)
                                                 && !SettingsRules.IsServerSide("MultiplayerMod", "Player Nametags", "Gameplay", true, personal, overrides));
        T("null lists are fine", SettingsRules.IsServerSide("SpellMod", "Fire Damage", null, false, null, null));

        var titles = new[] { "Off", "Low", "High" };
        T("index and title agree", SettingsRules.MatchIndex(titles, new OptionValue { index = 2, title = "High" }) == 2);
        T("title wins when the index moved", SettingsRules.MatchIndex(titles, new OptionValue { index = 0, title = "Low" }) == 1);
        T("unknown title is no match", SettingsRules.MatchIndex(titles, new OptionValue { index = 1, title = "Ultra" }) == -1);
        T("out of range index with unknown title is no match", SettingsRules.MatchIndex(titles, new OptionValue { index = 9, title = "?" }) == -1);
        T("no titles is no match", SettingsRules.MatchIndex(new string[0], new OptionValue { index = 0, title = "Off" }) == -1);
    }

    private const string ReleaseJson = @"{ ""tag_name"": ""v1.2.0"", ""draft"": false, ""prerelease"": false, ""body"": ""Fixes"",
      ""assets"": [
        { ""name"": ""SHA256SUMS.txt"", ""browser_download_url"": ""https://github.com/o/r/releases/download/v1.2.0/SHA256SUMS.txt"", ""size"": 100 },
        { ""name"": ""AMP-Community-1.2.0.zip"", ""browser_download_url"": ""https://github.com/o/r/releases/download/v1.2.0/AMP-Community-1.2.0.zip"", ""size"": 2000 } ] }";

    private static void ReleaseInfoTests() {
        ReleaseInfo r = ReleaseInfo.Parse(ReleaseJson);
        T("release parses", r != null && r.tag == "v1.2.0" && r.version == "1.2.0" && r.notes == "Fixes");
        T("release assets are read", r != null && r.assets.Count == 2 && r.assets[1].name == "AMP-Community-1.2.0.zip"
            && r.assets[1].url.EndsWith("/AMP-Community-1.2.0.zip") && r.assets[1].size == 2000);
        T("draft release is ignored", ReleaseInfo.Parse(ReleaseJson.Replace("\"draft\": false", "\"draft\": true")) == null);
        T("prerelease is ignored", ReleaseInfo.Parse(ReleaseJson.Replace("\"prerelease\": false", "\"prerelease\": true")) == null);
        T("garbage is not a release", ReleaseInfo.Parse("{not json") == null && ReleaseInfo.Parse("[]") == null && ReleaseInfo.Parse(null) == null);
        T("a release without a tag is rejected", ReleaseInfo.Parse("{ \"body\": \"x\" }") == null);

        T("1.2.0 < 1.10.0", ReleaseInfo.CompareVersions("1.2.0", "1.10.0") < 0);
        T("1.0 == 1.0.0", ReleaseInfo.CompareVersions("1.0", "1.0.0") == 0);
        T("1.0.1 > 1.0.0", ReleaseInfo.CompareVersions("1.0.1", "1.0.0") > 0);
        T("a non-numeric version is never newer", ReleaseInfo.CompareVersions("abc", "0.0.1") < 0 && ReleaseInfo.CompareVersions("1.0.0", "abc") > 0);

        T("zip asset is picked", ReleaseInfo.ZipAsset(r) != null && ReleaseInfo.ZipAsset(r).name == "AMP-Community-1.2.0.zip");
        T("no zip asset gives null", ReleaseInfo.ZipAsset(new ReleaseInfo { assets = { new ReleaseAsset { name = "SHA256SUMS.txt" } } }) == null);

        string a = "ABCDEF" + new string('0', 58), b = new string('b', 64);
        string sums = a + "  AMP-Community-1.2.0.zip\r\n" + b + " *Other.zip\n";
        T("checksum of the zip (two-space format) is lowercase", ReleaseInfo.ExpectedSha256(sums, "AMP-Community-1.2.0.zip") == a.ToLowerInvariant());
        T("checksum in binary format (* name) is read", ReleaseInfo.ExpectedSha256(sums, "Other.zip") == b);
        T("missing checksum gives null", ReleaseInfo.ExpectedSha256(sums, "Nope.zip") == null && ReleaseInfo.ExpectedSha256(null, "x") == null);

        T("github https urls are trusted", ReleaseInfo.IsTrustedUrl("https://github.com/x") && ReleaseInfo.IsTrustedUrl("https://api.github.com/repos/x")
            && ReleaseInfo.IsTrustedUrl("https://objects.githubusercontent.com/x") && ReleaseInfo.IsTrustedUrl("https://release-assets.githubusercontent.com/x"));
        T("http or lookalike urls are not trusted", !ReleaseInfo.IsTrustedUrl("http://github.com/x") && !ReleaseInfo.IsTrustedUrl("https://github.com.evil.com/x")
            && !ReleaseInfo.IsTrustedUrl("https://evil.com/github.com") && !ReleaseInfo.IsTrustedUrl("https://github.com@evil.com/x") && !ReleaseInfo.IsTrustedUrl(null));

        T("owner/name repos are valid", ReleaseInfo.IsValidRepo("rodthegod2/AMP-Community") && ReleaseInfo.IsValidRepo("a.b_c/d-e"));
        T("other repo strings are invalid", !ReleaseInfo.IsValidRepo(null) && !ReleaseInfo.IsValidRepo("") && !ReleaseInfo.IsValidRepo("owner")
            && !ReleaseInfo.IsValidRepo("a/b/c") && !ReleaseInfo.IsValidRepo("a/b?x=1") && !ReleaseInfo.IsValidRepo("../evil") && !ReleaseInfo.IsValidRepo("a/b\n"));

        long now = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc).Ticks;
        T("a check is due when there was none", ReleaseInfo.CheckDue(0, now));
        T("no new check within 6 hours", !ReleaseInfo.CheckDue(now - TimeSpan.FromHours(5).Ticks, now));
        T("a check is due after 6 hours", ReleaseInfo.CheckDue(now - TimeSpan.FromHours(6).Ticks, now));
        T("a check time in the future (clock change) is due", ReleaseInfo.CheckDue(now + TimeSpan.FromHours(1).Ticks, now));

        var local = new ModEntry { folder = "MultiplayerMod", isAmp = true, enabled = true };
        var fresh = new ModEntry { folder = "MultiplayerMod", isAmp = true, enabled = true, hash = new string('c', 64) };
        SwapJournal update = JournalPlanner.BuildAmpUpdate(fresh, local);
        T("AMP update: move aside then install, both kept", update.ops.Count == 2 && update.ops[0].kind == SwapOpKind.MoveAside && update.ops[0].folder == "MultiplayerMod" && update.ops[0].keep
            && update.ops[1].kind == SwapOpKind.Install && update.ops[1].hash == fresh.hash && update.ops[1].folder == "MultiplayerMod" && update.ops[1].keep);
        T("AMP update: nothing to rejoin, no load order change", !update.rejoinPending && update.host == "" && update.ops.All(o => o.kind != SwapOpKind.LoadOrder));
    }

    // Pairs of entry name and text
    private static string MakeZip(TestFolders f, string name, params string[] pairs) {
        string path = Path.Combine(f.Root, name);
        using(var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create)) {
            for(int i = 0; i < pairs.Length; i += 2) {
                using(var writer = new StreamWriter(zip.CreateEntry(pairs[i]).Open())) writer.Write(pairs[i + 1]);
            }
        }
        return path;
    }

    private static string Staging(TestFolders f) { return Path.Combine(f.Root, "staging-" + Guid.NewGuid().ToString("N")); }

    private static bool Rejects(Func<string> extract) {
        try {
            extract();
            return false;
        } catch(InvalidDataException) {
            return true;
        }
    }

    private static bool CannotWrite(string path) {
        try { using(File.OpenWrite(path)) { } return false; } catch(IOException) { return true; }
    }

    private static void ReleasePackageTests() {
        using(var f = new TestFolders()) {
            const long max = 1 << 20;
            string good = MakeZip(f, "good.zip", "MultiplayerMod/AMP.dll", "dll", "MultiplayerMod/manifest.json", "{}", "HOW-TO-PLAY.txt", "hi");
            string staging = Staging(f);
            string amp = ReleasePackage.ExtractAmpFolder(good, staging, max);
            T("valid package gives the AMP folder", amp != null && File.Exists(Path.Combine(amp, "AMP.dll")) && Path.GetFileName(amp) == "MultiplayerMod");
            T("the folder is inside the staging root", amp != null && Path.GetDirectoryName(Path.GetDirectoryName(amp)) == staging);

            using(var held = new FileStream(good, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                held.Seek(5, SeekOrigin.Begin); // as after hashing: the caller rewinds, the overload must work from an open stream
                held.Seek(0, SeekOrigin.Begin);
                string viaStream = ReleasePackage.ExtractAmpFolder(held, Staging(f), max);
                T("the stream overload extracts and leaves the stream open", File.Exists(Path.Combine(viaStream, "AMP.dll")) && held.CanRead);
                T("a held zip can't be written to meanwhile", CannotWrite(good));
            }

            T("release asset url of the exact repo", ReleaseInfo.IsReleaseAssetUrl("https://github.com/RodTheGod2/amp-community/releases/download/v1.0.1/a.zip", "rodthegod2/AMP-Community"));
            T("asset url of another repo or path is refused", !ReleaseInfo.IsReleaseAssetUrl("https://github.com/evil/AMP-Community/releases/download/v1/a.zip", "rodthegod2/AMP-Community")
                && !ReleaseInfo.IsReleaseAssetUrl("https://github.com/rodthegod2/AMP-Community-evil/releases/download/v1/a.zip", "rodthegod2/AMP-Community")
                && !ReleaseInfo.IsReleaseAssetUrl("https://github.com/rodthegod2/AMP-Community/raw/main/a.zip", "rodthegod2/AMP-Community")
                && !ReleaseInfo.IsReleaseAssetUrl("https://objects.githubusercontent.com/rodthegod2/AMP-Community/releases/download/a", "rodthegod2/AMP-Community")
                && !ReleaseInfo.IsReleaseAssetUrl("http://github.com/rodthegod2/AMP-Community/releases/download/a", "rodthegod2/AMP-Community"));

            var finished = new SwapJournal { state = SwapState.Applied, rejoinPending = false };
            finished.ops.Add(new SwapOp { keep = true, done = true });
            T("a finished AMP-only keep journal can be dropped", finished.IsFinishedKeepOnly());
            finished.ops.Add(new SwapOp { keep = false, done = true });
            T("a journal with a non-kept op is kept", !finished.IsFinishedKeepOnly());
            finished.ops.RemoveAt(1);
            finished.rejoinPending = true;
            T("a journal that still rejoins is kept", !finished.IsFinishedKeepOnly());
            finished.rejoinPending = false;
            finished.ops[0].done = false;
            T("an unfinished journal is kept", !finished.IsFinishedKeepOnly());

            string evil = MakeZip(f, "evil.zip", "MultiplayerMod/AMP.dll", "dll", "MultiplayerMod/manifest.json", "{}", "../evil.dll", "x");
            string evilStaging = Staging(f);
            T("a zip-slip entry rejects the whole package", Rejects(() => ReleasePackage.ExtractAmpFolder(evil, evilStaging, max)));
            T("...and nothing is extracted", !Directory.Exists(evilStaging) && !File.Exists(Path.Combine(f.Root, "evil.dll")));

            string big = MakeZip(f, "big.zip", "MultiplayerMod/AMP.dll", new string('x', 100), "MultiplayerMod/manifest.json", "{}");
            T("a package over the size limit is rejected", Rejects(() => ReleasePackage.ExtractAmpFolder(big, Staging(f), 50)));

            string noDll = MakeZip(f, "nodll.zip", "MultiplayerMod/manifest.json", "{}");
            string noDllStaging = Staging(f);
            T("a package without AMP.dll is rejected", Rejects(() => ReleasePackage.ExtractAmpFolder(noDll, noDllStaging, max)));
            T("...and the partial extraction is removed", !Directory.Exists(noDllStaging) || Directory.GetDirectories(noDllStaging).Length == 0);

            string two = MakeZip(f, "two.zip", "A/AMP.dll", "x", "A/manifest.json", "{}", "B/AMP.dll", "x", "B/manifest.json", "{}");
            T("two AMP folders are rejected", Rejects(() => ReleasePackage.ExtractAmpFolder(two, Staging(f), max)));

            string many = MakeZip(f, "many.zip", Enumerable.Range(0, 10001).SelectMany(i => new[] { "f" + i + ".txt", "" }).ToArray());
            T("more than 10,000 files is rejected", Rejects(() => ReleasePackage.ExtractAmpFolder(many, Staging(f), max)));
        }
    }

    private static AutopilotScript Parse(string json, out string error) { return AutopilotScript.Parse(json, out error); }
    private static bool Rejected(string json, string fragment) {
        string error;
        return Parse(json, out error) == null && error != null && error.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // mod.io delivery: unpacking a mod.io zip, the thumbnail the game writes, committing with and without the host's hash
    private static void ModioTests() {
        using(var f = new TestFolders()) {
            const long max = 1 << 20;
            string packaged = MakeZip(f, "packaged.zip", "InstantSloMo/manifest.json", "{\"Name\":\"x\"}", "InstantSloMo/Spell_SlowTime.json", "{}", "__MACOSX/InstantSloMo/._manifest.json", "junk");
            string dest = Staging(f);
            using(var zip = File.OpenRead(packaged)) ReleasePackage.ExtractAll(zip, dest, max);
            string folder = ReleasePackage.FindModFolder(dest);
            T("a packaged mod.io zip gives its one mod folder", Path.GetFileName(folder) == "InstantSloMo" && File.Exists(Path.Combine(folder, "Spell_SlowTime.json")));
            T("__MACOSX entries are not extracted", !Directory.Exists(Path.Combine(dest, "__MACOSX")));

            string flat = MakeZip(f, "flat.zip", "manifest.json", "{}", "Items/a.json", "{}");
            string flatDest = Staging(f);
            using(var zip = File.OpenRead(flat)) ReleasePackage.ExtractAll(zip, flatDest, max);
            T("a zip with manifest.json at the top is the mod itself", ReleasePackage.FindModFolder(flatDest) == flatDest);

            string two = MakeZip(f, "two.zip", "A/manifest.json", "{}", "B/manifest.json", "{}");
            string twoDest = Staging(f);
            using(var zip = File.OpenRead(two)) ReleasePackage.ExtractAll(zip, twoDest, max);
            T("two mod folders in one zip are refused", Rejects(() => ReleasePackage.FindModFolder(twoDest)));
            string none = MakeZip(f, "none.zip", "A/readme.txt", "x");
            string noneDest = Staging(f);
            using(var zip = File.OpenRead(none)) ReleasePackage.ExtractAll(zip, noneDest, max);
            T("a zip without a manifest is refused", Rejects(() => ReleasePackage.FindModFolder(noneDest)));
            string slip = MakeZip(f, "slip.zip", "A/manifest.json", "{}", "../../evil.json", "x");
            string slipDest = Staging(f);
            T("a zip-slip mod.io zip is refused before writing", Rejects(() => { using(var zip = File.OpenRead(slip)) ReleasePackage.ExtractAll(zip, slipDest, max); return ""; }) && !Directory.Exists(slipDest));
            string big = MakeZip(f, "bigmod.zip", "A/manifest.json", new string('x', 200));
            T("a mod.io zip over the size limit is refused", Rejects(() => { using(var zip = File.OpenRead(big)) ReleasePackage.ExtractAll(zip, Staging(f), 100); return ""; }));

            // The host's copy: installed through the game's mod browser, so it has id.modio and the thumbnail the game wrote
            string host = f.Mod("InstantSloMo");
            f.File_(host, "Spell_SlowTime.json", "{\"slow\":1}");
            f.File_(host, "id.modio", "{\"id\":2512544}");
            f.File_(host, "modio-thumbnail.png", "png bytes");
            ModEntry hostMod = ModInventory.Scan(f.Mods, f.Cache, true).Single(m => m.folder == "InstantSloMo");
            T("the thumbnail the game writes is not transferred", !TransferRules.ListTransferFiles(host).Any(x => x.path == "modio-thumbnail.png"));
            File.Delete(Path.Combine(host, "modio-thumbnail.png"));
            T("...and doesn't count for the hash", ModInventory.Scan(f.Mods, f.Cache, true).Single(m => m.folder == "InstantSloMo").hash == hostMod.hash);

            var cache = new ModCache(Path.Combine(f.Root, "cacheroot"));
            Func<string, string> download = content => {
                string staged = Path.Combine(cache.NewStagingFolder(), "x", "InstantSloMo");
                Directory.CreateDirectory(staged);
                File.Copy(Path.Combine(host, "manifest.json"), Path.Combine(staged, "manifest.json"));
                File.WriteAllText(Path.Combine(staged, "Spell_SlowTime.json"), content);
                return staged;
            };
            string actual;
            CacheEntry same = cache.Commit(download("{\"slow\":1}"), hostMod, 5249788, out actual);
            T("a mod.io download with the host's files is cached under the host's hash", same != null && same.hash == hostMod.hash && actual == hostMod.hash);
            T("...and records the mod.io file", same != null && same.modioId == 2512544 && same.modioFileId == 5249788);
            T("a transfer from the host records no mod.io file", cache.Find(hostMod.hash) != null && new ModCache(Path.Combine(f.Root, "c2")).Commit(download("{\"slow\":1}"), hostMod).modioFileId == 0);
            cache.Evict(0, new string[0]);

            string other = download("{\"slow\":2}");
            T("another version is refused and deleted, its hash reported", cache.Commit(other, hostMod, 5249789, out actual) == null && !Directory.Exists(other) && actual.Length == 64 && actual != hostMod.hash);

            CacheEntry quest = cache.CommitUnverified(download("{\"quest\":true}"), hostMod, 7615807);
            T("a cross-platform download is cached under its own hash", quest != null && quest.hash != hostMod.hash && cache.Find(quest.hash) != null);
            T("...with the host's folder and the mod.io ids", quest != null && quest.folder == "InstantSloMo" && quest.modioId == 2512544 && quest.modioFileId == 7615807);

            // AMP 1.0.0 hosts counted the thumbnail and send it: their hash still has to verify
            string legacyHost = f.Mod("Legacy");
            f.File_(legacyHost, "a.json", "{}");
            f.File_(legacyHost, "modio-thumbnail.png", "png");
            string[] legacyFiles = Directory.GetFiles(legacyHost, "*", SearchOption.AllDirectories);
            var legacyMod = new ModEntry { folder = "Legacy", name = "Legacy", hash = ModInventory.HashFolder(legacyHost, legacyFiles, HashCache.Load(Path.Combine(f.Root, "nocache.json")), true) };
            T("the legacy hash differs from the current one", legacyMod.hash != ModInventory.HashFolder(legacyHost, legacyFiles, HashCache.Load(Path.Combine(f.Root, "nocache.json"))));
            string received = cache.NewStagingFolder();
            foreach(string file in legacyFiles) File.Copy(file, Path.Combine(received, Path.GetFileName(file)));
            CacheEntry fromOld = cache.Commit(received, legacyMod);
            T("files from an AMP 1.0.0 host verify against its hash with the thumbnail", fromOld != null && fromOld.hash == legacyMod.hash);
            string tampered = cache.NewStagingFolder();
            File.WriteAllText(Path.Combine(tampered, "a.json"), "{\"x\":1}");
            File.WriteAllText(Path.Combine(tampered, "manifest.json"), File.ReadAllText(Path.Combine(legacyHost, "manifest.json")));
            File.WriteAllText(Path.Combine(tampered, "modio-thumbnail.png"), "png");
            T("...but other files still don't", cache.Commit(tampered, legacyMod) == null);

            var avail = ModioRules.Availability(new[] { new KeyValuePair<string, long>("windows", 7615808), new KeyValuePair<string, long>("Android", 7615807), new KeyValuePair<string, long>("oculus", 0) });
            T("platform availability from live files", avail.windows && avail.android && !avail.oculus && avail.Quest);
            T("no platforms means nowhere", !ModioRules.Availability(null).windows && !ModioRules.Availability(new KeyValuePair<string, long>[0]).Quest);
        }

        string error;
        var steps = AutopilotScript.Parse(@"[{""step"":""modioGetMod"",""id"":2512544,""expectName"":""Instant Slow-mo + Silent""},
            {""step"":""modioSearch"",""phrase"":""Instant Slow-mo"",""expectId"":2512544},{""step"":""modioDownload"",""id"":2512544,""expectFile"":""Spell_SlowTime.json""},{""step"":""modioDownload"",""id"":1}]", out error);
        T("mod.io steps parse (" + error + ")", steps != null && steps.Steps[0].Int("id") == 2512544 && steps.Steps[1].Str("phrase") == "Instant Slow-mo"
            && steps.Steps[2].Str("expectFile", null) == "Spell_SlowTime.json" && steps.Steps[3].Str("expectFile", null) == null);
        T("a mod.io id must be positive", AutopilotScript.Parse(@"[{""step"":""modioGetMod"",""id"":0,""expectName"":""x""}]", out error) == null);
    }

    private static void AutopilotTests() {
        string error;
        var full = Parse(@"[
            {""step"":""waitSpawn""},
            {""step"":""host"",""port"":27000},
            {""step"":""join"",""address"":""127.0.0.1"",""port"":27001,""password"":""pw"",""timeout"":300},
            {""step"":""expectModSync"",""blocks"":true},
            {""step"":""downloadAndRestart"",""mode"":""temporary""},
            {""step"":""expectRejoined""},
            {""step"":""expectModFolder"",""folder"":""AmpE2EDummy"",""present"":true},
            {""step"":""setOption"",""mod"":""MultiplayerMod"",""option"":""PvP"",""title"":""On""},
            {""step"":""expectOption"",""mod"":""MultiplayerMod"",""option"":""PvP"",""title"":""On""},
            {""step"":""expectProfileActive"",""active"":true},
            {""step"":""disconnect""},
            {""step"":""wait"",""seconds"":5},
            {""step"":""quit""}]", out error);
        T("a full script parses (" + error + ")", full != null && full.Steps.Count == 13);
        if(full == null) return;
        T("step names are kept in order", full.Steps[0].Name == "waitSpawn" && full.Steps[4].Name == "downloadAndRestart" && full.Steps[12].Name == "quit");
        T("default step timeout is 120 s", full.Steps[0].TimeoutSec == 120);
        T("an explicit timeout is used", full.Steps[2].TimeoutSec == 300);
        T("typed getters work", full.Steps[1].Int("port") == 27000 && full.Steps[2].Str("address") == "127.0.0.1" && full.Steps[3].Bool("blocks") && full.Steps[9].Bool("active"));
        T("an optional int falls back to its default", full.Steps[1].Int("maxPlayers", 4) == 4);
        T("an optional string falls back to its default", full.Steps[1].Str("password", "") == "");
        T("an optional value that is given wins", full.Steps[2].Str("password", "") == "pw");
        T("the swap mode is normalized", full.Steps[4].Str("mode") == "Temporary");
        T("a wait step gets time for the wait itself", full.Steps[11].TimeoutSec == 35);

        var gameplay = Parse(@"[{""step"":""expectRemotePlayers"",""count"":1},{""step"":""expectRemotePlayerMoved"",""minDistance"":1},
                                {""step"":""spawnItem"",""dataId"":""DaggerCommon""},{""step"":""expectItemSynced"",""dataId"":""SwordShortCommon""}]", out error);
        T("gameplay steps parse (" + error + ")", gameplay != null && gameplay.Steps[0].Int("count") == 1 && gameplay.Steps[1].Int("minDistance") == 1
                                                   && gameplay.Steps[2].Str("dataId") == "DaggerCommon" && gameplay.Steps[3].Str("dataId") == "SwordShortCommon");
        T("spawnItem needs an item id", Rejected(@"[{""step"":""spawnItem""}]", "dataId") && Rejected(@"[{""step"":""spawnItem"",""dataId"":"" ""}]", "dataId"));
        T("expectRemotePlayerMoved needs a positive distance", Rejected(@"[{""step"":""expectRemotePlayerMoved"",""minDistance"":0}]", "minDistance"));
        var update = Parse(@"[{""step"":""updateAmp"",""timeout"":300},{""step"":""expectAmpVersion"",""version"":""1.0.0""}]", out error);
        T("update steps parse (" + error + ")", update != null && update.Steps[0].TimeoutSec == 300 && update.Steps[1].Str("version") == "1.0.0");
        T("expectAmpVersion needs a version", Rejected(@"[{""step"":""expectAmpVersion""}]", "version"));
        var players = Parse(@"[{""step"":""waitPlayers"",""count"":2}]", out error);
        T("waitPlayers parses (" + error + ")", players != null && players.Steps[0].Int("count") == 2);
        T("waitPlayers needs a count of at least 1", Rejected(@"[{""step"":""waitPlayers"",""count"":0}]", "count") && Rejected(@"[{""step"":""waitPlayers""}]", "count"));

        T("invalid JSON is rejected", Rejected("[{", "json"));
        T("an object instead of an array is rejected", Rejected(@"{""step"":""quit""}", "array"));
        T("an empty script is rejected", Rejected("[]", "no steps"));
        T("a step that is not an object is rejected", Rejected(@"[""quit""]", "step 0"));
        T("a step without a name is rejected", Rejected(@"[{""port"":1}]", "step 0"));
        T("an unknown step is rejected", Rejected(@"[{""step"":""dance""}]", "dance"));
        T("an unknown key is rejected (typo guard)", Rejected(@"[{""step"":""host"",""port"":1,""maxPlayer"":2}]", "maxPlayer"));
        T("a missing required key is rejected", Rejected(@"[{""step"":""host""}]", "port"));
        T("a string where an int belongs is rejected", Rejected(@"[{""step"":""host"",""port"":""27000""}]", "port"));
        T("a float where an int belongs is rejected", Rejected(@"[{""step"":""host"",""port"":1.5}]", "port"));
        T("a port of 0 is rejected", Rejected(@"[{""step"":""host"",""port"":0}]", "port"));
        T("a port above the Netamite limit is rejected", Rejected(@"[{""step"":""host"",""port"":32768}]", "port"));
        T("maxPlayers 0 is rejected", Rejected(@"[{""step"":""host"",""port"":1,""maxPlayers"":0}]", "maxPlayers"));
        T("a number where a bool belongs is rejected", Rejected(@"[{""step"":""expectModSync"",""blocks"":1}]", "blocks"));
        T("a blank address is rejected", Rejected(@"[{""step"":""join"",""address"":"" "",""port"":1}]", "address"));
        T("an unknown swap mode is rejected", Rejected(@"[{""step"":""downloadAndRestart"",""mode"":""Forever""}]", "mode"));
        T("a mod folder with a path is rejected", Rejected(@"[{""step"":""expectModFolder"",""folder"":""..\\x"",""present"":true}]", "folder"));
        T("a '..' mod folder is rejected", Rejected(@"[{""step"":""expectModFolder"",""folder"":"".."",""present"":true}]", "folder"));
        T("a zero timeout is rejected", Rejected(@"[{""step"":""quit"",""timeout"":0}]", "timeout"));
        T("a negative wait is rejected", Rejected(@"[{""step"":""wait"",""seconds"":-1}]", "seconds"));
        T("steps after quit are rejected", Rejected(@"[{""step"":""quit""},{""step"":""disconnect""}]", "quit"));
        T("the error names the step number and name", Rejected(@"[{""step"":""waitSpawn""},{""step"":""host""}]", "step 1 (host)"));
        T("a null value is rejected", Rejected(@"[{""step"":""host"",""port"":null}]", "port"));

        // Command line
        string[] cmd = { "BladeAndSorcery.exe", "-vrmode", "mockhmd", "-amp-autopilot", @"C:\x y\script.json", "-logFile" };
        T("FindArg returns the value after the flag", AutopilotScript.FindArg(cmd, "-amp-autopilot") == @"C:\x y\script.json");
        T("FindArg returns null for a missing flag", AutopilotScript.FindArg(cmd, "-amp-test-docs") == null);
        T("FindArg returns null for a flag without a value", AutopilotScript.FindArg(cmd, "-logFile") == null);
        T("FindArg ignores argv[0]", AutopilotScript.FindArg(new[] { "-amp-autopilot", "x" }, "-amp-autopilot") == null);
        T("FindArg survives null input", AutopilotScript.FindArg(null, "-x") == null);

        // State
        using(var f = new TestFolders()) {
            string path = Path.Combine(f.Root, "state", "autopilot-state.json");
            string hash = AutopilotState.HashOf("script text");
            T("hash is stable and content-dependent", hash == AutopilotState.HashOf("script text") && hash != AutopilotState.HashOf("script text2") && hash.Length == 64);
            var fresh = AutopilotState.Load(path, hash);
            T("no state file starts at step 0", fresh.step == 0 && fresh.values.Count == 0);
            fresh.step = 4;
            fresh.values["restartedFrom"] = "3";
            fresh.Save(path);
            T("saving leaves no temp file", File.Exists(path) && !File.Exists(path + ".tmp"));
            var back = AutopilotState.Load(path, hash);
            T("state round-trips", back.step == 4 && back.values["restartedFrom"] == "3");
            fresh.step = 5;
            fresh.Save(path);
            T("state can be overwritten", AutopilotState.Load(path, hash).step == 5);
            T("a state of another script starts over", AutopilotState.Load(path, AutopilotState.HashOf("other")).step == 0);
            File.WriteAllText(path, "{garbage");
            T("a corrupt state starts over", AutopilotState.Load(path, hash).step == 0);
            File.WriteAllText(path, @"{""scriptHash"":""" + hash + @""",""step"":-3}");
            T("a negative step starts over", AutopilotState.Load(path, hash).step == 0);
        }
    }

    private static string DecodeSpec(string spec) { return System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(spec)); }

    private static void RelaunchTests() {
        string[] steamLaunch = { @"D:\Game\BladeAndSorcery.exe", "-vrmode", "openvr", "--doorstop-enabled", "true", "-amp-test-docs", @"C:\docs", "-vrmode", "mockhmd", "-logFile", @"C:\a b\run.log" };
        string[] args = HelperScript.SteamRelaunchArguments("629730", steamLaunch);
        T("relaunch goes through Steam", args[0] == "-applaunch" && args[1] == "629730");
        T("relaunch drops argv[0] and Steam's leading '-vrmode openvr'", args[2] == "--doorstop-enabled" && !args.Contains("openvr") && args.Count(a => a == "-vrmode") == 1);
        T("relaunch keeps every original argument in order", string.Join("|", args.Skip(2)) == @"--doorstop-enabled|true|-amp-test-docs|C:\docs|-vrmode|mockhmd|-logFile|C:\a b\run.log");
        string[] isolated = HelperScript.SteamRelaunchArguments("629730", new[] { "x.exe", "-vrmode", "openvr", "--doorstop-enabled", "true", "--doorstop-target-assembly", @"AmpTestBoot\AmpTestBoot.dll",
                                                                            "-amp-test-docs", @"C:\docs", "-vrmode", "mockhmd", "-amp-autopilot", @"C:\s.json" });
        T("an isolated test relaunch passes the isolation check", AutopilotScript.IsolationProblem(isolated) == null);
        Func<string[], string, string[]> without = (a, flag) => { var l = a.ToList(); int i = l.IndexOf(flag); l.RemoveRange(i, 2); return l.ToArray(); };
        T("a relaunch without the doorstop flag is refused", AutopilotScript.IsolationProblem(without(isolated, "--doorstop-enabled")) != null);
        T("a relaunch without the boot assembly is refused", AutopilotScript.IsolationProblem(without(isolated, "--doorstop-target-assembly")) != null);
        T("a relaunch without the test docs folder is refused", AutopilotScript.IsolationProblem(without(isolated, "-amp-test-docs")) != null);
        T("a relaunch without the autopilot is refused", AutopilotScript.IsolationProblem(without(isolated, "-amp-autopilot")) != null);
        T("a relaunch with a relative docs folder is refused", AutopilotScript.IsolationProblem(isolated.Select(a => a == @"C:\docs" ? "docs" : a).ToArray()) != null);
        T("a relaunch ending in -vrmode openvr is refused", AutopilotScript.IsolationProblem(isolated.Concat(new[] { "-vrmode", "openvr" }).ToArray()) != null);
        T("doorstop disabled is refused", AutopilotScript.IsolationProblem(isolated.Select(a => a == "true" ? "false" : a).ToArray()) != null);
        T("no arguments are refused", AutopilotScript.IsolationProblem(null) != null && AutopilotScript.IsolationProblem(new string[0]) != null);
        string[] plain = HelperScript.SteamRelaunchArguments("629730", new[] { "x.exe", "-skipintro" });
        T("without Steam's -vrmode nothing else is dropped", string.Join("|", plain) == "-applaunch|629730|-skipintro");
        string[] later = HelperScript.SteamRelaunchArguments("629730", new[] { "x.exe", "-skipintro", "-vrmode", "openvr" });
        T("a later '-vrmode openvr' is the user's choice and stays", later.Length == 5 && later[4] == "openvr");
        T("only the exe name gives only the Steam part", string.Join("|", HelperScript.SteamRelaunchArguments("629730", new[] { "x.exe" })) == "-applaunch|629730");
        T("null command line gives only the Steam part", string.Join("|", HelperScript.SteamRelaunchArguments("629730", null)) == "-applaunch|629730");

        T("plain arguments are not quoted", HelperScript.QuoteArguments(new[] { "-a", "b" }) == "-a b");
        T("arguments with spaces are quoted", HelperScript.QuoteArguments(new[] { @"C:\a b\c" }) == "\"C:\\a b\\c\"");
        T("an empty argument becomes \"\"", HelperScript.QuoteArguments(new[] { "a", "" }) == "a \"\"");
        T("embedded quotes are escaped", HelperScript.QuoteArguments(new[] { "say \"hi\"" }) == "\"say \\\"hi\\\"\"");
        T("a trailing backslash is doubled inside quotes", HelperScript.QuoteArguments(new[] { @"C:\a b\" }) == "\"C:\\a b\\\\\"");
        T("a trailing backslash without spaces is left alone", HelperScript.QuoteArguments(new[] { @"C:\ab\" }) == @"C:\ab\");

        string spec = HelperScript.RelaunchSpec(@"C:\Program Files (x86)\Steam\steam.exe", args);
        string[] decoded = DecodeSpec(spec).Split(new[] { '\n' }, 2);
        T("the spec holds the program and its quoted arguments", decoded[0] == @"C:\Program Files (x86)\Steam\steam.exe" && decoded[1].StartsWith("-applaunch 629730 --doorstop-enabled") && decoded[1].EndsWith("\"C:\\a b\\run.log\""));
        T("the spec is plain base64 (safe on a command line)", spec.All(c => char.IsLetterOrDigit(c) || c == '+' || c == '/' || c == '='));

        string normal = HelperScript.LauncherArguments("C:\\h.ps1", 1, "C:\\A.dll", "C:\\N.dll", "C:\\s.json", "C:\\M", "C:\\C", "apply", true, "C:\\h.log");
        string withSpec = HelperScript.LauncherArguments("C:\\h.ps1", 1, "C:\\A.dll", "C:\\N.dll", "C:\\s.json", "C:\\M", "C:\\C", "apply", true, "C:\\h.log", spec);
        string noRelaunch = HelperScript.LauncherArguments("C:\\h.ps1", 1, "C:\\A.dll", "C:\\N.dll", "C:\\s.json", "C:\\M", "C:\\C", "restore", false, "C:\\h.log", spec);
        Func<string, string> dec = a => DecodeSpec(a.Substring(a.LastIndexOf(' ') + 1));
        T("without a spec the helper command is unchanged", !dec(normal).Contains("-RelaunchSpec") && dec(normal).Contains("-Relaunch 1 -Log \"C:\\h.log\""));
        T("the spec is passed to the helper", dec(withSpec).Contains("-Relaunch 1 -Log \"C:\\h.log\" -RelaunchSpec " + spec));
        T("no relaunch means no spec", !dec(noRelaunch).Contains("-RelaunchSpec"));
        T("the helper script knows the parameter and keeps the default", HelperScript.Script().Contains("[string]$RelaunchSpec") && HelperScript.Script().Contains("steam://rungameid/629730"));

        // The helper really starts the given command, arguments with spaces intact
        string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if(!File.Exists(ps)) { T("powershell not available (skipped)", true); return; }
        using(var f = new TestFolders()) {
            string dir = Path.Combine(f.Root, "with space");
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, "target.ps1"), output = Path.Combine(dir, "out file.txt");
            File.WriteAllText(target, "[IO.File]::WriteAllText($args[0], ($args[1..($args.Count-1)] -join '|'))");
            string relaunchSpec = HelperScript.RelaunchSpec(ps, new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", target, output, "a b", "c" });

            string journalPath = Path.Combine(f.Root, "journal", "session.json");
            new SwapJournal { mode = SwapMode.Temporary }.SaveTo(journalPath);
            string script = Path.Combine(f.Root, "helper.ps1"), log = Path.Combine(f.Root, "helper.log");
            File.WriteAllText(script, HelperScript.Script());
            var helper = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ps,
                "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" -GamePid 0 -AmpDll \"" + typeof(JournalApplier).Assembly.Location + "\" -JsonDll \"" + typeof(Newtonsoft.Json.JsonConvert).Assembly.Location
                + "\" -Journal \"" + journalPath + "\" -Mods \"" + f.Mods + "\" -Cache \"" + f.Cache + "\" -Action apply -Relaunch 1 -Log \"" + log + "\" -RelaunchSpec " + relaunchSpec) { CreateNoWindow = true, UseShellExecute = false });
            helper.WaitForExit(60000);
            DateTime until = DateTime.UtcNow.AddSeconds(30);
            while(!File.Exists(output) && DateTime.UtcNow < until) System.Threading.Thread.Sleep(200);
            string text = File.Exists(output) ? File.ReadAllText(output) : "(no output)";
            T("the helper relaunches with the test command line (" + text + ")", text == "a b|c");
            string logText = File.Exists(log) ? File.ReadAllText(log) : "";
            T("...and says so in its log", logText.Contains("relaunched (test command)") && !logText.Contains("steam://"));
        }
    }
}
