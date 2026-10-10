# Mod Sync Phase 3b (Apply Downloads: Journal, Helper, Restart + Rejoin, Restore) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** On the mod sync page a PC joiner presses **Download & restart** (choosing Keep or Temporary): mods download from the host (phase 3a), the game closes, a helper swaps the mod folders, the game relaunches through Steam and reconnects to the host by itself. Temporary sessions put the player's own mods back when they quit. Every folder change is journaled so it can always be undone, even after a crash.

**Architecture:** Pure, headless-tested core: `SwapJournal` (what to change and how to undo it), `JournalPlanner` (SyncPlan + cache → journal), `JournalApplier` (apply / restore with retries and rollback; plain file moves and renames). The PC helper is a generated PowerShell script that waits for the game to exit, loads `AMP.dll` from a copy with `[Reflection.Assembly]::LoadFrom` and calls `JournalApplier` — one implementation, tested in C# and once through real PowerShell. Game glue: the download button and progress, quitting through the helper, auto-rejoin on the next start, restore on quit, "Restore my mods".

**Out of scope here (phase 3c):** separate multiplayer save profile, AMP self-update. Until 3c, the plan page warns: "Characters keep items only from mods that are installed; use a separate character for multiplayer."

**Research (verified, see `C:\Projects\bns-mp\research`):** mod DLLs are loaded from bytes (not locked) but Addressables keeps `.bundle` files open, so swaps must happen with the game closed on PC. Game exe `BladeAndSorcery.exe`, Steam app id 629730. Windows PowerShell 5.1 is part of Windows 10/11. A process started by the game may be in the game's job object; starting the helper through WMI `Win32_Process.Create` detaches it. Folders starting with `_` are skipped by the game. `loadorder.json` lives in the Mods folder.

## Conventions

As in phases 1–3a: repo `C:\Projects\bns-mp\amp` branch `fix/review-pass`; C# 7.3; csproj entries; `bash /c/Projects/bns-mp/build.sh`; ModSyncTests / packettest (`OK, 52 packets` unless a packet is added) / policytest; one build at a time; never `dotnet build-server shutdown`; never touch the game folder (tests use temp folders); Edit tool; commit identity `amp-review` + trailer naming your model.

## File structure

| File | Responsibility |
|---|---|
| `ModSync/SwapJournal.cs` (create) | Journal model + JSON load/save. Pure. |
| `ModSync/JournalPlanner.cs` (create) | Build a journal from a `SyncPlan` + `ModCache`. Pure. |
| `ModSync/JournalApplier.cs` (create) | Apply / restore a journal on disk with retries and rollback; `RunFromHelper` entry point. Pure. |
| `ModSync/HelperScript.cs` (create) | Generate the PowerShell helper and the WMI launcher command. Pure (string building). |
| `ModSync/ModSwapClient.cs` (create) | Game glue: download → journal → helper → quit; startup: rejoin / offer restore; restore on quit. |
| `UI/IngameModUI.cs` (modify) | Keep/Temporary toggle, Download & restart, progress, Restore my mods. |
| `ModSync/ModSyncClient.cs`, `ModManager.cs`, `ModLoader.cs` (modify) | Hooks. |
| `Tests/ModSyncTests/*` (modify) | Journal, applier, helper tests (incl. one real PowerShell run). |

---

### Task 1: SwapJournal + JournalPlanner (pure, TDD)

Journal operations, applied in order and undone in reverse order:

| Op | Apply | Undo (restore) |
|---|---|---|
| `Disable X` | rename `Mods/X` → `Mods/_AMPoff_X` | rename back |
| `MoveAside X` | rename `Mods/X` → `Mods/_AMPold_X` | rename back |
| `Install X from cache H` | move `cache/mods/H/content` → `Mods/X` | move `Mods/X` back to `cache/mods/H/content` |
| `LoadOrder` | copy `Mods/loadorder.json` (if any) → `<journal dir>/loadorder.backup.json`, write host order | restore backup (or delete file if there was none) |

