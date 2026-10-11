# Overnight test-and-harden pass (2026-10-11)

Branch fix/review-pass, from 42afde0 (v1.2.0). Final `run-e2e.ps1 -All`: **33/33 PASS** (4ccb46a; 18 scenarios before tonight). Two earlier full runs each had one test-side flake (NPC health tolerance, bot OnConnect), both fixed in the test code. Unit tests: ModSyncTests 564 pass, packettest 52 pass, AmpBot selftest pass.

## A. Real mod set (37 mods, copies)
- **Host** (`host-realmods`): 36 real mods + AMP, 1944 MB. First scan (no hash cache) **14.0 s**, in the background: longest frame in that window 42 ms, no freeze. The bot downloaded 35 mods (HJ_NexusModManager is a default personal mod): **1933.6 MB in 484.8 s = 3.99 MB/s** (the host's default 4 MB/s upload cap). Game working set 3.98 → 4.14 GB during the upload; managed heap flat (260-300 MB); handles flat (~15k). No AMP errors.
- **Reverse** (`join-realmods`): the game joined with nothing, downloaded all 36 (18 from mod.io with the host's exact files, 2 mod.io files differed and came from the host, 16 from the host: 684 MB in 15.5 s on loopback), swapped Temporary, restarted, rejoined with everything, restored on quit.
- Packet limits: the 37-mod list, manifests (up to 116 files) and reports are chunked; nothing near 64 KB. The 15-min check limit only covers the joiner's own scan (~14 s per 2 GB here).
- **Bug found:** over a 150 ms line the transfer runs at ~1.1 MB/s (ack window), so 2 GB takes ~30 min, and the host kicked joiners after a fixed 30-min setup limit. **Fixed** (0ea9bf9).
- **Open:** during mod.io downloads the main thread hitched 0.7-1.2 s per big mod (setup screen only; not in host transfers). Source not pinned down (likely the mod.io SDK).

## B. Soak
Soak 1 (59 min, fixture mods, bot rejoining every 10 min, a dagger and a PvP toggle every minute): 5 full rounds OK. Fake router, lease 120 s (`-amp-test-lease`): renewed every 60 s exactly (59 renewals per protocol logged), TCP+UDP deleted on quit. Working set 3.22 → 3.30 GB over 49 min (+1.6 MB/min, items accumulate), private +245 MB, handles ~13.3k flat, threads flat, managed heap 164 → 179 MB. Its script ended 3 min before the bot (timing), so it was rerun on the final build. **Soak 2: PASS**, 64.6 min hosting, 7 rejoin rounds all meeting their checks, **64 renewals per protocol** at 60 s, TCP+UDP deleted on quit (checked by the bot). Working set 3.24 → 3.31 GB in 52 min (+1.3 MB/min, 70 daggers spawned), handles 13.3k flat, threads flat, managed heap 179 → 184 MB, longest frame 25 ms. One harmless game-internal NullReferenceException per leave (HandleRagdoll despawn of a non-pooled avatar) remains.

## C. Real router
SnapAV AN-110-RT-2L1W, public address. TCP+UDP 26950 → this PC, "AMP Community", lease 120 s, renewed twice by PortMapper (every lease/2): after each renewal GetSpecificPortMappingEntry read this PC, the description and **120 s left** (renewed), then deleted and verified gone. Nothing else touched.

## D. Bad network and robustness
- AmpBot `--proxy-port`: 75 ms each way, 2% loss, 5 s stall every 45 s. `join-badnet` and `host-badnet` (49 MB download, restart, rejoin, avatars/items both ways): PASS, 1.10 MB/s.
- Drops at 30/60/95% and the host quitting at 50%: **bug** - the setup waited forever ("Downloading..."). **Fixed** (1579688): clear error, finished mods kept, joining again completes; Mods untouched.
- Game killed while swapped (PC): the next start said nothing and ran on the host's mods. **Fixed** (1579688): notice + Restore my mods (helper restores and relaunches); helper output now copied into Player.log.
- Fuzz (`host-fuzz`, 1224 malformed/oversized/out-of-order/duplicate packets and raw frames): no crash/hang, no writes outside Mods/AMP_ModCache. **Bug:** NaN positions were applied and relayed (26,000 Unity errors in 2 min). **Fixed** (0fa94ee).

## Gameplay audit (research/overnight-audit.md)
Each re-checked against code/decomp; E2E scenarios failed before and pass after:
imbue echo/energy (a1e4296, `host-imbue`), ~~damage reported once~~ (e45f950, **reverted** in ae48002 after review: it made a joiner's punches on host NPCs count for nothing; needs a 2-client test), level-change ghost entries (dbcf038, `host-level-change`), NPC health + shared catalog (c0ec3ea, `join-npc-health`), leaving player's items (dd1e235, `host-holder-leaves`), owner race lock (ce15c1f; `host-owner-race` passes, but also passed without the lock: race not reproduced), allocations/scans (90e69bc), packet hardening (708f195), QOL: notices, Go to, joiners next to host, pulled back instead of kicked (d0032e3). Not done: "resync world" (clears and respawns the player's own held items; too risky untested), SizeChangePacket (dead code left).

## Quest (research/quest-nomad.md)
e9f3cbf: Discord off on Android (it threw every FixedUpdate), host game version sent and a joiner warned on mismatch (never blocks), no System.Drawing reference, `[ModSupportedPlatforms(Windows|Android)]`, docs: confirmed Mods path, logs in files/Logs. Startup restore when quitting isn't signalled: covered by `join-quest-sim-killed`. ModOption hang risk noted in the checklist. Nothing verified on a headset.

## Docs
docs/TEST-WITH-FRIEND.md (morning checklist). Note: PC Player.log is in `StreamingAssets\Logs`, and every test run overwrites it.

## 1.2.1 (staged, NOT published)
COMMUNITY_VERSION 1.2.1; `tools/make-release.sh 1.2.1` staged dist/AMP-Community-1.2.1.zip (SHA256 d3e0ba08..., built from the tested code). Pushed to fork `community`. Publish: `tools/make-release.sh 1.2.1 --publish`.

## Safety
Every run: real Mods moved aside and restored (manifest verified), real saves unchanged (only steam_autocloud.vdf time exempt), real mod.io data unchanged, only own PIDs stopped, commit charge 18-37 GB. Final state: no unrestored aside folder, no AMP_ModCache in the install, doorstop enabled=false, no game/bot/helper process, real router mapping deleted and verified gone (05:54; tests never touch it). Real-mod copies removed from run folders (logs kept). Side effect: test runs rewrite the game's own StreamingAssets\Logs\Player.log.

## Review follow-up (same day, after an independent review said no-ship)
- Damage change reverted (ae48002), see above.
- Map changes off: MainMenu checked before the pull-back, the host's own client exempt (8d95d0b; unit tests + E2E host loads Arena).
- NaN check: typed checks in the movement/spawn packets instead of per-packet reflection (216176a).
- `spawnJoinersNearHost` defaults to false (208103a).
- Game version warning compares major.minor.patch only; unreadable formats say nothing (3027a95).
- Setup limit also waits while a joiner's mod.io download moves (keep-alive reports, f9b35ca; E2E host-setup-keepalive).
- Known, not addressed: review items M2, M3, M5.
