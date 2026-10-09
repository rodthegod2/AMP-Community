# Mod Sync Phase 1 (Handshake, Inventory, Planner, Join Gate) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When a player joins, the host sends its mod list; the joiner compares it with its own mods, and either joins (match) or sees exactly what differs, with "Join anyway" (warn mode) or "Disconnect".

**Architecture:** Pure, game-independent logic lives in `ModSync/` (inventory scan + hashing, sync planner, message chunking) and is unit-tested headlessly in `Tests/ModSyncTests`. Thin game glue (`ModSyncHost`, `ModSyncClient`, `ModSyncPaths`, one new packet, one UI page) wires it into AMP. The server holds back `GreetPlayer` until the joiner reports.

**Tech Stack:** C# 7.3 / .NET Framework 4.7.2 (Unity Mono), Netamite packets, Newtonsoft.Json (game's copy), headless tests as a net472 console app.

**Spec:** `docs/superpowers/specs/2026-10-09-mod-sync-design.md` (components 1, 2, 3, personal mods list; settings sync and downloads are later plans).

## Conventions for every task

- Repo: `C:\Projects\bns-mp\amp` (branch `fix/review-pass`, pushed to `fork/community`).
- C# 7.3 only: no switch expressions, no `is not`, no target-typed `new()`, no nullable reference annotations, no records.
- Files in the main csproj are listed explicitly: every new `.cs` file under the mod must be added as `<Compile Include="..." />` in `Adammantium Multiplayer Mod.csproj`.
- Build the mod: `bash /c/Projects/bns-mp/build.sh` — expected last lines contain no ` error ` and the exit code is 0. It never writes to the game folder.
- Headless tests: `cd /c/Projects/bns-mp/amp/Tests/ModSyncTests && dotnet build -nologo -v:q -nodeReuse:false -p:UseSharedCompilation=false && ./bin/Debug/net472/ModSyncTests.exe` — prints `PASS`/`FAIL` lines, exit code = number of failures.
- Packet round-trip test (after building the mod): `cd /c/Projects/bns-mp/packettest && dotnet build -nologo -v:q -nodeReuse:false -p:UseSharedCompilation=false && ./bin/Debug/net472/packettest.exe | tail -1` — expected `OK, 51 packets` once the new packet exists.
- Never run builds in parallel; never run `dotnet build-server shutdown` (other sessions share the machine).
- Commit with `git -c user.name=amp-review -c user.email=noreply@example.invalid commit` and end messages with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## File structure

| File | Responsibility |
|---|---|
| `ModSync/ModEntry.cs` (create) | Plain data for one mod folder. |
| `ModSync/ModInventory.cs` (create) | Scan a Mods folder into `ModEntry`s, folder hashing with an on-disk hash cache, read `loadorder.json`. Pure. |
| `ModSync/HostModSet.cs` (create) | Wire DTOs: `HostModSet`, `JoinerReport`, `ModSyncMode`, `JoinerStatus`. Pure. |
| `ModSync/SyncPlanner.cs` (create) | `SyncAction`, `SyncStep`, `SyncPlan` (+ text summary), `SyncPlanner.Plan`. Pure. |
| `ModSync/MessageChunks.cs` (create) | Split a long string into packet-sized parts; reassemble. Pure. |
| `ModSync/ModSyncPaths.cs` (create) | Resolve Mods folder, cache folder, platform once on the main thread. Game. |
| `ModSync/ModSyncHost.cs` (create) | Server side: build host mod set, send to joiners, gate greeting, timeouts. Game. |
| `ModSync/ModSyncClient.cs` (create) | Joiner side: receive set, scan + plan in background, report, show UI/notice. Game. |
| `Network/Packets/Implementation/ModSyncPacket.cs` (create) | Packet carrying JSON parts both ways. |
| `Network/Packets/PacketType.cs` (modify) | Add `MOD_SYNC` (111). |
| `Data/SafeFile.cs` (modify) | `hostingSettings.modSyncMode`, `hostingSettings.personalMods`. |
| `Network/Data/ClientData.cs` (modify) | `modSyncState` per connected client. |
| `Network/Server/Server.cs` (modify) | Route `OnConnect` through `ModSyncHost`. |
| `ModManager.cs` (modify) | Call `ModSyncPaths.Init()` at startup, `ModSyncHost.Prepare()` before hosting. |
| `UI/IngameModUI.cs` (modify) | `Page.ModSync` with the plan text and buttons. |
| `Adammantium Multiplayer Mod.csproj` (modify) | Compile includes. |
| `Tests/ModSyncTests/ModSyncTests.csproj`, `Program.cs`, `TestFolders.cs` (create) | Headless tests for the pure files. |

---

### Task 1: Test project + ModEntry + ModInventory

**Files:**
- Create: `ModSync/ModEntry.cs`, `ModSync/ModInventory.cs`
- Create: `Tests/ModSyncTests/ModSyncTests.csproj`, `Tests/ModSyncTests/Program.cs`, `Tests/ModSyncTests/TestFolders.cs`

- [ ] **Step 1: Create the test project**

`Tests/ModSyncTests/ModSyncTests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <!-- Headless tests for the game-independent mod sync code. Not part of the mod build. -->
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net472</TargetFramework>
    <PlatformTarget>x64</PlatformTarget>
    <LangVersion>7.3</LangVersion>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <ImportDirectoryBuildProps>false</ImportDirectoryBuildProps>
    <ImportDirectoryBuildTargets>false</ImportDirectoryBuildTargets>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Program.cs" />
    <Compile Include="TestFolders.cs" />
    <Compile Include="..\..\ModSync\ModEntry.cs" />
    <Compile Include="..\..\ModSync\ModInventory.cs" />
  </ItemGroup>
  <ItemGroup>
    <Reference Include="Newtonsoft.Json">
      <HintPath>C:\Program Files (x86)\Steam\steamapps\common\Blade &amp; Sorcery\BladeAndSorcery_Data\Managed\Newtonsoft.Json.dll</HintPath>
    </Reference>
  </ItemGroup>
</Project>
```

`Tests/ModSyncTests/TestFolders.cs`:

