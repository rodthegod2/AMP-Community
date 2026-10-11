# Testing with a friend - morning checklist

Everyone needs the **same AMP Community version** (github.com/rodthegod2/AMP-Community/releases) and the **same Blade & Sorcery version** (Player.log line `Game version:`; AMP warns on join if they differ). Use a spare character.

## Host (you, PC)
1. Install or update AMP: `MultiplayerMod` into `...\Blade & Sorcery\BladeAndSorcery_Data\StreamingAssets\Mods\` (or Book > Mods > AMP > "Update AMP").
2. Start the game, load into a level (Home is fine).
3. Book > Mods > AMP > Host tab > **Host on PC** (desktop: H > "Host Server ->").
4. The router line on the AMP page should say **"Port 26950 was opened on your router automatically."** Send your friend the **Internet code** (like `K7QX-29MB`). If Windows asks about the firewall, click Allow.
5. Stay in the level while your friend joins. If they need your mods (your full set is about 2 GB) they download them from you: expect 10-30 minutes over the internet. The host no longer kicks them while the download is still moving.

## PC friend
1. Same AMP version, same folder. Remove any other AMP.
2. Load into a level, Book > Mods > AMP > Join > **Join Code / IP**, type the code, press ">".
3. Mods differ: choose **Temporary**, press **Download & restart**. The game closes, reopens through Steam and reconnects by itself. If the connection drops mid-download, it now says so: join again, finished mods are kept.
4. Quit normally at the end: their own mods come back. (After a crash, the next start says the host's mods are still there: Book > Mods > AMP > Restore my mods.)

## Quest friend (EXPERIMENTAL: AMP has never run on a headset)
1. First time: in the game, Book > Mods > search **"Rod's Multiplayer Mod"** > Subscribe, restart the game ([mod.io page](https://mod.io/g/blade-and-sorcery/m/rods-multiplayer-mod)). Or by hand: start Nomad once, quit. USB **data** cable to a PC, accept "Allow access to data", copy `MultiplayerMod` into `Quest\Internal shared storage\Android\data\com.Warpfrog.BladeAndSorcery\files\Mods\` (confirmed path; folder directly inside `Mods`). No PC: a headset file manager (Mobile VR Station) - untested. The in-game Mods button won't show it (mod.io only). Don't also install the official "AMP [Crossplay]".
2. Load into a level, Book > Mods > AMP > Join > Join Code / IP, type the **Internet code**.
3. Mods differ: they come from mod.io (Quest builds). Download & restart, then **start the game again by hand**: it reconnects. Mods with no Quest version are skipped.
4. **Unverified / known risks:** the whole Quest flow; the official AMP's Nomad build was reported to hang at **"Loading Mods: AMP - ModOption"** (if ours does too, note how long and send the log); no voice chat on Quest; whether Nomad is on 1.3.1 like PC.

## If something fails
- **Can't connect:** read the host's router line. "No router answered" = turn on UPnP; "shares your IP" = CGNAT, use Tailscale; "refused" = forward 26950 TCP+UDP by hand. Same house: use the **Same Wi-Fi code** (guest Wi-Fi can block it).
- **Typo message:** codes never contain 0, O, 1 or I.
- **Mods look wrong afterwards:** Book > Mods > AMP > **Restore my mods**.

## Logs to send (copy them right after the problem, before starting the game again)
- PC: `...\Blade & Sorcery\BladeAndSorcery_Data\StreamingAssets\Logs\Player.log` and `Player-prev.log` (a mod swap restarts the game, so the earlier part is in Player-prev.log; the swap helper's report is copied into Player.log at the next start).
- Quest: `Android\data\com.Warpfrog.BladeAndSorcery\files\Logs\Player.log` and `Player-prev.log` (same USB cable).
- Host config if settings matter: `Documents\My Games\BladeAndSorcery\Saves\Default\Mods\MultiplayerMod\config.json`.

## Send back to Claude
What you did and what happened (with times), PC or Quest friend, both AMP versions (AMP page) and game versions, the host's router line (no public IP), and all Player.log/Player-prev.log files.