Replace = `MoveAside` + `Install`. Keep sessions undo only `Disable`, `MoveAside`-of-replaced… no: **Keep** undoes `Disable` and `LoadOrder`; installed mods stay; `_AMPold_X` copies stay disabled (the player can delete them). **Temporary** undoes everything.

- [ ] **Step 1: Tests** (add `SwapJournal.cs`, `JournalPlanner.cs` to the test csproj; `JournalTests();` in `Main`):

```csharp
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
```

(`M(...)` is the existing helper from PlannerTests.)

- [ ] **Step 2: Run** → fails. **Step 3: Implement.**

`ModSync/SwapJournal.cs`:

```csharp
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;

namespace AMP.ModSync {
    public enum SwapMode  { Temporary, Keep }
    // Planned: written, nothing changed yet. Applied: folders swapped. RestorePending: restore requested (runs when the game is closed). Restored: back to normal.
    public enum SwapState { Planned, Applied, RestorePending, Restored }
    public enum SwapOpKind { Disable, MoveAside, Install, LoadOrder }

    public class SwapOp {
        [JsonConverter(typeof(StringEnumConverter))]
        public SwapOpKind kind;
        public string folder = "";
        public string hash   = "";   // Install: cache entry
        public bool   done;          // set by the applier once the change is on disk
    }

    // Everything needed to apply and undo one mod swap, and to reconnect afterwards. Saved as AMP_ModCache/session.json.
    public class SwapJournal {
        public string id = Guid.NewGuid().ToString("N");
        [JsonConverter(typeof(StringEnumConverter))] public SwapMode  mode;
        [JsonConverter(typeof(StringEnumConverter))] public SwapState state = SwapState.Planned;
        public string   host     = "";
        public int      port;
        public string   password = "";
        public bool     rejoinPending;          // reconnect after the next start
        public string[] hostLoadOrder = new string[0];
        public bool     hadLoadOrder;           // there was a loadorder.json before the swap
        public List<SwapOp> ops = new List<SwapOp>();
        public string   lastError = "";

        public string ToJson() { return JsonConvert.SerializeObject(this, Formatting.Indented); }

        public static SwapJournal FromJson(string json) {
            try {
                return JsonConvert.DeserializeObject<SwapJournal>(json);
            } catch(JsonException) {
                return null;
            }
        }
    }
}
```

`ModSync/JournalPlanner.cs`:

```csharp
using System.Linq;

namespace AMP.ModSync {
    public static class JournalPlanner {
        // Disables first, then replaced mods are moved aside, then installs, then the load order
        public static SwapJournal Build(SyncPlan plan, string[] hostLoadOrder, string host, int port, string password, SwapMode mode) {
            var journal = new SwapJournal { mode = mode, host = host ?? "", port = port, password = password ?? "", rejoinPending = true,
                                            hostLoadOrder = hostLoadOrder ?? new string[0] };
            foreach(SyncStep step in plan.Of(SyncAction.Disable))
                journal.ops.Add(new SwapOp { kind = SwapOpKind.Disable, folder = step.localMod.folder });
            foreach(SyncStep step in plan.Of(SyncAction.Replace))
                journal.ops.Add(new SwapOp { kind = SwapOpKind.MoveAside, folder = step.localMod.folder });
            foreach(SyncStep step in plan.Of(SyncAction.Replace).Concat(plan.Of(SyncAction.Add)))
                journal.ops.Add(new SwapOp { kind = SwapOpKind.Install, folder = step.hostMod.folder, hash = step.hostMod.hash });
            if(plan.loadOrderDiffers)
                journal.ops.Add(new SwapOp { kind = SwapOpKind.LoadOrder });
            return journal;
        }
    }
}
```

