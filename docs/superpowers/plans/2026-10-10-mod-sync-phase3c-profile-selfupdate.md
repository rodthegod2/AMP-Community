# Mod Sync Phase 3c (Multiplayer Save Profile + AMP Self-Update on PC) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** (1) While a mod swap is active, the game uses a separate save folder `Saves/AMP_Multiplayer` (characters + Options.opt), seeded once from `Saves/Default`, so the player's own characters never lose modded items and their own Mod Options are never cleaned. (2) If a PC joiner runs a different AMP build than the host, AMP's folder is downloaded and swapped like a mod (always kept).

**Architecture:** Pure: `ProfileSeeder` (seed the MP folder once), planner/journal/applier support for an AMP replace that is never undone. Game glue: `ProfileSwitch` (switch `PlatformWindows.savePartialPath` by reflection and reload Options, at AMP startup, before settings sync's startup snapshot), host serves its AMP folder, UI notice.

**Facts (verified by decompiling, see `C:\Projects\bns-mp\decomp\ThunderRoad_full\ThunderRoad`):**
- `PlatformWindows` has private string fields `rootPath` (Documents), `altPath` (persistentDataPath), `savePartialPath` (`Path.Combine("My Games", Application.productName, "Saves", "Default")`). `TryGetSavePath` returns `Path.Combine(rootPath, savePartialPath)` (or `altPath` fallback) — not cached. Every `.chr` and `Options.opt` read/write goes through `GameManager.platform.TryGetSavePath`.
- Startup order: `Options.LoadCoroutine` (before mods) → mod ThunderScripts (`ScriptLoaded` → AMP `ModManager.Initialize`, synchronous up to its first coroutine yield) → `Catalog.RefreshCoroutine` → `Options.ApplyModOptions` (runs `CleanModOptions`) → `PlayerSaveData.LoadCharacterSaves`. So switching the path and re-running `Options.LoadCoroutine().AsSynchronous()` inside `Initialize` covers both characters and mod options.
- AMP's `config.json` path is computed from `TryGetSavePath` in `Initialize` **before** the switch, and `SafeFile` keeps its `filePath`, so AMP's config stays in `Saves/Default`. The switch must happen after the config is loaded and after `ModSyncPaths.Init`, and before `SettingsSyncClient.StartupRoutine` starts.
- Android (`PlatformAndroid`) is phase 4; on Android the switch is skipped.

## Conventions

As before (repo/branch, C# 7.3, csproj entries, `bash /c/Projects/bns-mp/build.sh`, ModSyncTests incl. the real PowerShell tests, packettest `OK, 52 packets`, policytest 15; one build at a time; never `dotnet build-server shutdown`; never write to the game folder or real save folders; Edit tool; commit identity + trailer naming your model).

---

### Task 1: ProfileSeeder (pure, TDD)

`ModSync/ProfileSeeder.cs`:

```csharp
using System;
using System.IO;

namespace AMP.ModSync {
    // The multiplayer save folder starts as a copy of the player's normal save (settings + characters), once.
    // Characters loaded there may lose items from mods that aren't installed - only the copies, never the originals.
    public static class ProfileSeeder {
        public const string ProfileFolderName = "AMP_Multiplayer";

        // Returns true if the folder exists afterwards. Never overwrites anything in an existing profile.
        public static bool EnsureSeeded(string defaultSaveFolder, string profileFolder) {
            try {
                if(Directory.Exists(profileFolder)) return true;
                string staging = profileFolder + ".seeding";
                if(Directory.Exists(staging)) Directory.Delete(staging, true);
                Directory.CreateDirectory(staging);
                if(Directory.Exists(defaultSaveFolder)) {
                    string options = Path.Combine(defaultSaveFolder, "Options.opt");
                    if(File.Exists(options)) File.Copy(options, Path.Combine(staging, "Options.opt"));
                    foreach(string character in Directory.GetFiles(defaultSaveFolder, "*.chr")) {
                        File.Copy(character, Path.Combine(staging, Path.GetFileName(character)));
                    }
                }
                Directory.Move(staging, profileFolder); // appears complete or not at all
                return true;
            } catch(IOException) {
                return Directory.Exists(profileFolder);
            } catch(UnauthorizedAccessException) {
                return Directory.Exists(profileFolder);
            }
        }
    }
}
```

Tests (`ProfileTests();`): seeds Options.opt and both `.chr` files from a fake Default folder; doesn't copy other files (e.g. `steam_autocloud.vdf`); a second call with a changed Default doesn't touch the existing profile; missing Default folder still creates an empty profile; a leftover `.seeding` folder is replaced. Add to both csproj files, build, commit `Profile: seed the multiplayer save folder once`.

---

### Task 2: AMP self-update in planner / journal / applier (pure, TDD)

1. `SyncPlanner.Plan`: when `samePlatform` and the host has an enabled `isAmp` entry with a hash and `plan.ampDiffers` is true, add `new SyncStep { action = SyncAction.Replace, hostMod = hostAmp, localMod = localAmp (may be null), source = "host" }` (if the joiner has no enabled AMP entry — impossible in practice — use `SyncAction.Add`). Cross-platform: unchanged (no step; phase 4). Keep everything else as is (AMP still never gets Disable). Update the existing test "AMP is never added, replaced or disabled" → it now expects exactly one Replace for AMP on the same platform and none across platforms.
2. `SwapOp` gets `public bool keep;` ("never undone, even by a Temporary restore"). `JournalPlanner.Build` sets `keep = true` on the `MoveAside` and `Install` ops whose folder belongs to an AMP step (`step.hostMod.isAmp`). The AMP `MoveAside` uses the **joiner's** AMP folder name (`localMod.folder`); the `Install` uses the host's folder name. If the names differ, both ops still apply (old one goes to `AMP_ModCache/old/<id>/`).
3. `JournalApplier`: `Undo` skips ops with `keep == true` in every mode (they are only rolled back by a failed **Apply**'s rollback — keep that). `Precheck` unchanged.
4. Tests: planner adds the AMP replace on same platform only; journal marks AMP ops `keep`; Temporary restore leaves the new AMP installed and the old one in `old/<id>/`; a failed Apply still rolls the AMP ops back.

Commit `Mod swap: AMP updates itself from the host (PC)`.

---

### Task 3: Game glue

1. **Host serves its AMP folder:** in `ModTransferHost` (request handling), stop excluding `isAmp` entries **only** when the request is for the host's AMP folder and the requesting joiner's report said it's on the same platform (`windows`). Keep all other limits. (Look at how the joiner's platform is known on the host — `JoinerReport.platform` is received in `ModSyncHost.OnPacket`; store it on `ClientData` (e.g. `modSyncPlatform`) if not already stored.)
2. **Joiner downloads it:** `ModSwapClient.CanDownload` already accepts Replace steps; make sure the AMP step's `hostMod` is included in the download list and nothing filters `isAmp` out on the joiner side (check `ModTransferClient`). The UI text shows "AMP update (host's build)" for that step (it already lists Replace steps by name).
3. **`ModSync/ProfileSwitch.cs`** (main thread):
   - `internal static bool Active { get; private set; }`
   - `internal static void ApplyAtStartup()`: if `ModSyncPaths.Platform != "windows"` → return. Read the journal via the same loader `ModSwapClient` uses; active when the journal state is `Applied` or `RestorePending`. If not active → return.
   - Get `GameManager.platform`; if it isn't `PlatformWindows` → log and return. Read private fields `rootPath`, `altPath`, `savePartialPath` with reflection (`BindingFlags.NonPublic | BindingFlags.Instance`); if any is missing → log "profile switch unavailable" and return (the swap still works; the player was warned to use a separate character).
   - `defaultFolder = TryGetSavePath(...)` (before changing anything); `profilePartial = Path.Combine(Path.GetDirectoryName(savePartialPath), ProfileSeeder.ProfileFolderName)`; profile folder = `Path.Combine(<the same root that defaultFolder used>, profilePartial)` (compute by replacing the last path segment of `defaultFolder`).
   - `ProfileSeeder.EnsureSeeded(defaultFolder, profileFolder)`; if false → log and return.
   - Set `savePartialPath = profilePartial` via reflection; verify `TryGetSavePath` now returns the profile folder (else restore the old value and return).
   - Reload options: `Options.LoadCoroutine().AsSynchronous();` (ThunderRoad extension; verify the name in the decompiled code). On exception: restore `savePartialPath`, reload options again, log, return.
   - `Active = true`; log "Multiplayer profile active: <folder>".
   - Call it in `ModManager.Initialize` right after `ModSyncPaths.Init()` / `ModSwapClient.OnStartup()` and **before** `StartCoroutine(SettingsSyncClient.StartupRoutine())` (check the current order in `Initialize` and place it so the settings snapshot reads the profile's options).
4. **UI:** on the AMP pages, when `ProfileSwitch.Active`, show one line "Multiplayer profile (separate characters) - your own saves are untouched." (reuse an existing info text field, e.g. the server info message area, or the mod sync text). Remove the "use a separate character" hint from the Download & restart page and replace it with "Your characters are kept safe: multiplayer uses a copy of your saves."
5. No change needed when the swap is restored: next start has no active journal → Default profile.

Build, all suites, commit `Profile: separate multiplayer saves while a mod swap is active`.

### Task 4: Verify, review, push

All suites + nettest; Opus review (focus: can the player's real saves ever be modified while Active? reflection failure modes; ordering vs. `Options.ApplyModOptions` and settings sync snapshot; AMP self-update chicken-and-egg: old AMP running the new protocol? Does an AMP update ever get rolled back by Temporary restore?); fix; push; update test build + guide.

In-game checks for the user: after Download & restart the character select shows copies of your characters; a modded item from a turned-off mod is missing only there; after quitting (Temporary) your normal characters still have it. Joiner with an older AMP build gets it replaced and reconnects.