```csharp
using System;
using System.IO;

// Builds throwaway Mods folders under %TEMP% for the tests.
internal sealed class TestFolders : IDisposable {
    public readonly string Root;
    public readonly string Mods;
    public readonly string Cache;

    public TestFolders() {
        Root  = Path.Combine(Path.GetTempPath(), "amp-modsync-test-" + Guid.NewGuid().ToString("N"));
        Mods  = Path.Combine(Root, "Mods");
        Cache = Path.Combine(Root, "AMP_ModCache");
        Directory.CreateDirectory(Mods);
    }

    public string Mod(string folder, string name = null, string author = "Someone", string version = "1.0") {
        string dir = Path.Combine(Mods, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            "{ \"Name\": \"" + (name ?? folder) + "\", \"Author\": \"" + author + "\", \"ModVersion\": \"" + version + "\", \"GameVersion\": \"1.0.0.0\" }");
        return dir;
    }

    public void File_(string modDir, string relativePath, string content) {
        string path = Path.Combine(modDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, content);
    }

    public void Dispose() {
        try { Directory.Delete(Root, true); } catch(IOException) { } catch(UnauthorizedAccessException) { }
    }
}
```

`Tests/ModSyncTests/Program.cs`:

```csharp
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
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run the headless test command from Conventions.
Expected: build fails with `CS0246` / `CS0103` for `AMP.ModSync`, `ModInventory`.

- [ ] **Step 3: Implement ModEntry and ModInventory**

`ModSync/ModEntry.cs`:

```csharp
namespace AMP.ModSync {
    // One mod folder as the mod sync sees it. Plain data so it can travel as JSON and be tested without the game.
    public class ModEntry {
        public string folder;
        public string name;
        public string author;
        public string version;
        public long   modioId;    // from id.modio, 0 if the mod didn't come from mod.io
        public string hash;       // SHA-256 over relative paths + file hashes, "" if not computed
        public bool   hasCode;    // contains a .dll
        public long   sizeBytes;
        public bool   enabled;    // the game skips folders starting with "_"
        public bool   isAmp;      // the multiplayer mod itself
    }
}
```

`ModSync/ModInventory.cs`:

```csharp
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AMP.ModSync {
    // Reads a Mods folder the same way the game decides what a mod is: a folder with a manifest.json.
    public static class ModInventory {
        // Rewritten on every mod.io update check, so it must not change a mod's hash
        private static readonly string[] HashIgnoredFiles = { "id.modio" };
        private const string HashCacheFile = "hashcache.json";

        // hashAll = false skips hashing (cross-platform joiners can't compare hashes); AMP itself is always hashed
        public static List<ModEntry> Scan(string modsPath, string cacheDir, bool hashAll) {
            var result = new List<ModEntry>();
            if(!Directory.Exists(modsPath)) return result;

            HashCache cache = HashCache.Load(Path.Combine(cacheDir, HashCacheFile));
            foreach(string dir in Directory.GetDirectories(modsPath).OrderBy(d => d, StringComparer.OrdinalIgnoreCase)) {
                ModEntry entry = ReadEntry(dir);
                if(entry == null) continue;
                if(hashAll || entry.isAmp) entry.hash = HashFolder(dir, cache);
                result.Add(entry);
            }
            cache.Save();
            return result;
        }

        public static string[] ReadLoadOrder(string modsPath) {
            string path = Path.Combine(modsPath, "loadorder.json");
            if(!File.Exists(path)) return new string[0];
            try {
                JToken names = JObject.Parse(File.ReadAllText(path))["modNames"];
                return names == null ? new string[0] : names.ToObject<string[]>();
            } catch(JsonException) {
                return new string[0];
            }
        }

        internal static ModEntry ReadEntry(string dir) {
            string manifestPath = Path.Combine(dir, "manifest.json");
            if(!File.Exists(manifestPath)) return null; // the game doesn't load folders without a manifest either

            JObject manifest;
            try {
                manifest = JObject.Parse(File.ReadAllText(manifestPath));
            } catch(JsonException) {
                return null;
            }

            string folder = Path.GetFileName(dir);
            string[] files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
            return new ModEntry {
                folder    = folder,
                name      = Str(manifest, "Name", folder),
                author    = Str(manifest, "Author", ""),
                version   = Str(manifest, "ModVersion", ""),
                modioId   = ReadModioId(Path.Combine(dir, "id.modio")),
                hash      = "",
                hasCode   = files.Any(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)),
                sizeBytes = files.Sum(f => new FileInfo(f).Length),
                enabled   = !folder.StartsWith("_"),
                isAmp     = files.Any(f => Path.GetFileName(f).Equals("AMP.dll", StringComparison.OrdinalIgnoreCase)),
            };
        }

        // id.modio is JSON {"id":123,...} or, in older installs, just the number
        internal static long ReadModioId(string path) {
            if(!File.Exists(path)) return 0;
            string text = File.ReadAllText(path).Trim();
            long id;
            if(long.TryParse(text, out id)) return id;
            try {
                JToken token = JObject.Parse(text)["id"];
                return token == null ? 0 : token.Value<long>();
            } catch(JsonException) {
                return 0;
            } catch(FormatException) {
                return 0;
            }
        }

        internal static string HashFolder(string dir, HashCache cache) {
            var lines = new StringBuilder();
            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                                 .Where(f => !HashIgnoredFiles.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
                                 .Select(f => new KeyValuePair<string, string>(RelativePath(dir, f), f))
                                 .OrderBy(f => f.Key, StringComparer.Ordinal);
            foreach(var file in files) {
                lines.Append(file.Key).Append('\0').Append(cache.FileHash(file.Value)).Append('\n');
            }
            return HashCache.Hex(SHA256Bytes(Encoding.UTF8.GetBytes(lines.ToString())));
        }

        internal static string RelativePath(string root, string file) {
            return file.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
        }

        private static byte[] SHA256Bytes(byte[] data) {
            using(SHA256 sha = SHA256.Create()) return sha.ComputeHash(data);
        }

        private static string Str(JObject obj, string key, string fallback) {
            JToken token = obj[key];
            return token == null || token.Type == JTokenType.Null ? fallback : token.ToString();
        }
    }

    // Remembers file hashes by path, size and modification time, so rescanning gigabytes of mods is fast
    internal class HashCache {
        internal class Item {
            public long   size;
            public long   ticks;
            public string sha;
        }

        private readonly string path;
        private readonly Dictionary<string, Item> items;
        private bool dirty;

        private HashCache(string path, Dictionary<string, Item> items) {
            this.path  = path;
            this.items = items;
        }

        internal static HashCache Load(string path) {
            Dictionary<string, Item> items = null;
            try {
                if(File.Exists(path)) items = JsonConvert.DeserializeObject<Dictionary<string, Item>>(File.ReadAllText(path));
            } catch(JsonException) { } catch(IOException) { }
            return new HashCache(path, items ?? new Dictionary<string, Item>());
        }

        internal string FileHash(string file) {
            var info = new FileInfo(file);
            Item item;
            if(items.TryGetValue(file, out item) && item.size == info.Length && item.ticks == info.LastWriteTimeUtc.Ticks) return item.sha;

            string sha;
            using(FileStream stream = File.OpenRead(file))
            using(SHA256 hasher = SHA256.Create()) {
                sha = Hex(hasher.ComputeHash(stream));
            }
            items[file] = new Item { size = info.Length, ticks = info.LastWriteTimeUtc.Ticks, sha = sha };
            dirty = true;
            return sha;
        }

        internal void Save() {
            if(!dirty) return;
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(items));
                dirty = false;
            } catch(IOException) { } catch(UnauthorizedAccessException) { } // only a speed-up, losing it is fine
        }

        internal static string Hex(byte[] bytes) {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach(byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run the headless test command. Expected: 16 `PASS` lines, `ALL PASSED`, exit code 0.

- [ ] **Step 5: Add the files to the mod and build**

In `Adammantium Multiplayer Mod.csproj`, after `<Compile Include="Network\Packets\Implementation\MagicThrowPacket.cs" />` add:

```xml
    <Compile Include="ModSync\ModEntry.cs" />
    <Compile Include="ModSync\ModInventory.cs" />
```

Run `bash /c/Projects/bns-mp/build.sh`. Expected: exit 0, no ` error `.

- [ ] **Step 6: Commit**

```bash
cd /c/Projects/bns-mp/amp
git add ModSync/ModEntry.cs ModSync/ModInventory.cs Tests/ModSyncTests "Adammantium Multiplayer Mod.csproj"
git -c user.name=amp-review -c user.email=noreply@example.invalid commit -m "Mod sync: mod inventory with cached folder hashes

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

Also add `Tests/ModSyncTests/bin/` and `Tests/ModSyncTests/obj/` to `.gitignore` before committing if `git status` shows them.

---

### Task 2: Wire DTOs + SyncPlanner

**Files:**
- Create: `ModSync/HostModSet.cs`, `ModSync/SyncPlanner.cs`
- Modify: `Tests/ModSyncTests/ModSyncTests.csproj`, `Tests/ModSyncTests/Program.cs`

- [ ] **Step 1: Write the failing tests**

Add to `ModSyncTests.csproj` inside the Compile ItemGroup:

```xml
    <Compile Include="..\..\ModSync\HostModSet.cs" />
    <Compile Include="..\..\ModSync\SyncPlanner.cs" />
```

In `Program.cs`, call `PlannerTests();` after `InventoryTests();` in `Main`, and add:

```csharp
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run the headless test command. Expected: build errors for `HostModSet`, `SyncPlanner`, `SyncPlan`, `SyncAction`, `ModSyncMode`.

- [ ] **Step 3: Implement**

`ModSync/HostModSet.cs`:

```csharp
using System.Collections.Generic;

namespace AMP.ModSync {
    // "off": old behaviour (no mod list); "warn": show differences, joiners may still join; "enforce": joiners must match
    public static class ModSyncMode {
        public const string Off     = "off";
        public const string Warn    = "warn";
        public const string Enforce = "enforce";
    }

    public static class JoinerStatus {
        public const string Ready      = "ready";
        public const string NeedsSetup = "needs_setup";
        public const string Declined   = "declined";
    }

    // Sent host -> joiner as JSON. Fields are only ever appended, so newer builds can still talk to older ones.
    public class HostModSet {
        public const int CurrentProtocol = 1;

        public int            protocol     = CurrentProtocol;
        public string         platform     = "";
        public string         ampBuild     = "";
        public string         mode         = ModSyncMode.Warn;
        public List<ModEntry> mods         = new List<ModEntry>();
        public string[]       personalMods = new string[0];
        public string[]       loadOrder    = new string[0];
    }

    // Sent joiner -> host as JSON.
    public class JoinerReport {
        public int    protocol = HostModSet.CurrentProtocol;
        public string platform = "";
        public string status   = JoinerStatus.Ready;
        public string detail   = "";
    }
}
```

`ModSync/SyncPlanner.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AMP.ModSync {
    public enum SyncAction { Add, Replace, Disable, Unavailable }

    public class SyncStep {
        public SyncAction action;
        public ModEntry   hostMod;   // null for Disable
        public ModEntry   localMod;  // null for Add and Unavailable
        public string     source = ""; // "modio" or "host" for Add/Replace

        public ModEntry Mod { get { return hostMod ?? localMod; } }
    }

    public class SyncPlan {
        public List<SyncStep> steps = new List<SyncStep>();
        public bool loadOrderDiffers;
        public bool ampDiffers;

        // Mods a crossplay player can't get only produce a warning; everything else needs setting up first
        public bool BlocksJoin { get { return loadOrderDiffers || steps.Any(s => s.action != SyncAction.Unavailable); } }

        public IEnumerable<SyncStep> Of(SyncAction action) { return steps.Where(s => s.action == action); }

        public string Describe() {
            var sb = new StringBuilder();
            Group(sb, "Download",                     SyncAction.Add);
            Group(sb, "Replace with the host's version", SyncAction.Replace);
            Group(sb, "Turn off",                     SyncAction.Disable);
            Group(sb, "Not available on your platform", SyncAction.Unavailable);
            if(loadOrderDiffers) sb.AppendLine("Mod load order differs from the host's.");
            if(ampDiffers)       sb.AppendLine("Your AMP build differs from the host's.");
            return sb.ToString().TrimEnd();
        }

        private void Group(StringBuilder sb, string title, SyncAction action) {
            List<SyncStep> group = Of(action).ToList();
            if(group.Count == 0) return;
            sb.AppendLine(title + ":");
            foreach(SyncStep step in group) {
                string line = "  " + step.Mod.name;
                if(action == SyncAction.Add || action == SyncAction.Replace) line += " (" + Megabytes(step.Mod.sizeBytes) + ")";
                if(step.Mod.hasCode && action != SyncAction.Disable) line += " [contains code]";
                sb.AppendLine(line);
            }
        }

        internal static string Megabytes(long bytes) {
            return Math.Max(1, (long) Math.Round(bytes / (1024.0 * 1024.0))) + " MB";
        }
    }

    public static class SyncPlanner {
        public static SyncPlan Plan(HostModSet host, List<ModEntry> local, string[] localLoadOrder, string localPlatform) {
            var plan = new SyncPlan();
            bool samePlatform = string.Equals(host.platform, localPlatform, StringComparison.OrdinalIgnoreCase);
            var personal = new HashSet<string>(host.personalMods ?? new string[0], StringComparer.OrdinalIgnoreCase);
            Func<ModEntry, bool> leftAlone = m => m.isAmp || personal.Contains(m.folder);

            List<ModEntry> mine = local.Where(m => m.enabled && !leftAlone(m)).ToList();
            var matched = new HashSet<ModEntry>();

            foreach(ModEntry hostMod in (host.mods ?? new List<ModEntry>()).Where(m => m.enabled && !leftAlone(m))) {
                ModEntry localMod = mine.FirstOrDefault(m => !matched.Contains(m) && SameMod(hostMod, m, samePlatform));
                string source = hostMod.modioId > 0 ? "modio" : (samePlatform ? "host" : "");

                if(localMod == null) {
                    plan.steps.Add(new SyncStep { action = source == "" ? SyncAction.Unavailable : SyncAction.Add, hostMod = hostMod, source = source });
                    continue;
                }
                matched.Add(localMod);

                // Builds differ between platforms, so versions are only compared on the same platform
                if(samePlatform && !string.IsNullOrEmpty(hostMod.hash) && !string.IsNullOrEmpty(localMod.hash) && hostMod.hash != localMod.hash) {
                    plan.steps.Add(new SyncStep { action = SyncAction.Replace, hostMod = hostMod, localMod = localMod, source = source });
                }
            }

            foreach(ModEntry localMod in mine.Where(m => !matched.Contains(m))) {
                plan.steps.Add(new SyncStep { action = SyncAction.Disable, localMod = localMod });
            }

            plan.loadOrderDiffers = samePlatform && !SameOrder(host.loadOrder, localLoadOrder, personal);

            ModEntry hostAmp  = (host.mods ?? new List<ModEntry>()).FirstOrDefault(m => m.isAmp);
            ModEntry localAmp = local.FirstOrDefault(m => m.isAmp);
            string hostBuild  = !string.IsNullOrEmpty(host.ampBuild) ? host.ampBuild : (hostAmp != null ? hostAmp.hash : "");
            plan.ampDiffers   = !string.IsNullOrEmpty(hostBuild) && (localAmp == null || localAmp.hash != hostBuild);
            return plan;
        }

        internal static bool SameMod(ModEntry a, ModEntry b, bool samePlatform) {
            if(string.Equals(a.folder, b.folder, StringComparison.OrdinalIgnoreCase)) return true;
            if(samePlatform) return false;
            if(a.modioId > 0 && b.modioId > 0) return a.modioId == b.modioId;
            return Normalize(a.name) != "" && Normalize(a.name) == Normalize(b.name);
        }

        internal static string Normalize(string text) {
            return new string((text ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        }

        private static bool SameOrder(string[] host, string[] local, HashSet<string> personal) {
            string[] a = (host ?? new string[0]).Where(n => !personal.Contains(n)).ToArray();
            string[] b = (local ?? new string[0]).Where(n => !personal.Contains(n)).ToArray();
            return a.SequenceEqual(b, StringComparer.OrdinalIgnoreCase);
        }
    }
}
```

Note on the AMP check: `host.ampBuild` is filled by the host from its AMP folder hash; joiners always hash their own AMP folder (`ModInventory.Scan` does this even with `hashAll = false`).

- [ ] **Step 4: Run tests to verify they pass**

Run the headless test command. Expected: all `PASS`, `ALL PASSED`.

- [ ] **Step 5: Add to the mod, build, commit**

Add to the csproj after the Task 1 entries:

```xml
    <Compile Include="ModSync\HostModSet.cs" />
    <Compile Include="ModSync\SyncPlanner.cs" />
```

Run `bash /c/Projects/bns-mp/build.sh` (exit 0), then:

```bash
cd /c/Projects/bns-mp/amp
git add ModSync/HostModSet.cs ModSync/SyncPlanner.cs Tests/ModSyncTests "Adammantium Multiplayer Mod.csproj"
git -c user.name=amp-review -c user.email=noreply@example.invalid commit -m "Mod sync: plan what a joiner has to add, replace or turn off

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Message chunking

**Files:**
- Create: `ModSync/MessageChunks.cs`
- Modify: `Tests/ModSyncTests/ModSyncTests.csproj`, `Tests/ModSyncTests/Program.cs`

- [ ] **Step 1: Write the failing tests**

csproj: add `<Compile Include="..\..\ModSync\MessageChunks.cs" />`.

`Program.cs`: call `ChunkTests();` in `Main` after `PlannerTests();`, and add:

```csharp
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
    }
```

- [ ] **Step 2: Run to verify failure** — expected build errors for `MessageChunks`, `MessageAssembler`.

- [ ] **Step 3: Implement** `ModSync/MessageChunks.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;

namespace AMP.ModSync {
    // Netamite strings and packets are limited to 64 KB, so long JSON messages travel in parts.
    public static class MessageChunks {
        public const int MaxChars = 15000; // UTF-8 can need 4 bytes per char: 15000 * 4 stays under 64 KB

        public static List<string> Split(string text) {
            var parts = new List<string>();
            text = text ?? "";
            int index = 0;
            do {
                int length = System.Math.Min(MaxChars, text.Length - index);
                // Don't cut an emoji (surrogate pair) in half, each part is encoded on its own
                if(length > 0 && index + length < text.Length && char.IsHighSurrogate(text[index + length - 1])) length--;
                parts.Add(text.Substring(index, length));
                index += length;
            } while(index < text.Length);
            return parts;
        }
    }

    public class MessageAssembler {
        private const int MaxParts = 1000;
        private readonly Dictionary<string, string[]> pending = new Dictionary<string, string[]>();

        // Returns the full message once every part arrived, otherwise null
        public string Add(string key, int part, int parts, string data) {
            if(parts <= 0 || parts > MaxParts || part < 0 || part >= parts) return null;
            lock(pending) {
                string[] slots;
                if(!pending.TryGetValue(key, out slots) || slots.Length != parts) {
                    slots = new string[parts];
                    pending[key] = slots;
                }
                slots[part] = data ?? "";
                if(slots.Any(s => s == null)) return null;
                pending.Remove(key);
                return string.Concat(slots);
            }
        }
    }
}
```

- [ ] **Step 4: Run tests** — expected `ALL PASSED`.

- [ ] **Step 5: Add `<Compile Include="ModSync\MessageChunks.cs" />` to the mod csproj, build (exit 0), commit** with message `Mod sync: split long messages into packet-sized parts`.

---

### Task 4: ModSyncPacket

**Files:**
- Create: `Network/Packets/Implementation/ModSyncPacket.cs`
- Modify: `Network/Packets/PacketType.cs`, `Adammantium Multiplayer Mod.csproj`

This packet calls `ModSyncClient`/`ModSyncHost`, which come in Tasks 6–7. To keep every task buildable, this task adds the two classes as empty stubs with exactly the signatures used here; Tasks 6 and 7 replace their bodies.

- [ ] **Step 1: Add the packet type** in `Network/Packets/PacketType.cs`, directly after `MOD_LIST                    = 110,`:

```csharp
        MOD_SYNC,
```

- [ ] **Step 2: Create `Network/Packets/Implementation/ModSyncPacket.cs`:**

```csharp
using AMP.ModSync;
using AMP.Network.Data;
using Netamite.Client.Definition;
using Netamite.Network.Packet;
using Netamite.Network.Packet.Attributes;
using Netamite.Server.Definition;
using System.Collections.Generic;
using System.Threading;

namespace AMP.Network.Packets.Implementation {
    // Carries mod sync JSON messages (possibly split into parts) in both directions.
    [PacketDefinition((byte) PacketType.MOD_SYNC)]
    public class ModSyncPacket : AMPPacket {
        public const byte HOST_MOD_SET  = 1;
        public const byte JOINER_REPORT = 2;

        [SyncedVar] public byte   kind;
        [SyncedVar] public int    transferId;
        [SyncedVar] public int    part;
        [SyncedVar] public int    parts;
        [SyncedVar] public string data;

        private static int nextTransferId;

        public ModSyncPacket() { }

        public ModSyncPacket(byte kind, int transferId, int part, int parts, string data) {
            this.kind       = kind;
            this.transferId = transferId;
            this.part       = part;
            this.parts      = parts;
            this.data       = data;
        }

        internal static List<ModSyncPacket> Create(byte kind, string json) {
            int id = Interlocked.Increment(ref nextTransferId);
            List<string> chunks = MessageChunks.Split(json);
            var packets = new List<ModSyncPacket>(chunks.Count);
            for(int i = 0; i < chunks.Count; i++) packets.Add(new ModSyncPacket(kind, id, i, chunks.Count, chunks[i]));
            return packets;
        }

        public override bool ProcessClient(NetamiteClient client) {
            ModSyncClient.OnPacket(this);
            return true;
        }

        public override bool ProcessServer(NetamiteServer server, ClientData client) {
            ModSyncHost.OnPacket(client, this);
            return true;
        }
    }
}
```

- [ ] **Step 3: Create stubs** `ModSync/ModSyncClient.cs`:

```csharp
using AMP.Network.Packets.Implementation;

namespace AMP.ModSync {
    internal static class ModSyncClient {
        internal static void OnPacket(ModSyncPacket packet) { }
    }
}
```

and `ModSync/ModSyncHost.cs`:

```csharp
using AMP.Network.Data;
using AMP.Network.Packets.Implementation;

namespace AMP.ModSync {
    internal static class ModSyncHost {
        internal static void OnPacket(ClientData client, ModSyncPacket packet) { }
    }
}
```

- [ ] **Step 4: Add to the csproj:**

```xml
    <Compile Include="ModSync\ModSyncClient.cs" />
    <Compile Include="ModSync\ModSyncHost.cs" />
    <Compile Include="Network\Packets\Implementation\ModSyncPacket.cs" />
```

- [ ] **Step 5: Build and run the packet round-trip test.** Expected: build exit 0; packettest prints `OK, 51 packets` (one more than before, no FAIL lines).

- [ ] **Step 6: Commit** with message `Mod sync: packet for mod list messages`.

---

### Task 5: Settings + paths

**Files:**
- Modify: `Data/SafeFile.cs`, `Network/Data/ClientData.cs`, `ModManager.cs`
- Create: `ModSync/ModSyncPaths.cs`

- [ ] **Step 1: Host settings.** In `Data/SafeFile.cs` inside `HostingSettings`, after the `ownerHoldSeconds` line add:

```csharp
            // Mod sync on join: "off" (old behaviour), "warn" (joiners see differences and may still join), "enforce" (joiners must match)
            public string   modSyncMode            = "warn";
            // Mods joiners may keep, lack or have in another version; AMP itself always counts as personal
            public string[] personalMods           = new string[] { "HJ_NexusModManager" };
```

- [ ] **Step 2: Per-client state.** In `Network/Data/ClientData.cs`, after `internal bool greeted = false;` add:

```csharp
        internal string modSyncState = ""; // "", "waiting", "setup", "ready" (see ModSyncHost)
```

- [ ] **Step 3: Create `ModSync/ModSyncPaths.cs`:**

```csharp
using System.IO;
using ThunderRoad;
using UnityEngine;

namespace AMP.ModSync {
    // Unity paths may only be read on the main thread, so they are resolved once at startup and then used from background scans
    internal static class ModSyncPaths {
        internal static string ModsFolder  { get; private set; }
        internal static string CacheFolder { get; private set; }
        internal static string Platform    { get; private set; }

        internal static void Init() {
            ModsFolder  = FileManager.GetFullPath(FileManager.Type.JSONCatalog, FileManager.Source.Mods);
            CacheFolder = Path.Combine(Directory.GetParent(ModsFolder.TrimEnd('/', '\\')).FullName, "AMP_ModCache");
            Platform    = Application.platform == RuntimePlatform.Android ? "android" : "windows";
        }
    }
}
```

Add `<Compile Include="ModSync\ModSyncPaths.cs" />` to the csproj.

- [ ] **Step 4: Call it at startup.** In `ModManager.cs`, directly before `if(safeFile.modSettings.useBrowserIntegration) {` add:

```csharp
            ModSync.ModSyncPaths.Init();
```

- [ ] **Step 5: Build (exit 0), commit** with message `Mod sync: host settings, client state and paths`.

---

### Task 6: ModSyncHost (server side) + greeting gate

**Files:**
- Modify: `ModSync/ModSyncHost.cs` (replace stub), `Network/Server/Server.cs`, `ModManager.cs`

- [ ] **Step 1: Replace `ModSync/ModSyncHost.cs`:**

```csharp
using AMP.Logging;
using AMP.Network.Data;
using AMP.Network.Packets.Implementation;
using Netamite.Server.Data;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AMP.ModSync {
    // Server side: sends every joiner the host's mod list and holds back the normal greeting until the joiner answers.
    internal static class ModSyncHost {
        private const int REPORT_TIMEOUT_MS = 60 * 1000;      // a joiner without mod sync never answers
        private const int SETUP_TIMEOUT_MS  = 30 * 60 * 1000; // time a joiner gets to look at / set up mods

        private static Task<string> hostModSetJson;
        private static readonly MessageAssembler assembler = new MessageAssembler();

        private static string Mode {
            get { return ModManager.safeFile.hostingSettings.modSyncMode ?? ModSyncMode.Warn; }
        }

        // Main thread, before the server starts: settings and paths are read here, the folder scan runs in the background
        internal static void Prepare() {
            hostModSetJson = null;
            if(Mode == ModSyncMode.Off) return;

            string modsFolder  = ModSyncPaths.ModsFolder;
            string cacheFolder = ModSyncPaths.CacheFolder;
            var set = new HostModSet {
                platform     = ModSyncPaths.Platform,
                mode         = Mode,
                personalMods = ModManager.safeFile.hostingSettings.personalMods ?? new string[0],
            };
            hostModSetJson = Task.Run(() => {
                List<ModEntry> mods = ModInventory.Scan(modsFolder, cacheFolder, true);
                set.mods      = mods.Where(m => m.enabled).ToList();
                set.loadOrder = ModInventory.ReadLoadOrder(modsFolder);
                ModEntry amp  = mods.FirstOrDefault(m => m.isAmp);
                set.ampBuild  = amp != null ? amp.hash : "";
                Log.Info(Defines.SERVER, $"Mod sync ({set.mode}): hosting with {set.mods.Count} mods.");
                return JsonConvert.SerializeObject(set);
            });
        }

        internal static void OnClientConnected(ClientData client) {
            if(hostModSetJson == null) { // mod sync off: the old, delayed greeting
                ModManager.serverInstance.GreetPlayer((ClientInformation) client);
                return;
            }

            client.modSyncState = "waiting";
            hostModSetJson.ContinueWith(t => {
                if(t.IsFaulted) {
                    Log.Err(Defines.SERVER, "Mod sync: scanning the host's mods failed, joining without it. " + t.Exception.GetBaseException().Message);
                    Greet(client);
                    return;
                }
                foreach(ModSyncPacket packet in ModSyncPacket.Create(ModSyncPacket.HOST_MOD_SET, t.Result)) {
                    ModManager.serverInstance.netamiteServer.SendTo(client, packet);
                }
                Task.Delay(REPORT_TIMEOUT_MS).ContinueWith(_ => OnReportTimeout(client));
            });
        }

        internal static void OnPacket(ClientData client, ModSyncPacket packet) {
            if(packet.kind != ModSyncPacket.JOINER_REPORT) return;
            string json = assembler.Add(client.ClientId + ":" + packet.transferId, packet.part, packet.parts, packet.data);
            if(json == null) return;

            JoinerReport report;
            try {
                report = JsonConvert.DeserializeObject<JoinerReport>(json);
            } catch(JsonException) {
                return;
            }
            if(report == null) return;

            if(report.status == JoinerStatus.Ready) {
                if(!string.IsNullOrEmpty(report.detail)) Log.Info(Defines.SERVER, $"Mod sync: {client.ClientName} joins with differences: {report.detail}");
                Greet(client);
            } else if(report.status == JoinerStatus.NeedsSetup) {
                client.modSyncState = "setup";
                Log.Info(Defines.SERVER, $"Mod sync: {client.ClientName} ({report.platform}) needs to set up mods:\n{report.detail}");
                Task.Delay(SETUP_TIMEOUT_MS).ContinueWith(_ => {
                    if(client.modSyncState == "setup" && IsConnected(client)) client.Kick("Mod setup took too long.");
                });
            }
            // Declined: the joiner disconnects by itself
        }

        private static void OnReportTimeout(ClientData client) {
            if(client.modSyncState != "waiting" || !IsConnected(client)) return;
            if(Mode == ModSyncMode.Enforce) {
                client.Kick("This server checks mods on join. Update AMP to join.");
            } else {
                Log.Info(Defines.SERVER, $"Mod sync: {client.ClientName} didn't answer (older AMP?), joining without mod check.");
                Greet(client);
            }
        }

        private static void Greet(ClientData client) {
            client.modSyncState = "ready";
            ModManager.serverInstance?.GreetPlayer(client, false);
        }

        private static bool IsConnected(ClientData client) {
            return ModManager.serverInstance != null && ModManager.serverInstance.netamiteServer.GetClientById(client.ClientId) != null;
        }
    }
}
```

- [ ] **Step 2: Route connections through it.** In `Network/Server/Server.cs` replace

```csharp
                netamiteServer.OnConnect += GreetPlayer;
```

with

```csharp
                netamiteServer.OnConnect += (client) => ModSync.ModSyncHost.OnClientConnected((ClientData) client);
```

`GreetPlayer(ClientInformation)` (the 1 s delayed variant) stays and is used by `OnClientConnected` when mod sync is off.

- [ ] **Step 3: Prepare before hosting.** In `ModManager.cs`, in `HostServer(uint maxPlayers, int port, string password, Action<string> callback)` add as the first line after `StopHost();`:

```csharp
            ModSync.ModSyncHost.Prepare();
```

and the same line in `HostSteamServer` after its `StopHost();`.

- [ ] **Step 4: Build (exit 0). Run all headless suites** (ModSyncTests, packettest `OK, 51 packets`, `cd /c/Projects/bns-mp/policytest && dotnet run` → 15 PASS).

- [ ] **Step 5: Commit** with message `Mod sync: host sends its mod list and waits for the joiner before greeting`.

---

### Task 7: ModSyncClient (joiner side)

**Files:**
- Modify: `ModSync/ModSyncClient.cs` (replace stub)

- [ ] **Step 1: Replace `ModSync/ModSyncClient.cs`:**

```csharp
using AMP.Extension;
using AMP.GameInteraction.Components;
using AMP.Logging;
using AMP.Network.Packets.Implementation;
using AMP.Threading;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace AMP.ModSync {
    // Joiner side: compares the host's mods with ours, reports back, and shows what differs.
    internal static class ModSyncClient {
        private static readonly MessageAssembler assembler = new MessageAssembler();

        internal static HostModSet HostSet { get; private set; }
        internal static SyncPlan   Plan    { get; private set; }

        internal static void OnPacket(ModSyncPacket packet) {
            if(packet.kind != ModSyncPacket.HOST_MOD_SET) return;
            string json = assembler.Add("host:" + packet.transferId, packet.part, packet.parts, packet.data);
            if(json == null) return;

            HostModSet set;
            try {
                set = JsonConvert.DeserializeObject<HostModSet>(json);
            } catch(JsonException e) {
                Log.Err(Defines.CLIENT, "Mod sync: the host sent an unreadable mod list. " + e.Message);
                Report(JoinerStatus.Ready, "unreadable mod list");
                return;
            }
            if(set == null) return;

            string modsFolder  = ModSyncPaths.ModsFolder;
            string cacheFolder = ModSyncPaths.CacheFolder;
            string platform    = ModSyncPaths.Platform;
            bool samePlatform  = string.Equals(set.platform, platform, StringComparison.OrdinalIgnoreCase);

            Task.Run(() => {
                List<ModEntry> local = ModInventory.Scan(modsFolder, cacheFolder, samePlatform);
                return SyncPlanner.Plan(set, local, ModInventory.ReadLoadOrder(modsFolder), platform);
            }).ContinueWith(t => Dispatcher.Enqueue(() => {
                if(t.IsFaulted) {
                    // A scan problem must not lock anyone out
                    Log.Err(Defines.CLIENT, "Mod sync: checking your mods failed. " + t.Exception.GetBaseException().Message);
                    Report(JoinerStatus.Ready, "mod check failed");
                    return;
                }
                Handle(set, t.Result);
            }));
        }

        private static void Handle(HostModSet set, SyncPlan plan) {
            HostSet = set;
            Plan    = plan;
            if(plan.ampDiffers) Log.Warn(Defines.CLIENT, "Mod sync: your AMP build differs from the host's.");

            if(!plan.BlocksJoin) {
                List<SyncStep> missing = plan.Of(SyncAction.Unavailable).ToList();
                if(missing.Count > 0) {
                    ShowNotice("Joining without mods that aren't available on your platform:\n" + string.Join(", ", missing.Select(s => s.Mod.name)));
                }
                Report(JoinerStatus.Ready, missing.Count > 0 ? plan.Describe() : "");
                return;
            }

            Report(JoinerStatus.NeedsSetup, plan.Describe());
            bool canJoinAnyway = set.mode != ModSyncMode.Enforce;
            UI.IngameModUI.currentUI?.ShowModSync(plan.Describe(), canJoinAnyway);
            ShowNotice("Your mods don't match the host's.\nOpen the book: Mods > AMP.");
        }

        // Only offered when the host runs mod sync in "warn" mode
        internal static void JoinAnyway() {
            if(Plan == null) return;
            Report(JoinerStatus.Ready, Plan.Describe());
        }

        internal static void Decline() {
            Report(JoinerStatus.Declined, "");
            ModManager.StopClient();
        }

        private static void Report(string status, string detail) {
            if(ModManager.clientInstance == null) return;
            var report = new JoinerReport { platform = ModSyncPaths.Platform, status = status, detail = detail };
            foreach(ModSyncPacket packet in ModSyncPacket.Create(ModSyncPacket.JOINER_REPORT, JsonConvert.SerializeObject(report))) {
                packet.SendToServerReliable();
            }
        }

        private static void ShowNotice(string text) {
            TextDisplay.ShowTextDisplay(new DisplayTextPacket("modsync", text, Color.yellow, Vector3.forward * 2, true, true, 15));
        }
    }
}
```

- [ ] **Step 2: Build.** It fails until Task 8 adds `IngameModUI.ShowModSync`. Expected error: `CS1061 'IngameModUI' does not contain a definition for 'ShowModSync'`. Continue directly with Task 8 and commit both together.

---

### Task 8: Mod sync page in the book UI

**Files:**
- Modify: `UI/IngameModUI.cs`

- [ ] **Step 1: Page and fields.** In the `Page` enum add `ModSync = 8,` after `Moderation = 7,`. Next to `RectTransform disconnectPanel;` add:

```csharp
        RectTransform modSyncPanel;
        TextMeshProUGUI modSyncText;
        GameObject modSyncJoinAnyway;
```

- [ ] **Step 2: Build the panel.** Directly after the `#endregion` that closes `#region Connecting`, add:

```csharp
            #region ModSync
            gobj = CreateObject("ModSyncPanel");
            gobj.transform.SetParent(transform);
            modSyncPanel = gobj.AddComponent<RectTransform>();

            gobj = CreateObject("ModSyncTitle");
            gobj.transform.SetParent(modSyncPanel);
            textMesh = gobj.AddComponent<TextMeshProUGUI>();
            textMesh.text = "Your mods don't match the host's";
            textMesh.fontSize = 55;
            textMesh.fontStyle = FontStyles.Bold;
            textMesh.color = Color.black;
            textMesh.alignment = TextAlignmentOptions.Center;
            rect = gobj.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(1000, 80);
            rect.localPosition = new Vector3(0, 330, 0);

            gobj = CreateObject("ModSyncText");
            gobj.transform.SetParent(modSyncPanel);
            modSyncText = gobj.AddComponent<TextMeshProUGUI>();
            modSyncText.fontSize = 34;
            modSyncText.enableAutoSizing = true;
            modSyncText.fontSizeMin = 18;
            modSyncText.fontSizeMax = 34;
            modSyncText.color = Color.black;
            modSyncText.alignment = TextAlignmentOptions.TopLeft;
            rect = gobj.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(1000, 480);
            rect.localPosition = new Vector3(0, 30, 0);

            gobj = CreateObject("ModSyncJoinAnyway");
            gobj.transform.SetParent(modSyncPanel);
            modSyncJoinAnyway = gobj;
            rect = gobj.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(450, 130);
            rect.localPosition = new Vector3(-245, -300, 0);
            btn = gobj.AddComponent<Button>();
            btn.targetGraphic = gobj.AddComponent<Image>();
            btn.targetGraphic.color = new Color(1, 1, 1, 0.6f);
            btn.colors = buttonColor;
            btn.onClick.AddListener(() => {
                #if AMP
                ModSync.ModSyncClient.JoinAnyway();
                #endif
                ShowPage(Page.Connecting);
                connectingMessage.text = "Joining...";
            });
            text = CreateObject("Text");
            text.transform.SetParent(btn.transform, false);
            rect = text.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(450, 130);
            btnText = text.AddComponent<TextMeshProUGUI>();
            btnText.text = "Join anyway";
            btnText.fontSize = 55;
            btnText.color = Color.black;
            btnText.alignment = TextAlignmentOptions.Center;

            gobj = CreateObject("ModSyncLeave");
            gobj.transform.SetParent(modSyncPanel);
            rect = gobj.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(450, 130);
            rect.localPosition = new Vector3(245, -300, 0);
            btn = gobj.AddComponent<Button>();
            btn.targetGraphic = gobj.AddComponent<Image>();
            btn.targetGraphic.color = new Color(1, 1, 1, 0.6f);
            btn.colors = buttonColor;
            btn.onClick.AddListener(() => {
                #if AMP
                ModSync.ModSyncClient.Decline();
                #endif
                ShowPage(Page.Serverlist);
            });
            text = CreateObject("Text");
            text.transform.SetParent(btn.transform, false);
            rect = text.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(450, 130);
            btnText = text.AddComponent<TextMeshProUGUI>();
            btnText.text = "Disconnect";
            btnText.fontSize = 55;
            btnText.color = Color.red;
            btnText.alignment = TextAlignmentOptions.Center;
            #endregion
```

Check before adding: the method that builds the UI already declares local variables named `gobj`, `rect`, `btn`, `text`, `btnText` and `textMesh` (they are used in the Host region). If `textMesh` is not declared in that method, declare it as `TextMeshProUGUI textMesh;` at the top of the ModSync region instead.

- [ ] **Step 3: Show/hide it.** In `ShowPage`, next to `disconnectPanel.gameObject.SetActive(false);` add `modSyncPanel.gameObject.SetActive(false);`, and add a case before `default:`:

```csharp
                case Page.ModSync: {
                    buttonBar.gameObject.SetActive(false);
                    modSyncPanel.gameObject.SetActive(true);
                    break;
                }
```

- [ ] **Step 4: Public entry point.** Add to the class (next to `HostOnThisPC`):

```csharp
        // Called by ModSyncClient on the main thread when the host's mods differ from ours
        internal void ShowModSync(string description, bool canJoinAnyway) {
            modSyncText.text = description;
            modSyncJoinAnyway.SetActive(canJoinAnyway);
            ShowPage(Page.ModSync);
        }
```

- [ ] **Step 5: Build** — expected exit 0. Run all three headless suites (all pass, packettest `OK, 51 packets`).

- [ ] **Step 6: Commit Tasks 7 + 8 together:**

```bash
cd /c/Projects/bns-mp/amp
git add ModSync/ModSyncClient.cs UI/IngameModUI.cs
git -c user.name=amp-review -c user.email=noreply@example.invalid commit -m "Mod sync: joiner compares mods, reports to the host and shows the differences

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: End-to-end check, review, push

- [ ] **Step 1: Final build + all tests** (mod build exit 0; ModSyncTests `ALL PASSED`; packettest `OK, 51 packets`; policytest 15 PASS; `cd /c/Projects/bns-mp/nettest && dotnet build -nologo -v:q -nodeReuse:false -p:UseSharedCompilation=false && ./bin/Debug/net472/nettest.exe` → `ALL PASSED`).
- [ ] **Step 2: Code review** of the Phase 1 commits (superpowers:requesting-code-review); fix confirmed issues.
- [ ] **Step 3: Push:** `cd /c/Projects/bns-mp/amp && git push fork fix/review-pass:community`.
- [ ] **Step 4: Update the test build** (`C:\Projects\bns-mp\test-build\MultiplayerMod`) with the new `AMP.dll` and note in `HOW-TO-PLAY.txt`: host setting `modSyncMode` ("off" / "warn" / "enforce") and `personalMods`.

In-game checks for the user (not automatable here):
1. Host and joiner with identical mods: joiner joins as before.
2. Joiner with one extra mod, host `modSyncMode: "warn"`: joiner sees the list and "Join anyway" works.
3. Same with `"enforce"`: only "Disconnect" is offered.
4. Official AMP (no mod sync) joining a `"warn"` host: joins after ~60 s; `"enforce"` host: kicked with the update message.