Note the test expects the Replace MoveAside before the Replace Install for the same folder — the ordering above satisfies it. Folder names come from our own inventory/plan (the host's `folder` for Install was validated in phase 3a: `NormalizeRelativePath(folder) == folder`, no `/`) — `JournalApplier` re-validates anyway.

- [ ] **Step 4: Run tests. Step 5:** csproj, build, commit `Mod swap: journal and planner`.

---

### Task 2: JournalApplier (pure, TDD on temp folders)

- [ ] **Step 1: Tests** (`JournalApplier.cs` to the test csproj; `ApplierTests();` in `Main`):

```csharp
    private static void ApplierTests() {
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
            T("old mod moved aside, host version installed", File.ReadAllText(Path.Combine(f.Mods, "_AMPold_Old", "v.txt")) == "mine" && File.ReadAllText(Path.Combine(f.Mods, "Old", "v.txt")) == "host");
            T("new mod installed from cache", File.Exists(Path.Combine(f.Mods, "New", "manifest.json")));
            T("host load order written", File.ReadAllText(Path.Combine(f.Mods, "loadorder.json")).Contains("New"));

            T("temporary restore succeeds", JournalApplier.Restore(j, f.Mods, cacheRoot, journalDir) && j.state == SwapState.Restored);
            T("everything is back", File.ReadAllText(Path.Combine(f.Mods, "Old", "v.txt")) == "mine" && Directory.Exists(Path.Combine(f.Mods, "Extra"))
                                     && !Directory.Exists(Path.Combine(f.Mods, "New")) && !Directory.Exists(Path.Combine(f.Mods, "_AMPold_Old")));
            T("cache entries are back", File.Exists(Path.Combine(cacheRoot, "mods", hNew, "content", "manifest.json")));
            T("original load order is back", File.ReadAllText(Path.Combine(f.Mods, "loadorder.json")).Contains("Extra") && !File.ReadAllText(Path.Combine(f.Mods, "loadorder.json")).Contains("New"));

            var keep = SwapJournal.FromJson(j.ToJson());
            keep.mode = SwapMode.Keep; keep.state = SwapState.Planned; keep.ops.ForEach(o => o.done = false);
            T("keep apply succeeds", JournalApplier.Apply(keep, f.Mods, cacheRoot, journalDir));
            T("keep restore succeeds", JournalApplier.Restore(keep, f.Mods, cacheRoot, journalDir));
            T("keep: extra re-enabled, host mods stay", Directory.Exists(Path.Combine(f.Mods, "Extra")) && Directory.Exists(Path.Combine(f.Mods, "New"))
                                                       && File.ReadAllText(Path.Combine(f.Mods, "Old", "v.txt")) == "host" && Directory.Exists(Path.Combine(f.Mods, "_AMPold_Old")));

            // Failure in the middle rolls back what was done
            using(var g = new TestFolders()) {
                g.Mod("A");
                var bad = new SwapJournal { mode = SwapMode.Temporary };
                bad.ops.Add(new SwapOp { kind = SwapOpKind.Disable, folder = "A" });
                bad.ops.Add(new SwapOp { kind = SwapOpKind.Install, folder = "B", hash = new string('c', 64) }); // not in cache
                T("apply with a missing cache entry fails", !JournalApplier.Apply(bad, g.Mods, Path.Combine(g.Root, "cache"), Path.Combine(g.Root, "j")));
                T("...and rolls back the disable", Directory.Exists(Path.Combine(g.Mods, "A")) && bad.state == SwapState.Restored && bad.lastError.Length > 0);
            }

            using(var g = new TestFolders()) {
                var evil = new SwapJournal();
                evil.ops.Add(new SwapOp { kind = SwapOpKind.Disable, folder = "..\\x" });
                T("unsafe folder names are refused", !JournalApplier.Apply(evil, g.Mods, Path.Combine(g.Root, "cache"), Path.Combine(g.Root, "j")));
            }
        }
    }
```

- [ ] **Step 2: Run** → fails. **Step 3: Implement `ModSync/JournalApplier.cs`:**

```csharp
using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace AMP.ModSync {
    // Changes the Mods folder as described by a journal, and undoes it. Plain renames/moves inside the same drive, so it is fast.
    // Used in-game (Quest, and planning) and by the PC helper after the game has closed. Never throws.
    public static class JournalApplier {
        public const string OffPrefix = "_AMPoff_";
        public const string OldPrefix = "_AMPold_";
        public const int RetryMs = 30 * 1000; // antivirus / OneDrive can hold files briefly after the game exits

        public static bool Apply(SwapJournal journal, string modsFolder, string cacheRoot, string journalDir) {
            if(journal.ops.Any(o => !SafeFolder(o))) return Fail(journal, "Unsafe folder name in journal.");
            try {
                foreach(SwapOp op in journal.ops.Where(o => !o.done)) {
                    Do(op, journal, modsFolder, cacheRoot, journalDir);
                    op.done = true;
                }
                journal.state = SwapState.Applied;
                journal.lastError = "";
                return true;
            } catch(Exception e) {
                journal.lastError = e.Message;
                Undo(journal, modsFolder, cacheRoot, journalDir, true); // roll back whatever was done
                journal.state = SwapState.Restored;
                return false;
            }
        }

        // Temporary: undo everything. Keep: undo only Disable and LoadOrder.
        public static bool Restore(SwapJournal journal, string modsFolder, string cacheRoot, string journalDir) {
            if(journal.ops.Any(o => !SafeFolder(o))) return Fail(journal, "Unsafe folder name in journal.");
            bool ok = Undo(journal, modsFolder, cacheRoot, journalDir, journal.mode == SwapMode.Temporary);
            journal.state = ok ? SwapState.Restored : journal.state;
            return ok;
        }

        private static bool Undo(SwapJournal journal, string modsFolder, string cacheRoot, string journalDir, bool everything) {
            bool ok = true;
            for(int i = journal.ops.Count - 1; i >= 0; i--) {
                SwapOp op = journal.ops[i];
                if(!op.done) continue;
                if(!everything && (op.kind == SwapOpKind.Install || op.kind == SwapOpKind.MoveAside)) continue;
                try {
                    UndoOne(op, journal, modsFolder, cacheRoot, journalDir);
                    op.done = false;
                } catch(Exception e) {
                    journal.lastError = e.Message;
                    ok = false; // keep going: undo as much as possible, the journal remembers what's left
                }
            }
            return ok;
        }

        private static void Do(SwapOp op, SwapJournal journal, string mods, string cacheRoot, string journalDir) {
            switch(op.kind) {
                case SwapOpKind.Disable:   MoveDir(Path.Combine(mods, op.folder), Path.Combine(mods, OffPrefix + op.folder)); break;
                case SwapOpKind.MoveAside: MoveDir(Path.Combine(mods, op.folder), Path.Combine(mods, OldPrefix + op.folder)); break;
                case SwapOpKind.Install:   MoveDir(Path.Combine(cacheRoot, "mods", op.hash, "content"), Path.Combine(mods, op.folder)); break;
                case SwapOpKind.LoadOrder: {
                    string file = Path.Combine(mods, "loadorder.json");
                    Directory.CreateDirectory(journalDir);
                    journal.hadLoadOrder = File.Exists(file);
                    if(journal.hadLoadOrder) File.Copy(file, Path.Combine(journalDir, "loadorder.backup.json"), true);
                    File.WriteAllText(file, "{\"modNames\":[" + string.Join(",", journal.hostLoadOrder.Select(n => "\"" + n.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")) + "]}");
                    break;
                }
            }
        }

        private static void UndoOne(SwapOp op, SwapJournal journal, string mods, string cacheRoot, string journalDir) {
            switch(op.kind) {
                case SwapOpKind.Disable:   MoveDir(Path.Combine(mods, OffPrefix + op.folder), Path.Combine(mods, op.folder)); break;
                case SwapOpKind.MoveAside: MoveDir(Path.Combine(mods, OldPrefix + op.folder), Path.Combine(mods, op.folder)); break;
                case SwapOpKind.Install: {
                    string content = Path.Combine(cacheRoot, "mods", op.hash, "content");
                    Directory.CreateDirectory(Path.GetDirectoryName(content));
                    MoveDir(Path.Combine(mods, op.folder), content);
                    break;
                }
                case SwapOpKind.LoadOrder: {
                    string file = Path.Combine(mods, "loadorder.json");
                    string backup = Path.Combine(journalDir, "loadorder.backup.json");
                    if(journal.hadLoadOrder && File.Exists(backup)) File.Copy(backup, file, true);
                    else if(!journal.hadLoadOrder && File.Exists(file)) File.Delete(file);
                    break;
                }
            }
        }

        // Retries for a while: files can stay locked for a moment after the game exits
        private static void MoveDir(string from, string to) {
            if(!Directory.Exists(from)) throw new IOException("Missing folder: " + from);
            if(Directory.Exists(to)) throw new IOException("Folder already exists: " + to);
            DateTime giveUp = DateTime.UtcNow.AddMilliseconds(RetryMs);
            while(true) {
                try {
                    Directory.Move(from, to);
                    return;
                } catch(IOException) when (DateTime.UtcNow < giveUp) {
                    Thread.Sleep(500);
                } catch(UnauthorizedAccessException) when (DateTime.UtcNow < giveUp) {
                    Thread.Sleep(500);
                }
            }
        }

        private static bool SafeFolder(SwapOp op) {
            if(op.kind == SwapOpKind.LoadOrder) return true;
            string folder = op.folder ?? "";
            if(TransferRules.NormalizeRelativePath(folder) != folder || folder.Contains("/")) return false;
            if(op.kind == SwapOpKind.Install) return op.hash != null && op.hash.Length == 64 && op.hash.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
            return true;
        }

        private static bool Fail(SwapJournal journal, string error) {
            journal.lastError = error;
            return false;
        }

        // Entry point for the PC helper (PowerShell). Returns "" on success or an error text. Writes the updated journal back.
        public static string RunFromHelper(string journalPath, string modsFolder, string cacheRoot, string action) {
            try {
                SwapJournal journal = SwapJournal.FromJson(File.ReadAllText(journalPath));
                if(journal == null) return "Journal unreadable.";
                string journalDir = Path.GetDirectoryName(journalPath);
                bool ok = action == "apply" ? Apply(journal, modsFolder, cacheRoot, journalDir)
                                            : Restore(journal, modsFolder, cacheRoot, journalDir);
                File.WriteAllText(journalPath, journal.ToJson());
                return ok ? "" : journal.lastError;
            } catch(Exception e) {
                return e.Message;
            }
        }
    }
}
```

In tests `RetryMs` makes failing cases slow (30 s per missing folder). Make it settable for tests: `public static int RetryMs = 30 * 1000;` (not const) and set `JournalApplier.RetryMs = 200;` at the start of `ApplierTests`, restoring it after. Missing source folders should fail immediately (no retry) — the `Directory.Exists(from)` check already throws before the loop.

- [ ] **Step 4: Run tests. Step 5:** csproj, build, commit `Mod swap: apply and restore with rollback`.

---

### Task 3: PowerShell helper (pure generator + one real PowerShell test)

- [ ] **Step 1: Implement `ModSync/HelperScript.cs`:**

```csharp
using System;
using System.Text;

namespace AMP.ModSync {
    // The PC helper: runs after the game has exited, swaps the Mods folder via JournalApplier (loaded from a copy of AMP.dll), optionally relaunches through Steam.
    public static class HelperScript {
        public const string SteamAppId = "629730";

        public static string Script() {
            return
@"param([int]$GamePid, [string]$AmpDll, [string]$JsonDll, [string]$Journal, [string]$Mods, [string]$Cache, [string]$Action, [int]$Relaunch, [string]$Log)
$ErrorActionPreference = 'Continue'
function Write-Log($text) { Add-Content -Path $Log -Value ((Get-Date).ToString('s') + ' ' + $text) }
Write-Log ""helper start: $Action pid=$GamePid""
if($GamePid -gt 0) { Wait-Process -Id $GamePid -Timeout 300 -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 2
try {
    [void][Reflection.Assembly]::LoadFrom($JsonDll)
    [void][Reflection.Assembly]::LoadFrom($AmpDll)
    $result = [AMP.ModSync.JournalApplier]::RunFromHelper($Journal, $Mods, $Cache, $Action)
    if($result) { Write-Log ""failed: $result"" } else { Write-Log 'done' }
} catch {
    Write-Log (""error: "" + $_.Exception.Message)
}
if($Relaunch -eq 1) { Start-Process ('steam://rungameid/" + SteamAppId + @"'); Write-Log 'relaunched' }
";
        }

        // Command for a short-lived powershell that starts the helper through WMI, so the helper isn't part of the game's process tree/job
        public static string LauncherArguments(string scriptPath, int gamePid, string ampDll, string jsonDll, string journal, string mods, string cache, string action, bool relaunch, string log) {
            string inner = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File " + Q(scriptPath)
                         + " -GamePid " + gamePid + " -AmpDll " + Q(ampDll) + " -JsonDll " + Q(jsonDll) + " -Journal " + Q(journal)
                         + " -Mods " + Q(mods) + " -Cache " + Q(cache) + " -Action " + action + " -Relaunch " + (relaunch ? 1 : 0) + " -Log " + Q(log);
            string command = "Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = '" + inner.Replace("'", "''") + "' } | Out-Null";
            return "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        }

        private static string Q(string path) { return "\"" + path + "\""; }
    }
}
```

Paths contain spaces and `&` ("Blade & Sorcery"); quoting with `"..."` inside a WMI command line is correct for `-File` arguments; the `&` is safe because the inner command line isn't parsed by cmd. Reject paths containing `"` before building (throw ArgumentException) — Windows paths can't contain `"` anyway.

- [ ] **Step 2: Real PowerShell test** (Windows only; skip with a PASS-note if `powershell.exe` isn't found). Add `HelperTests();` to `Main`:

```csharp
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
                HelperScript.LauncherArguments(script, dummy.Id, ampDll, jsonDll, journalPath, f.Mods, cache, "apply", false, log)) { CreateNoWindow = true, UseShellExecute = false });
            launcher.WaitForExit(30000);

            DateTime until = DateTime.UtcNow.AddSeconds(60);
            while(DateTime.UtcNow < until && !(File.Exists(log) && File.ReadAllText(log).Contains("done") || File.Exists(log) && File.ReadAllText(log).Contains("fail"))) System.Threading.Thread.Sleep(500);

            T("helper ran through WMI and applied the journal", Directory.Exists(Path.Combine(f.Mods, "_AMPoff_Extra")));
            T("helper wrote the updated journal", SwapJournal.FromJson(File.ReadAllText(journalPath)).state == SwapState.Applied);
        }
    }
```

Important: the test exe is `ModSyncTests.exe`, so `[AMP.ModSync.JournalApplier]` resolves from it (same namespace/type, compiled from the same source). Loading the test exe with `LoadFrom` is fine. If WMI process creation is blocked on this machine, report it (don't weaken the test) — that would also affect real players.

Add `HelperScript.cs` to both csproj files. **Step 3: Run tests, build, commit** `Mod swap: PowerShell helper started outside the game's process tree`.

---

### Task 4: Game glue + UI

Requirements (implementer adapts to the real code; read `ModSyncClient.cs`, `ModTransferClient.cs`, `ModSyncPaths.cs`, `IngameModUI.cs` (ModSync page, `ShowModSync`, `UpdateConnectionScreen`), `ModManager.cs` (`Initialize`, `JoinServer`, `StopClientImmediatly`), `GUIManager.JoinServer`, `ModLoader.cs`):

1. **`ModSync/ModSwapClient.cs`** (main thread unless noted):
   - `JournalPath` = `CacheFolder/session.json`; `HelperFolder` = `CacheFolder/helper` (script, copied `AMP.dll` + `Newtonsoft.Json.dll`, `helper.log`).
   - `CanDownload(SyncPlan plan)`: PC only (`ModSyncPaths.Platform == "windows"`), no `Unavailable`-blocking, every Add/Replace step has a host-side source (same platform) — mod.io sources also come from the host on PC in this phase (the host has the files). Returns false on Quest (phase 4).
   - `DownloadAndRestart(SwapMode mode)`: takes `ModSyncClient.Plan`/`HostSet`; collects `hostMod` of Add+Replace steps; calls `ModTransferClient.Download(...)` with progress → `IngameModUI.currentUI?.ShowModSyncProgress(text)`; on success: builds the journal with `JournalPlanner.Build(plan, HostSet.loadOrder, ModManager.guiManager.join_ip, int.Parse(join_port) (TryParse), ModManager.guiManager.join_password, mode)`, saves it, copies `AMP.dll` and `Newtonsoft.Json.dll` from AMP's own mod folder (`ModSyncPaths.ModsFolder` + AMP's folder from the inventory `isAmp` entry) into `HelperFolder`, writes the script, starts the launcher (`Process.Start(new ProcessStartInfo("powershell.exe", HelperScript.LauncherArguments(...)) { UseShellExecute = false, CreateNoWindow = true })`, action "apply", relaunch true, game pid = `Process.GetCurrentProcess().Id`), then `Application.Quit()`. On failure: show the error, re-enable the button (and enforce a 3 s cool-down before another try — the host ignores requests within 2 s).
   - `OnStartup()` (called from `ModManager.Initialize` after `ModSyncPaths.Init`): reads the journal if present.
     - `state == Applied && rejoinPending` → set `rejoinPending = false`, save, remember host/port/password; when the player is in a level (poll in a coroutine: `Player.currentCreature != null` and not loading, max 10 minutes), call `GUIManager.JoinServer(host, port.ToString(), password, false)` once and show a notice "Reconnecting to <host>…".
     - `state == Applied && mode == Temporary` (crash, or rejoin finished) → nothing now; restore on quit (below).
     - `state == Planned` (helper never ran) → delete the journal.
     - `state == RestorePending` (restore didn't finish) → show "Your mods couldn't be restored automatically. Book > Mods > AMP > Restore my mods." and offer the button.
     - Also read `helper/helper.log` last line; if it starts with "failed" or "error", show it once.
   - `OnQuit()` (hook `Application.quitting`, register in `ModManager.Initialize`): if journal `Applied` and mode `Temporary` → set state `RestorePending`, save, launch helper with action "restore", relaunch false.
   - `RestoreNow()` (button): journal `Applied` (any mode) → `RestorePending`, save, launch helper "restore" with relaunch true, `Application.Quit()`.
   - Never call `JournalApplier` in-game on PC (files are locked); Quest is phase 4.
2. **UI** (`IngameModUI.cs`, ModSync page):
   - When `ModSwapClient.CanDownload(plan)`: show a **Keep / Temporary** toggle button (cycles, default Temporary) and a **Download & restart** button; hide "Join anyway" stays as is. Text line under the plan: "Characters keep items only from mods that are installed — use a separate character for multiplayer."
   - `internal void ShowModSyncProgress(string text)` updates the ModSync text area while downloading and disables the buttons.
   - On the AMP page while connected (Disconnect page) and on the server list page: a **Restore my mods** button visible when a journal in state `Applied` exists. Keep the layout simple (one button at a free position; follow existing button code).
3. `ModSyncClient.Reset()` must not delete the journal. `ModManager.StopClientImmediatly` must not touch it.
4. Joining a different server while a Temporary swap is applied: allowed; the new host's mod check compares against the swapped set as usual.

Build, all suites, commit `Mod swap: download & restart, rejoin, restore`.

### Task 5: Verify, review, push

All suites + nettest; final review (superpowers:code-reviewer) with emphasis on: data safety (can any path lose a player's mods?), crash at every step, helper quoting with real paths (`C:\Program Files (x86)\Steam\steamapps\common\Blade & Sorcery\...` and OneDrive Documents paths), Steam relaunch, `Application.quitting` timing; fix; push; refresh `test-build` + guide section "Downloading mods (PC)".

In-game checks for the user (PC host + PC friend):
1. Friend has one mod fewer → Download & restart → game closes, reopens, reconnects, mod present.
2. Friend has an extra mod, Temporary → after quitting, the extra mod is enabled again and the downloaded mod is gone (back in `AMP_ModCache`).
3. Same with Keep → downloaded mod stays.
4. Kill the game (Task Manager) mid-session in Temporary → next start offers Restore my mods; it works.
