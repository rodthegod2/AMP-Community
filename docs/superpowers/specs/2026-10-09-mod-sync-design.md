# Host-enforced mod sync — design

Date: 2026-10-09
Status: draft for review
Scope: AMP fork (`fix/review-pass`), Blade & Sorcery 1.x, PCVR (Steam) host, PCVR and Nomad (Quest) joiners.

## Goal

When a player joins, they end up running exactly the host's mod set, with the host's gameplay settings, and then play.
The joiner chooses whether downloaded mods stay installed ("Keep") or only apply to this host ("Temporary").
Their own mod setup and characters come back untouched afterwards.

## Decisions (from the brainstorm)

| Topic | Decision |
|---|---|
| Who joins | Friends the host trusts. Files may come straight from the host's PC, with a confirmation on the joiner's side. |
| Temporary vs keep | Joiner picks, per host. |
| Joiner's extra mods | Switched off for the session (exact match), except mods on the host's "personal mods" list. |
| Different version of the same mod | Counts as a mismatch; the host's version is used. |
| Restart | PC: automatic (game closes, relaunches through Steam, reconnects). Quest: player restarts, AMP reconnects. |
| Crossplay | PC host; PC and Quest joiners. mod.io is used for anything a Quest player needs. |
| Quest can't get a mod | Quest player joins anyway, with a warning listing what's missing. |
| Nexus-only mods for Quest | Auto-matched to a mod.io mod with a Nomad build (name + author); unsure matches are confirmed once by the host and remembered. |
| Settings | Server-side mod options and AMP host settings sync live from host to everyone; joiners' menus update live and can't change them. |
| Also in v1 | Personal mods list, mod settings sync, AMP updates itself, temporary-mod cache. |
| Hosting | Server runs inside the host's game ("Host on PC"). Dedicated server is a later phase. |

## Facts this design relies on (researched, see "Verified" column)

| Fact | Verified |
|---|---|
| Mods load only at game start; DLLs are locked while the game runs on Windows. | yes (ModManager, Windows file locks) |
| A mod folder starting with `_` is skipped (`ModManager.IsEnabledFolder`). | yes |
| Load order comes from `<Mods>/loadorder.json` `{"modNames":[...]}`; unlisted folders load first. | yes |
| Mods folder: PC `StreamingAssets/Mods`, Quest `persistentDataPath/Mods`. | yes |
| Quest loads mod DLLs through HybridCLR when the store allows scripted mods; no Harmony patching of AOT code. | yes (code path), AMP itself on Quest: **no** |
| A character that references items/spells/skills from a missing mod loses them silently, and the pruned save is written back. | yes (`SavedInventoryData.Check*`) |
| On every boot, saved mod options of mods that aren't loaded are deleted (`Options.CleanModOptions`). | yes |
| The in-game mod.io browser deletes any Mods folder whose `id.modio` id the user isn't subscribed to (`RefreshLooseFileHandler`). | yes |
| mod.io: game id 3852; `ModIOUnity.GetMod/GetMods/GetModDependencies` work without a user; `DownloadModToStreamOperation` downloads without a user; `ModProfile.platforms` lists which platforms have a live file. | yes (decompiled SDK) |
| Another mod can initialise and call the game's mod.io SDK. | inferred, **phase 0 check** |
| Mod options: `ModOption.Apply(index)` sets a value and fires `ValueChanged`; UI refresh is `LoadValue(false)` + `RefreshUI()`; options are saved in `<save dir>/Options.opt`. | yes |
| The save directory can be redirected before characters load (AMP loads before `LoadCharacterSaves`). | inferred, **phase 0 check** |

## Overview

