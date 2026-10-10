using System;
using System.IO;
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

        string json = j.ToJson();
        SwapJournal back = SwapJournal.FromJson(json);
        T("journal round-trips through JSON", back.ops.Count == j.ops.Count && back.mode == SwapMode.Temporary && back.id == j.id);
        T("garbage JSON gives null", SwapJournal.FromJson("{not json") == null);
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
        T("AMP is never added, replaced or disabled", ampDiff.steps.Count == 0);
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
}
