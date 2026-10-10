# Phase 4b (GitHub Releases + In-Game Updater) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Versioned releases of the fork on `github.com/rodthegod2/AMP-Community` (zip + SHA256SUMS + install guide), and AMP checks for a newer release at startup and can update itself (PC: through the existing journal + helper swap; installs from mod.io are left to mod.io).

**Architecture:** Pure: `ReleaseInfo` (parse the GitHub "latest release" JSON, compare versions, pick the asset, parse SHA256SUMS), `ReleasePackage` (extract a release zip into a staging folder safely — zip-slip protection — and locate the `MultiplayerMod` folder). Game glue: `ReleaseUpdater` (UnityWebRequest to the GitHub API with a User-Agent, download, verify, extract into the mod cache, build a one-op AMP journal, launch the helper, relaunch). Tooling: `tools/make-release.sh` (build, stage, zip, checksums, notes, `gh release create`).

## Conventions
As in phases 1–3 (repo/branch, C# 7.3, csproj entries, `build.sh`, ModSyncTests/packettest/policytest, one build at a time, no `dotnet build-server shutdown`, never write to the game folder or real saves, Edit tool, commit identity + trailer naming your model).

## Versioning
- Add to `Data/Defines.cs`: `public const string COMMUNITY_VERSION = "1.0.0";` (semver, the fork's own version; `MOD_VERSION` stays as the upstream protocol label).
- Release tags: `v<COMMUNITY_VERSION>` (e.g. `v1.0.0`). Release asset: `AMP-Community-<version>.zip` containing a top-level `MultiplayerMod/` folder + `HOW-TO-PLAY.txt`; asset `SHA256SUMS.txt` with lines `<sha256-hex>  <file name>`.
- The `MultiplayerMod/manifest.json` `ModVersion` is set to the community version by the release script.

---

### Task 1: ReleaseInfo (pure, TDD)

`ModSync/ReleaseInfo.cs`:
- `public class ReleaseAsset { public string name; public string url; public long size; }`
- `public class ReleaseInfo { public string tag; public string version; public List<ReleaseAsset> assets; public string notes; }`
- `public static ReleaseInfo Parse(string githubJson)` — reads `tag_name`, `body`, `assets[].name`, `assets[].browser_download_url`, `assets[].size`; `version` = tag without a leading `v`; returns null on invalid JSON or missing tag; ignores drafts/prereleases (`draft`/`prerelease` true → null).
- `public static int CompareVersions(string a, string b)` — numeric compare of up to 4 dot-separated parts (missing parts = 0); non-numeric parts → treat the whole version as 0.0.0 (never "newer").
- `public static ReleaseAsset ZipAsset(ReleaseInfo r)` — the asset whose name starts with `AMP-Community-` and ends with `.zip` (case-insensitive); null if none.
- `public static string ExpectedSha256(string sha256sums, string fileName)` — parses `hex  name` / `hex *name` lines; returns lowercase hex or null.
- Download URL safety: `public static bool IsTrustedUrl(string url)` — https and host `github.com`, `api.github.com` or `objects.githubusercontent.com` / `release-assets.githubusercontent.com` only.

Tests: parse a realistic sample (write one inline: tag `v1.2.0`, two assets), draft/prerelease → null, garbage → null; compare `1.2.0` vs `1.10.0` (< 0), `1.0` vs `1.0.0` (0), `1.0.1` vs `1.0.0` (> 0), `abc` vs `0.0.1` (< 0); ZipAsset picks the right one; checksum parsing both formats + missing; IsTrustedUrl accepts github hosts and rejects `http://github.com`, `https://github.com.evil.com`, `https://evil.com/github.com`.

Commit `Releases: parse GitHub releases and checksums`.

### Task 2: ReleasePackage (pure, TDD)

`ModSync/ReleasePackage.cs`:
- `public static string ExtractAmpFolder(string zipPath, string stagingRoot, long maxBytes)` — uses `System.IO.Compression.ZipArchive` (reference `System.IO.Compression` in both csproj files; the game ships `System.IO.Compression.dll`); every entry name goes through `TransferRules.NormalizeRelativePath` (reject the whole zip on any unsafe name — zip-slip), total uncompressed size ≤ maxBytes, entry count ≤ 10,000; extracts into a fresh `stagingRoot/<guid>`; returns the path of the folder that contains `AMP.dll` and `manifest.json` (must be exactly one); throws `InvalidDataException` with a clear message otherwise.

Tests (build zips in the test with `ZipArchive`): valid package → folder with AMP.dll; `../x.dll` entry → rejected; too large → rejected; no AMP.dll → rejected; two AMP folders → rejected.

Commit `Releases: safe release package extraction`.

### Task 3: Updater in game (PC)

`ModSync/ReleaseUpdater.cs` (main thread, coroutines on `ModManager.instance`):
1. Settings: `modSettings.checkForUpdates = true`, `modSettings.updateRepo = "rodthegod2/AMP-Community"` (owner/name, validated `^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$`).
2. Skip entirely when: AMP's folder contains `id.modio` (installed from mod.io → mod.io updates it), platform isn't windows (Quest: phase 4), a swap journal is active (`ModSwapClient.CanRestore`), or checkForUpdates is false.
3. `CheckRoutine()` at startup (after the existing startup routines, delay 10 s): GET `https://api.github.com/repos/<repo>/releases/latest` with headers `User-Agent: AMP-Community-Updater` and `Accept: application/vnd.github+json`, timeout 15 s. Parse; if `CompareVersions(info.version, Defines.COMMUNITY_VERSION) > 0` and a zip asset exists → `Available = info`; show a notice "AMP <version> is available: Book > Mods > AMP > Update AMP". Rate limit: at most one check per 6 hours (timestamp in `CacheFolder/update-check.json`).
4. `UpdateNow()` (button): download `SHA256SUMS.txt` and the zip (only `IsTrustedUrl` URLs; UnityWebRequest follows redirects to githubusercontent — check the final URL host too) into `CacheFolder/updates/`; verify SHA-256 (mismatch → delete + error); `ReleasePackage.ExtractAmpFolder` into the cache's partial folder; compute its folder hash with `ModInventory`'s hashing and commit into `ModCache` (so it's an ordinary cache entry); build a journal with `JournalPlanner` for a single AMP Replace step (`hostMod` = the extracted entry incl. its hash + folder name, `localMod` = our AMP entry, `keep` ops), `rejoinPending = false`; launch the helper exactly like `ModSwapClient` does (reuse its launch + verified-start + quit logic — refactor into a shared method if needed rather than copying), relaunch = true.
5. UI: an **Update AMP (vX)** button on the AMP server list page when `Available != null` (and not connected); progress text while downloading; errors shown on the page.

Build, all suites, commit `Releases: in-game update check and update`.

### Task 4: Release tooling + first release

`tools/make-release.sh <version>` (bash, runs from the repo root):
1. Refuse if the working tree is dirty or `Defines.COMMUNITY_VERSION` != `<version>`.
2. `bash /c/Projects/bns-mp/build.sh` (exit 0).
3. Stage `dist/MultiplayerMod/` = `BS Files/manifest.json` (with `ModVersion` set to the version and `Description` "Community fork of AMP by Adammantium — stability fixes, host on PC, mod sync."), `AMP.dll`, `AMP.pdb`, `Netamite*.dll`, `Newtonsoft.Json.dll` from `C:/Projects/bns-mp/build/`; `dist/HOW-TO-PLAY.txt` from `docs/HOW-TO-PLAY.txt` (move the guide into the repo: copy `C:\Projects\bns-mp\test-build\HOW-TO-PLAY.txt` to `docs/HOW-TO-PLAY.txt`, update its intro: download from GitHub releases; add a "Quest players" section placeholder line "Quest support is coming in a later version.").
4. Zip to `dist/AMP-Community-<version>.zip` (PowerShell `Compress-Archive`), write `dist/SHA256SUMS.txt` (`sha256sum`), write `dist/RELEASE_NOTES.md` (what's new, install steps, credits: AMP by Adammantium (Apache-2.0), Netamite by Adammantium (included modified; credited, no license file published upstream), upstream contributors).
5. `gh release create v<version> dist/AMP-Community-<version>.zip dist/SHA256SUMS.txt --repo rodthegod2/AMP-Community --title "AMP Community <version>" --notes-file dist/RELEASE_NOTES.md --target community` — only when called with `--publish`; without it, only stage and print what would be uploaded.
6. `dist/` is git-ignored.

Then: set `COMMUNITY_VERSION = "1.0.0"`, commit, push, run `tools/make-release.sh 1.0.0` (dry run), inspect, run with `--publish` (approved by the owner), verify with `gh release view v1.0.0 --repo rodthegod2/AMP-Community` and download the asset back + verify the checksum.

### Task 5: Review + push
Opus review (security of the updater: URL trust, redirects, checksum, zip-slip, what if GitHub is down / rate-limited, never updating while a swap is active, never touching mod.io installs), fix, push.