```
 Host PC (Host on PC)                                Joiner (PC or Quest)
 ───────────────────                                 ────────────────────
 1. connect ───────────────────────────────────────► (normal Netamite connect)
 2. ModSet (mods, versions, mod.io ids,
    hashes, personal list, load order) ────────────► 3. compute plan
                                                        - nothing to do → 6
                                                        - else show plan, ask Keep/Temporary
                                         ◄──────────── 4. fetch: mod.io (own platform build)
    host transfer (PC joiners, chunked) ───────────►       or host transfer (PC only)
                                                     5. stage in cache → apply → restart → reconnect
 6. greet + settings snapshot ─────────────────────► apply host settings, lock them, play
 7. host changes a setting ────────────────────────► update value + menu live
                                                     8. leave/quit (Temporary): restore own mods + profile
```

The host holds back the normal greeting (player spawn, items, creatures) until the joiner reports "mods OK".

## Components

Each unit has one job and can be tested without the game unless marked "game".

### 1. ModInventory (both sides)

Scans the local Mods folder into a list of `ModEntry`:

- `folder` — folder name (the game's mod key).
- `name`, `author`, `version` — from `manifest.json`.
- `modioId` — from `id.modio` if present.
- `hash` — SHA-256 over sorted relative paths + file contents (PC only; cached by path/size/mtime so rescans are fast).
- `hasCode` — contains any `.dll`.
- `sizeBytes`.
- `enabled` — folder not prefixed with `_`.

Also reads `loadorder.json`. Pure file logic, headless-testable on a fake Mods folder.

### 2. ModSet handshake (packets)

New packets (ids appended after existing ones; the handshake packets' fields are append-only so future builds stay compatible):

- `MOD_SET` (host → joiner): protocol version, host platform, AMP build hash, list of `ModEntry` (enabled mods only), personal-mods list, load order, server-side settings snapshot. Sent in parts if larger than one packet.
- `MOD_SYNC_STATUS` (joiner → host): `ready` | `needs_setup` | `declined`, plus joiner platform.
- `MOD_FILE_REQUEST` / `MOD_FILE_CHUNK` / `MOD_FILE_DONE` — host transfer (component 5).
- `MOD_SETTING` (host → all): one changed setting.
- `HOST_SETTINGS` (host → all): AMP host settings (PvP, multipliers, map change, voice, NPC authority, …).

Server change: `GreetPlayer` waits for `ready` (or for a Quest joiner's `ready` with warnings). A joiner that never answers within 60 s, or runs a build without this protocol, is kicked with "Update AMP: the host requires mod sync".

The existing whitelist/blacklist/requirelist checks keep working and run after `ready`.

### 3. SyncPlanner (joiner, pure logic)

Input: host `MOD_SET`, local `ModInventory`, joiner platform, saved Nexus→mod.io matches from the host. Output: a plan of actions:

| Action | When |
|---|---|
| `Add` | host has it, joiner doesn't |
| `Replace` | both have it, hash differs (PC↔PC) or version differs (cross-platform) |
| `Disable` | joiner has it enabled, host doesn't, not on the personal list, not AMP |
| `Unavailable` | Quest joiner, host mod has no mod.io source with an Android/Oculus file |
| `LoadOrder` | host's load order differs |

Each `Add`/`Replace` has a source: `modio(id)` or `host`. AMP itself is handled by component 9.
Unit-tested headlessly with table-driven cases (like `policytest`).

### 4. Nexus → mod.io matcher (host)

For each host mod without `modioId`, when a Quest player is involved (or ahead of time from a host button):

1. `ModIOUnity.GetMods` with `AddSearchPhrase(name)`, page size 20.
2. Score candidates: exact name (case/space/punctuation-insensitive) + exact author (`creator.username`) → **auto match**; name match but different/unknown author, or name contains host name ("Jujutsu Sorcerers (Nomad)") → **needs confirmation**; else no match.
3. Candidate must have an Android or Oculus entry in `ModProfile.platforms` with a live file.
4. Results stored in host config `hostingSettings.modMatches`: `{ "<folder>": { "modioId": 123, "confirmed": true } }` or `{ "modioId": 0 }` for "no Quest version".
5. Confirmation UI in the host's book: "Use '<candidate>' by <author> for Quest players?" Yes / No.

Scoring is pure logic and unit-tested; the SDK call is "game".

### 5. Delivery

**5a. mod.io (any platform).** Joiner downloads its own platform's file with `DownloadModToStreamOperation` (no login needed), verifies the SDK's MD5, extracts into the cache (component 6). Dependencies come from `GetModDependencies` and are added to the plan. We never write `id.modio` for temporary mods (the game's mod browser would delete them). For **Keep** with a logged-in mod.io user, the mod is subscribed instead, so the game's browser manages and updates it.

**5b. Host transfer (PC joiner, PC host only).** The mod folder is sent as a stream of files over the existing connection:

- `MOD_FILE_CHUNK` carries (mod, relative path, offset, ≤ 32 KB data) on the reliable channel.
- Host limits each transfer (default 4 MB/s per joiner, `hostingSettings.modUploadKBps`) so players already in the game aren't starved, and refuses anything over `hostingSettings.maxModTransferMB` (default 4096).
- Joiner writes only inside its cache folder: relative paths are normalised; anything absolute, containing `..`, or resolving outside the target is rejected and the transfer aborted.
- After `MOD_FILE_DONE`, the joiner recomputes the folder hash and compares it with the host's `ModEntry.hash`; mismatch → delete and fail.
- Joiner confirmation before any download: list of mods, size, and "contains code" marker; "Trust this host" remembers the host address so friends aren't asked every time.

### 6. Mod cache

`AMP_ModCache/` next to the Mods folder (PC: `StreamingAssets/AMP_ModCache`, Quest: `persistentDataPath/AMP_ModCache`) — outside Mods so the game never loads it.

- Entries keyed by `modioId@fileId` or `hash`.
- Size cap `modSettings.modCacheMaxMB` (default 8192); least-recently-used entries are evicted, never ones in the active session.
- Rejoining the same host reuses cached entries: no download, just apply + restart.

### 7. Apply, restart, restore (the "swap")

All folder changes are recorded first in a **journal** (`AMP_ModCache/session.json`): every rename/move with its reverse, plus the session info (host address, port, platform, Keep/Temporary, profile). The journal makes every step undoable and crash-safe.

Operations:

- `Disable` → rename `X` to `_AMPoff_X` (the `_` prefix makes the game skip it).
- `Add` → move cache entry into `Mods/<folder>`.
- `Replace` → rename the joiner's copy to `_AMPold_X`, then `Add`.
- `LoadOrder` → back up `loadorder.json` to the cache, write the host's.

**PC** (files locked while running): AMP writes the journal, writes a small helper script to the cache, starts it detached, and quits the game. The helper waits for the game process to exit, applies the journal, then launches `steam://rungameid/629730`. Restore (Temporary) runs the same helper in reverse when the player quits the game while a temporary session is active.

**Quest** (no file locks): AMP applies the journal in-game, shows "Restart Blade & Sorcery to finish" and quits. Restore happens in-game on quit (`Application.quitting`) or from the "Restore my mods" button.

**On launch with a journal present:** if the session is pending, AMP switches to the multiplayer profile (component 8), and once the player is in any level it reconnects to the saved host automatically. If the journal shows a session that ended without restore (crash), AMP offers "Restore my mods" in the book and on the desktop overlay.

**Keep** sessions: added mods stay. Disabled extras and `_AMPold_` copies are still restored on quit, because they're the joiner's own setup. Replaced mods stay on the host's version.

### 8. Multiplayer profile (save protection)

Whenever the joiner's mod set is changed by the sync, the game uses a separate save folder for that session: `Saves/AMP_Multiplayer` instead of `Saves/Default` (characters `*.chr` and `Options.opt`). This prevents both data-loss hazards: the solo character never loads without its mods, and solo mod options are never cleaned. The redirect is set by AMP at startup before characters load (phase 0 confirms the hook: `GameManager.platform` save path override). First use: the player creates a multiplayer character.

If the joiner already matches the host exactly, nothing restarts and the normal profile is used; host settings are then applied in memory and the joiner's own values restored on leave (component 10).

### 9. AMP updates itself

AMP's own folder is a special `ModEntry` (`isAmp`). Host and joiner compare the AMP build hash in `MOD_SET`.

- PC joiner, different build → AMP folder comes through host transfer and is swapped by the helper like any other mod (always Keep).
- Quest joiner → same, **if** phase 0 shows the same AMP build runs on both platforms (the official AMP Crossplay ships one file for Windows and Android, which suggests it does). Otherwise Quest joiners get "Update AMP" with instructions.
- Bootstrap: players on a build without this feature need one manual install of this version. The `MOD_SET`/`MOD_SYNC_STATUS` format is kept stable so later builds can always update earlier ones.

### 10. Settings sync

**What is synced.** All mod options of mods in the synced set, except "personal" ones. Default personal options: name or category contains volume, sound, audio, music, voice, UI, menu, HUD, display, graphic, haptic, vibration, control, key, hand pose, subtitle, language. The host can override per option in `hostingSettings.settingOverrides` (`"<mod>/<option>": "server" | "personal"`). Options of personal mods are never synced. Plus AMP's host settings (`HOST_SETTINGS`).

**Host.** Subscribes to `ValueChanged` on every server-side `ModOption`. On change, sends `MOD_SETTING{mod, option, index}`. A guard flag ignores changes AMP itself makes. Full snapshot goes out in `MOD_SET` and on reconnect.

**Joiner.**

- Applies with `ModOption.Apply(index)`, then `(uiComponent as UIList)?.LoadValue(false)` and `RefreshUI()` so an open menu shows the new value immediately.
- Locking: on `ValueChanged` from the player, AMP re-applies the host's index (guarded) and shows "Set by the host"; it also calls `SetInteractable(false)` after each refresh where the UI supports it.
- On join (normal profile) AMP records the joiner's own indices; on leave it restores them, so host values aren't saved into the solo `Options.opt`.
- AMP host settings show on the joiner's AMP page as read-only values and update live.

### 11. Release updater and distribution

Releases are published on GitHub (`rodthegod2/AMP-Community`), one zip per version containing the `MultiplayerMod` folder (same file for PC and Quest if phase 0 confirms one build runs on both; otherwise one asset per platform, named `-pc` / `-nomad`).

- On game start (and from a "Check for updates" button), AMP queries `https://api.github.com/repos/rodthegod2/AMP-Community/releases/latest` (no login), compares the release tag with its own build version, and offers "Update to vX".
- Update = download the asset into the cache, verify its SHA-256 against the checksum file attached to the release, then swap the AMP folder exactly like a mod swap (component 7): helper + relaunch on PC, in-game swap + manual restart on Quest. The previous version is kept in the cache for rollback.
- **Installed from mod.io** (an `id.modio` file in AMP's folder): the GitHub updater stays off; mod.io's own updates apply. This keeps mod.io copies within mod.io's review process.
- Order of precedence when joining: host self-update (component 9) wins over GitHub, so a session always runs the host's build.

First install:

| Player | How |
|---|---|
| PC | Download the release zip, copy `MultiplayerMod` into `StreamingAssets/Mods` (guide in the release notes). |
| Quest + a PC | USB copy into the Quest's B&S `Mods` folder (guide), once. |
| Quest, no PC | mod.io listing (optional, uploaded by the owner). Possible one-time wait if Blade & Sorcery's Android platform is moderated on mod.io. |

Netamite has no license. Decision (owner, 2026-10-09): releases ship our modified Netamite DLLs; the Netamite author is credited in the release notes and asked/added once reachable.

## Personal mods list

`hostingSettings.personalMods`: folder names (case-insensitive) that joiners may keep, lack, or have in any version. Default: AMP itself and `HJ_NexusModManager`. The host's book shows a "Personal" toggle per mod. Personal mods are left alone by the planner (no Add, no Disable, no Replace) and their settings aren't synced.

## UI (book → Mods → AMP)

Joiner, new **Mod sync** page shown when the plan isn't empty:

- Summary: "Host uses 12 mods. You need: 3 to download (840 MB), 2 to turn off, 1 replace. 1 not available on Quest."
- List with action icons, size, "contains code" marker.
- Keep / Temporary choice, "Trust this host" toggle.
- Buttons: **Download & restart** (PC) / **Download** then **Restart** (Quest), **Cancel** (disconnect).
- Progress bar per mod and total.

Joiner, AMP page while connected: "Restore my mods" when a temporary session is active; read-only host settings.

Host, **Mods** page: list of the host's mods with Personal toggles, Quest-match status (matched / needs confirmation / no Quest version) with confirm buttons, and per-joiner transfer progress.

## Error handling

| Situation | Behaviour |
|---|---|
| Joiner cancels the plan | Disconnect, nothing changed on disk. |
| Download fails / hash mismatch | Retry once; then show which mod failed; nothing applied. |
| Not enough disk space | Checked before downloading (plan size + 10%); clear message. |
| Game crashes mid-session | Journal remains; next launch offers restore. |
| Helper can't move a file (still locked) | Helper retries for 30 s, then restores everything it changed and relaunches; AMP shows the error. |
| Host disconnects during download | Partial cache entries are deleted; joiner returns to the server list. |
| mod.io unreachable | Quest joiner: mods it needs become `Unavailable` (joins with warning). PC joiner: host transfer is used instead where possible. |
| Joiner on old AMP without mod sync | Kicked with "Update AMP" message. |

## Testing

Headless (part of the existing test set, run after every change):

- `ModInventory` on fake Mods folders (manifest/no manifest, disabled `_` folders, id.modio, hash stability).
- `SyncPlanner` table tests (PC↔PC, PC↔Quest, personal mods, versions, load order, AMP entry).
- Matcher scoring tests (exact, Nomad-suffixed names, wrong author, no Android file).
- Journal apply + rollback on a temp folder, including interrupted-apply recovery.
- Host transfer over a real Netamite connection (`nettest` style): chunks, rate limit, hash check, path-traversal rejection.
- Helper script against a fake Mods folder and a dummy process.
- New packets are covered by `packettest` automatically.

In game (needs the user):

- Phase 0 checks (below).
- PC host + PC joiner: add / disable / replace / Temporary restore / Keep.
- PC host + Quest joiner: mod.io download, unavailable warning, restart + reconnect.
- Live settings change visible on joiner with menu open; joiner can't change it.

## Phases

0. **Feasibility (needs one Quest session).** (a) Package the fork for Nomad and confirm it loads and joins a PC host. (b) From inside the game: initialise the mod.io SDK, `GetMod`, search, `DownloadModToStreamOperation` to a temp folder. (c) Redirect the save folder at startup and confirm the game reads characters from it. Each result either confirms the design or changes components 5a, 8 or 9 before they're built.
1. **Handshake + planner + UI (no downloads).** Inventory, `MOD_SET`, planner, personal list, plan page, greeting gate, "Update AMP" kick.
2. **Settings sync.** Mod options + AMP host settings, live UI, locking, restore on leave. Independent of downloads, so it can ship early.
3. **PC↔PC delivery.** Host transfer, cache, journal, helper restart/rejoin, multiplayer profile, Temporary restore, AMP self-update on PC.
4. **Quest.** mod.io delivery, Nexus→mod.io matcher with host confirmation, Quest apply/restart path, AMP self-update on Quest if phase 0 allows.
4b. **Release updater + publishing.** GitHub release pipeline (zip + checksum), in-game updater, install guides.
5. **Later (not in v1).** Dedicated server with a mod folder; Quest hosting.

## Out of scope for v1

- Joining strangers / public servers with forced downloads (security model is "friends only").
- Quest players hosting.
- Syncing mods' own save data or per-mod config files other than ModOptions.
- Automatic Nexus downloads.
