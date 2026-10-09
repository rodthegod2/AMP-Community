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
        Console.WriteLine(fails == 0 ? "ALL PASSED" : fails + " FAILED");
        return fails;
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

            var mods = ModInventory.Scan(f.Mods, f.Cache, true);
            var byFolder = mods.ToDictionary(m => m.folder);

            T("folders without manifest are skipped", !byFolder.ContainsKey("NoManifest"));
            T("manifest fields are read", byFolder["ModA"].name == "Mod A" && byFolder["ModA"].author == "Alice" && byFolder["ModA"].version == "2.1");
            T("mod.io id from json id.modio", byFolder["ModB"].modioId == 5966899);
            T("mod.io id from legacy id.modio", byFolder["Legacy"].modioId == 123456);
            T("no id.modio means id 0", byFolder["ModA"].modioId == 0);
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

        string text = missing.Describe();
        T("summary names the mod and size", text.Contains("B") && text.Contains("1 MB"));
    }
}
