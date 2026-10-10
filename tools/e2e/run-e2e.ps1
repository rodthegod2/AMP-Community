# In-game end-to-end tests (docs/superpowers/plans/2026-10-10-e2e-harness.md): the REAL game, launched through Steam with the
# isolation hook (saves in a test folder), driven by AMP's autopilot, against AmpBot as the other player.
#   run-e2e.ps1 -Scenario host-basic        one scenario
#   run-e2e.ps1 -All                        every scenario in order, then a table
# Safety (every run): real Mods folder set aside and restored (try/finally, verified), real saves compared with the baseline,
# only processes started by this run are stopped, every game process must carry the isolation arguments (else it is killed at once).
param([string]$Scenario, [switch]$All, [switch]$NoBuild, [int]$Port = 26951, [int]$TimeoutMin = 20)
$ErrorActionPreference = 'Stop'
. 'C:\Projects\bns-mp\gametest\harness.ps1'
. 'C:\Projects\bns-mp\gametest\mods-common.ps1'

$Game      = 'C:\Program Files (x86)\Steam\steamapps\common\Blade & Sorcery'
$Steam     = 'C:\Program Files (x86)\Steam\steam.exe'
$GitBash   = 'C:\Program Files\Git\bin\bash.exe'
$CacheDir  = Join-Path (Split-Path $ModsDir) 'AMP_ModCache'   # AMP's mod cache next to the Mods folder (created by the game under test)
$Repo      = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$BuildDir  = 'C:\Projects\bns-mp\build'
$RunsRoot  = 'C:\Projects\bns-mp\e2e-runs'                  # outside the repo; paths passed through Steam must not contain spaces
$Fixtures  = Join-Path $PSScriptRoot 'fixtures'
$BotExe    = Join-Path $PSScriptRoot 'AmpBot\bin\Release\net472\AmpBot.exe'
$SavesSrc  = 'C:\Projects\bns-mp\gametest\saves-backup\BladeAndSorcery\Saves\Default'
$Base      = @('InstantSloMo', '_TrueZeroEffort', 'Fascia')
$Dummy     = 'AmpE2EDummy'
$OldBuild  = 'C:\Projects\bns-mp\build-0.9.9'              # this code built as community version 0.9.9, for the self-update scenario
$Release100 = 'C:\Projects\bns-mp\build-1.0.0'            # the published v1.0.0 release (what players have), for self-update-from-1.0.0
$ReleaseRepo = 'rodthegod2/AMP-Community'
$PvP       = @{ mod = 'MultiplayerMod'; option = 'PvP' }
$SloMo     = 'InstantSloMo'                                  # fixture copy of the mod.io mod 2512544 (tiny, JSON only)
$SloMoId   = 2512544
$FasciaId  = 5966899                                         # mod.io Fascia (separate Windows and Android files)
$ZeroEffortId = 2512494                                      # mod.io "Zero Effort", the _TrueZeroEffort fixture
# The player's real mod.io data (login, subscriptions, installed mod.io mods): tests isolate mod.io into the run folder, this proves it
$ModioReal = @((Join-Path $env:LOCALAPPDATA 'mod.io\03852'), (Join-Path $env:LOCALAPPDATA 'mod.io\globalsettings.json'), (Join-Path $env:PUBLIC 'mod.io\3852'))

# GameMods/BotMods: fixture folders next to MultiplayerMod (the AMP build under test, always present on both sides); "Fixture>Folder" stages a
# fixture under another folder name. GameSetup/BotSetup: script blocks run on the staged folder (fixture variants). GameArgs: extra game arguments.
# Relaunches: how often the runner starts the game again after it closed for a restart (the Quest flow: the player starts it, not AMP).
# RestoreInGame: the swap is undone by the game itself when it quits (Quest), not by the helper.
$Scenarios = [ordered]@{
    'host-basic' = @{
        GameMods = $Base + $Dummy; BotMods = $Base + $Dummy; BotMode = 'join'
        Script   = @(@{ step = 'waitSpawn' }, @{ step = 'host'; port = $Port; maxPlayers = 4 }, @{ step = 'waitPlayers'; count = 2; timeout = 300 }, @{ step = 'wait'; seconds = 5 }, @{ step = 'quit' })
        Expect   = @{ planBlocks = $false; greeted = $true; snapshot = @($PvP) }
    }
    'host-transfer' = @{
        GameMods = $Base + $Dummy; BotMods = $Base; BotMode = 'join'
        Script   = @(@{ step = 'waitSpawn' }, @{ step = 'host'; port = $Port; maxPlayers = 4 }, @{ step = 'waitPlayers'; count = 2; timeout = 300 }, @{ step = 'wait'; seconds = 5 }, @{ step = 'quit' })
        Expect   = @{ planBlocks = $true; download = $true; quietBeforeGreeting = $true; downloaded = @($Dummy); files = @("$Dummy/empty.bin", "$Dummy/data.bin"); greeted = $true }
    }
    'host-settings' = @{
        GameMods = $Base + $Dummy; BotMods = $Base + $Dummy; BotMode = 'join'
        # Enabled first: whatever the start value is, the switch to Disabled is a real change
        Script   = @(@{ step = 'waitSpawn' }, @{ step = 'host'; port = $Port; maxPlayers = 4 }, @{ step = 'waitPlayers'; count = 2; timeout = 300 },
                     @{ step = 'setOption'; mod = 'MultiplayerMod'; option = 'PvP'; title = 'Enabled' }, @{ step = 'wait'; seconds = 2 },
                     @{ step = 'setOption'; mod = 'MultiplayerMod'; option = 'PvP'; title = 'Disabled' }, @{ step = 'wait'; seconds = 5 }, @{ step = 'quit' })
        # The seeded options start with PvP Enabled, so applying Enabled again must not be broadcast
        Expect   = @{ greeted = $true; snapshot = @(@{ mod = 'MultiplayerMod'; option = 'PvP'; title = 'Enabled' }); settingChanged = @(@{ mod = 'MultiplayerMod'; option = 'PvP'; title = 'Disabled' })
                      noSettingChanged = @(@{ mod = 'MultiplayerMod'; option = 'PvP'; title = 'Enabled' }) }
    }
    'host-gameplay' = @{
        GameMods = $Base + $Dummy; BotMods = $Base + $Dummy; BotMode = 'join'
        # The game sees the bot's avatar walk and its sword; the bot sees the host's player, its moves and the dagger the game spawns.
        # An idle player only sends its position every 5 s, hence the low seePlayerMoves. quietBeforeGreeting: no gameplay traffic during mod sync.
        Script   = @(@{ step = 'waitSpawn' }, @{ step = 'host'; port = $Port; maxPlayers = 4 }, @{ step = 'waitPlayers'; count = 2; timeout = 300 },
                     @{ step = 'expectRemotePlayers'; count = 1 }, @{ step = 'expectRemotePlayerMoved'; minDistance = 1 }, @{ step = 'expectItemSynced'; dataId = 'SwordShortCommon' },
                     @{ step = 'spawnItem'; dataId = 'DaggerCommon' }, @{ step = 'wait'; seconds = 10 }, @{ step = 'quit' })
        Expect   = @{ greeted = $true; quietBeforeGreeting = $true; play = $true; playItem = 'SwordShortCommon'; equipment = @(); seePlayers = 1; seePlayerMoves = 3; seeItems = @('DaggerCommon'); seeItemMoves = 3; lingerSec = 15 }
    }
    'join-identical' = @{
        GameMods = $Base + $Dummy; BotMods = $Base + $Dummy; BotMode = 'host'
        Script   = @(@{ step = 'waitSpawn' }, @{ step = 'join'; address = '127.0.0.1'; port = $Port }, @{ step = 'expectModSync'; blocks = $false }, @{ step = 'wait'; seconds = 3 },
                     @{ step = 'expectOption'; mod = 'MultiplayerMod'; option = 'PvP'; title = 'Disabled' }, @{ step = 'quit' })
        Expect   = @{ reports = @('ready'); greetings = 1; sendSnapshot = @(@{ mod = 'MultiplayerMod'; option = 'PvP'; title = 'Disabled'; index = 0 }); lingerSec = 10 }
    }
    'join-gameplay' = @{
        GameMods = $Base + $Dummy; BotMods = $Base + $Dummy; BotMode = 'host'
        # The bot hosts like Server.GreetPlayer (level, players, transmission) and plays: avatar, walking, a sword
        Script   = @(@{ step = 'waitSpawn' }, @{ step = 'join'; address = '127.0.0.1'; port = $Port }, @{ step = 'expectModSync'; blocks = $false },
                     @{ step = 'expectRemotePlayers'; count = 1 }, @{ step = 'expectRemotePlayerMoved'; minDistance = 1 }, @{ step = 'expectItemSynced'; dataId = 'SwordShortCommon' },
                     @{ step = 'spawnItem'; dataId = 'DaggerCommon' }, @{ step = 'wait'; seconds = 10 }, @{ step = 'quit' })
        Expect   = @{ reports = @('ready'); greetings = 1; play = $true; playItem = 'SwordShortCommon'; seePlayers = 1; seePlayerMoves = 3; seeItems = @('DaggerCommon'); seeItemMoves = 3; lingerSec = 15 }
    }
    'self-update' = @{
        # A 0.9.9 build finds the latest published release on GitHub, downloads it (redirects to the asset host), swaps itself and relaunches.
        # 'latest' is resolved from GitHub when the scenario starts (the version bump of a release doesn't break this scenario).
        GameMods = $Base; BotMode = 'none'; AmpBuild = $OldBuild; ExpectAmpAfter = 'latest'
        Script   = @(@{ step = 'waitSpawn' }, @{ step = 'expectAmpVersion'; version = '0.9.9' }, @{ step = 'updateAmp'; timeout = 300 },
                     @{ step = 'expectAmpVersion'; version = 'latest' }, @{ step = 'quit' })
        Expect   = @{}
    }
    'self-update-from-1.0.0' = @{
        # What players of the first release see: the published v1.0.0 build (checked against its SHA256SUMS) updates itself to the latest release.
        # Skipped while 1.0.0 is the latest release.
        GameMods = $Base; BotMode = 'none'; AmpBuild = $Release100; ExpectAmpAfter = 'latest'
        Skip     = { if ([version](Get-LatestRelease) -le [version]'1.0.0') { 'the latest release is still 1.0.0' } }
        Script   = @(@{ step = 'waitSpawn' }, @{ step = 'expectAmpVersion'; version = '1.0.0' }, @{ step = 'updateAmp'; timeout = 300 },
                     @{ step = 'expectAmpVersion'; version = 'latest' }, @{ step = 'quit' })
        Expect   = @{}
    }
    'modio-probe' = @{
        # Read-only mod.io through the game's plugin (no login): mod info, search, download into the mod cache
        GameMods = $Base; BotMode = 'none'
        Script   = @(@{ step = 'waitSpawn' }, @{ step = 'modioGetMod'; id = $SloMoId; expectName = 'Instant Slow-mo + Silent' },
                     @{ step = 'modioSearch'; phrase = 'Instant Slow-mo'; expectId = $SloMoId }, @{ step = 'modioDownload'; id = $SloMoId; expectFile = 'Spell_SlowTime.json'; timeout = 300 }, @{ step = 'quit' })
        Expect   = @{}
    }
    'join-download-temporary' = @{
        GameMods = $Base; BotMods = $Base + $Dummy; BotMode = 'host'; WaitRestore = $true
        Script   = @(@{ step = 'waitSpawn' }, @{ step = 'join'; address = '127.0.0.1'; port = $Port }, @{ step = 'expectModSync'; blocks = $true },
                     @{ step = 'downloadAndRestart'; mode = 'Temporary'; timeout = 300 }, @{ step = 'expectRejoined'; timeout = 300 },
                     @{ step = 'expectModFolder'; folder = $Dummy; present = $true }, @{ step = 'expectProfileActive'; active = $true }, @{ step = 'quit' })
        Expect   = @{ reports = @('needs_setup', 'ready'); served = @($Dummy); greetings = 1; lingerSec = 10 }
    }
    'join-modio' = @{
        # The host's InstantSloMo came from mod.io and mod.io still serves that version: the game gets it from mod.io, never from the host
        GameMods = @('_TrueZeroEffort', 'Fascia'); BotMods = $Base; BotMode = 'host'; WaitRestore = $true; CachedFolder = $SloMo; CachedFrom = 'modio'
        Script   = @(@{ step = 'waitSpawn' }, @{ step = 'join'; address = '127.0.0.1'; port = $Port }, @{ step = 'expectModSync'; blocks = $true },
                     @{ step = 'downloadAndRestart'; mode = 'Temporary'; timeout = 300 }, @{ step = 'expectRejoined'; timeout = 300 },
                     @{ step = 'expectModFolder'; folder = $SloMo; present = $true }, @{ step = 'expectProfileActive'; active = $true }, @{ step = 'quit' })
        Expect   = @{ reports = @('needs_setup', 'ready'); notServed = @($SloMo); greetings = 1; lingerSec = 10 }
    }
    'join-modio-fallback' = @{
        # The host's copy differs from mod.io's file (an extra file): the mod.io download doesn't verify, the host sends the mod instead
        GameMods = @('_TrueZeroEffort', 'Fascia'); BotMods = $Base; BotMode = 'host'; WaitRestore = $true; CachedFolder = $SloMo; CachedFrom = 'host'
        BotExtraFile = "$SloMo\amp-e2e-variant.txt"
        Script   = @(@{ step = 'waitSpawn' }, @{ step = 'join'; address = '127.0.0.1'; port = $Port }, @{ step = 'expectModSync'; blocks = $true },
                     @{ step = 'downloadAndRestart'; mode = 'Temporary'; timeout = 300 }, @{ step = 'expectRejoined'; timeout = 300 },
                     @{ step = 'expectModFolder'; folder = $SloMo; present = $true }, @{ step = 'quit' })
        Expect   = @{ reports = @('needs_setup', 'ready'); served = @($SloMo); greetings = 1; lingerSec = 10 }
    }
    'host-matcher' = @{
        # Quest matching when hosting starts (real mod.io data). Fascia came from mod.io and has an Android file. InstantSloMo without its
        # id.modio, its manifest named like its mod.io page ("Instant Slow-mo + Silent" by Jenix106), is found by name and author: automatic.
        # TrueZeroEffort without its id.modio is only found as "True Zero Effort" (mod.io: "Zero Effort" by Jenix106): the host must say Yes.
        # The dummy has no Quest version. The bot joins as a Quest player and must get these as questModioId.
        GameMods = @($SloMo, 'Fascia', '_TrueZeroEffort>TrueZeroEffort', $Dummy); BotMods = @(); BotMode = 'join'
        GameSetup = { param($mods)
            Remove-Item -LiteralPath (Join-Path $mods "$SloMo\id.modio"), (Join-Path $mods 'TrueZeroEffort\id.modio')
            $manifest = Join-Path $mods "$SloMo\manifest.json"
            (Get-Content -Raw -LiteralPath $manifest) -replace '"Name": "InstantSloMoSilent"', '"Name": "Instant Slow-mo + Silent"' | Set-Content -NoNewline -LiteralPath $manifest
            if (-not (Select-String -Quiet -LiteralPath $manifest -Pattern 'Instant Slow-mo \+ Silent')) { throw 'renaming the InstantSloMo fixture failed' }
        }
        Script   = @(@{ step = 'waitSpawn' }, @{ step = 'host'; port = $Port; maxPlayers = 4 },
                     @{ step = 'expectMatch'; folder = 'Fascia'; modioId = $FasciaId }, @{ step = 'expectMatch'; folder = $SloMo; modioId = $SloMoId },
                     @{ step = 'expectMatch'; folder = 'TrueZeroEffort'; modioId = $ZeroEffortId; confirm = $true }, @{ step = 'expectMatch'; folder = $Dummy; modioId = 0 },
                     @{ step = 'waitPlayers'; count = 2; timeout = 300 }, @{ step = 'wait'; seconds = 5 }, @{ step = 'quit' })
        # questResend: the bot connects while mod.io is still being asked, gets the list marked "pending" and must get it again, complete
        Expect   = @{ platform = 'android'; questIds = @{ $SloMo = $SloMoId; 'Fascia' = $FasciaId; $Dummy = -1 }; questResend = $true; greeted = $true }
    }
    'join-quest-sim' = @{
        # The game is a Quest player (-amp-simulate-platform android) joining a PC host. The host's InstantSloMo has no id.modio (as if from Nexus)
        # and is mapped to mod.io 2512544; the dummy has no Quest version. The game downloads the Android file from mod.io, swaps in-game
        # (TrueZeroEffort, which the host lacks, is turned off), closes; the runner starts it again like the player would; it rejoins.
        # InstantSloMo has one file for Windows and Android, so Fascia (separate files) is also downloaded, into the cache only (its asset bundles
        # are Android builds): it must be the Android file, i.e. mod.io was asked as Android.
        GameMods = @('_TrueZeroEffort>TrueZeroEffort'); BotMods = @($SloMo, $Dummy); BotMode = 'host'
        BotSetup = { param($mods) Remove-Item -LiteralPath (Join-Path $mods "$SloMo\id.modio") }
        GameArgs = @('-amp-simulate-platform', 'android'); Relaunches = 1; RestoreInGame = $true; CachedFolder = $SloMo; CachedFrom = 'modio'
        Script   = @(@{ step = 'waitSpawn' }, @{ step = 'join'; address = '127.0.0.1'; port = $Port }, @{ step = 'expectModSync'; blocks = $true },
                     @{ step = 'expectUnavailable'; folder = $Dummy }, @{ step = 'downloadAndRestart'; mode = 'Temporary'; timeout = 300 },
                     @{ step = 'expectRejoined'; timeout = 300 }, @{ step = 'expectModFolder'; folder = $SloMo; present = $true },
                     @{ step = 'expectModFolder'; folder = 'TrueZeroEffort'; present = $false }, @{ step = 'expectUnavailable'; folder = $Dummy },
                     @{ step = 'expectCachedModio'; folder = $SloMo; platform = 'android' }, @{ step = 'modioDownload'; id = $FasciaId; timeout = 300 },
                     @{ step = 'expectCachedModio'; folder = 'Fascia'; platform = 'android' }, @{ step = 'expectProfileActive'; active = $false }, @{ step = 'quit' })
        Expect   = @{ reports = @('needs_setup', 'ready'); notServed = @($SloMo, $Dummy); greetings = 1; sendQuestIds = @{ $SloMo = $SloMoId; $Dummy = -1 }; lingerSec = 10 }
    }
    'join-quest-sim-killed' = @{
        # As join-quest-sim, but the session ends like Android may end an app: the process dies, no quit event, so the in-game restore on
        # quit never runs. The next start must undo the swap in-game and close by itself (its catalogs came from the swapped folders);
        # the runner starts the game both times like the player would. Afterwards Mods is the staged set again.
        GameMods = @('_TrueZeroEffort>TrueZeroEffort'); BotMods = @($SloMo, $Dummy); BotMode = 'host'
        BotSetup = { param($mods) Remove-Item -LiteralPath (Join-Path $mods "$SloMo\id.modio") }
        GameArgs = @('-amp-simulate-platform', 'android'); Relaunches = 2; RestoreInGame = $true; CachedFolder = $SloMo; CachedFrom = 'modio'
        Script   = @(@{ step = 'waitSpawn' }, @{ step = 'join'; address = '127.0.0.1'; port = $Port }, @{ step = 'expectModSync'; blocks = $true },
                     @{ step = 'downloadAndRestart'; mode = 'Temporary'; timeout = 300 }, @{ step = 'expectRejoined'; timeout = 300 },
                     @{ step = 'expectModFolder'; folder = $SloMo; present = $true }, @{ step = 'killGame' },
                     @{ step = 'expectStartupRestore'; folder = $SloMo; timeout = 300 }, @{ step = 'quit' })
        Expect   = @{ reports = @('needs_setup', 'ready'); notServed = @($SloMo, $Dummy); greetings = 1; sendQuestIds = @{ $SloMo = $SloMoId; $Dummy = -1 }; lingerSec = 10 }
    }
}

function Note([string]$text) { Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $text) }

# The version of the latest published release (what the in-game updater finds), asked once per runner
$script:latestRelease = $null
function Get-LatestRelease {
    if (-not $script:latestRelease) {
        $r = Invoke-RestMethod -Uri "https://api.github.com/repos/$ReleaseRepo/releases/latest" -Headers @{ 'User-Agent' = 'AMP-e2e' } -TimeoutSec 30
        $script:latestRelease = $r.tag_name.TrimStart('v')
        if ($script:latestRelease -notmatch '^\d+(\.\d+){1,3}$') { throw "unexpected latest release tag $($r.tag_name)" }
    }
    $script:latestRelease
}

# The published v1.0.0 build in $Release100: downloaded from our own GitHub release once and checked against the release's SHA256SUMS
function Get-Release100 {
    if (Test-Path -LiteralPath (Join-Path $Release100 'AMP.dll')) { return }
    Note "fetching the v1.0.0 release into $Release100"
    $tmp = Join-Path $RunsRoot 'release-1.0.0-download'
    if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $tmp | Out-Null
    $base = "https://github.com/$ReleaseRepo/releases/download/v1.0.0"
    Invoke-WebRequest -Uri "$base/AMP-Community-1.0.0.zip" -OutFile (Join-Path $tmp 'AMP-Community-1.0.0.zip') -UseBasicParsing -TimeoutSec 120
    Invoke-WebRequest -Uri "$base/SHA256SUMS.txt" -OutFile (Join-Path $tmp 'SHA256SUMS.txt') -UseBasicParsing -TimeoutSec 60
    $expected = ((Get-Content -LiteralPath (Join-Path $tmp 'SHA256SUMS.txt') | Where-Object { $_ -match 'AMP-Community-1\.0\.0\.zip' }) -split '\s+')[0]
    $actual = (Get-FileHash -Algorithm SHA256 (Join-Path $tmp 'AMP-Community-1.0.0.zip')).Hash
    if (-not $expected -or $actual -ne $expected.ToUpperInvariant()) { throw "v1.0.0 zip hash $actual doesn't match SHA256SUMS ($expected)" }
    Expand-Archive -LiteralPath (Join-Path $tmp 'AMP-Community-1.0.0.zip') -DestinationPath (Join-Path $tmp 'x')
    New-Item -ItemType Directory -Force -Path $Release100 | Out-Null
    Copy-Item -Path (Join-Path $tmp 'x\MultiplayerMod\*') -Destination $Release100
    Remove-Item -LiteralPath $tmp -Recurse -Force
    Note "  v1.0.0 verified ($actual)"
}

# Unity keeps its log open; read it without locking it
function Read-Shared([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return '' }
    try {
        $fs = [IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite, Delete')
        try { return (New-Object IO.StreamReader($fs)).ReadToEnd() } finally { $fs.Dispose() }
    } catch { return $null }
}

function Get-ModioShape { foreach ($p in $ModioReal) { if (Test-Path -LiteralPath $p -PathType Leaf) { $i = Get-Item -LiteralPath $p; "$p|$($i.Length)|$($i.LastWriteTimeUtc.Ticks)" } elseif (Test-Path -LiteralPath $p) { Get-TreeManifest $p | ForEach-Object { "$p\$_" } } else { "$p|missing" } } }

function Get-CommandLine([int]$Id) { (Get-CimInstance Win32_Process -Filter "ProcessId=$Id" -ErrorAction SilentlyContinue).CommandLine }

# Names and sizes only: moves inside the Mods folder keep file contents but may touch folder times
function Get-ModsShape([string]$Root) { if (Test-Path -LiteralPath $Root) { Get-TreeManifest $Root | ForEach-Object { ($_ -split '\|')[0..1] -join '|' } } }

function Build-All {
    Note 'building AMP (build.sh) and AmpBot'
    & $GitBash -lc "cd /c/Projects/bns-mp && ./build.sh" 2>&1 | Select-Object -Last 3 | ForEach-Object { Note "  $_" }
    if ($LASTEXITCODE -ne 0) { throw 'AMP build failed' }
    & dotnet build (Join-Path $PSScriptRoot 'AmpBot\AmpBot.csproj') -c Release -nologo -v:minimal -nodeReuse:false -p:UseSharedCompilation=false 2>&1 | Select-Object -Last 2 | ForEach-Object { Note "  $_" }
    if ($LASTEXITCODE -ne 0) { throw 'AmpBot build failed' }
    & $BotExe --selftest $Repo | Select-String -Pattern '^BOT (FAIL|DONE)' | ForEach-Object { Note "  $_" }
    if ($LASTEXITCODE -ne 0) { throw 'AmpBot selftest failed' }
}

# The same code as the build under test, but saying it is community version 0.9.9 (a temporary worktree, nothing committed)
function Build-OldAmp {
    Note "building the 0.9.9 test AMP into $OldBuild"
    $ErrorActionPreference = 'Continue'   # git reports progress on stderr; exit codes are checked instead
    $wt = 'C:\Projects\bns-mp\wt-amp-099'   # under bns-mp so the local Directory.Build.props applies
    if (Test-Path -LiteralPath $wt) { & git -C $Repo worktree remove --force $wt }
    & git -C $Repo worktree add --detach $wt HEAD 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'git worktree add failed' }
    try {
        $defines = Join-Path $wt 'Data\Defines.cs'
        (Get-Content -Raw -LiteralPath $defines) -replace 'COMMUNITY_VERSION = "[^"]+"', 'COMMUNITY_VERSION = "0.9.9"' | Set-Content -NoNewline -LiteralPath $defines
        if (-not (Select-String -Quiet -LiteralPath $defines -Pattern 'COMMUNITY_VERSION = "0.9.9"')) { throw 'patching the version failed' }
        & dotnet msbuild (Join-Path $wt 'Adammantium Multiplayer Mod.csproj') -nologo -v:minimal -p:Configuration=Release "-p:GameDirectory=$Game" `
            '-p:NetamiteDirectory=C:/Projects/bns-mp/ampdeps' "-p:OutputPath=$OldBuild\" '-p:PostBuildEvent=' -nodeReuse:false -p:UseSharedCompilation=false 2>&1 | Select-Object -Last 1 | ForEach-Object { Note "  $_" }
        if ($LASTEXITCODE -ne 0) { throw '0.9.9 build failed' }
    } finally { & git -C $Repo worktree remove --force $wt }
}

function Copy-ModSet([string]$Target, [string[]]$Names, [string]$From = $BuildDir) {
    New-Item -ItemType Directory -Force -Path $Target | Out-Null
    $amp = Join-Path $Target 'MultiplayerMod'
    New-Item -ItemType Directory -Force -Path $amp | Out-Null
    Copy-Item -Path "$From\AMP.dll", "$From\AMP.pdb", "$From\Netamite*.dll", "$From\Newtonsoft.Json.dll" -Destination $amp
    $manifest = if (Test-Path -LiteralPath (Join-Path $From 'manifest.json')) { Join-Path $From 'manifest.json' } else { Join-Path $Repo 'BS Files\manifest.json' }
    Copy-Item -LiteralPath $manifest -Destination $amp
    foreach ($n in $Names) {
        $src, $dst = if ($n.Contains('>')) { $n -split '>', 2 } else { $n, $n }
        Copy-Item -LiteralPath (Join-Path $Fixtures $src) -Destination (Join-Path $Target $dst) -Recurse
    }
}

# Waits for AMP's swap helper (a powershell started through WMI, so not our child) that works on this run's cache, then stops it if it hangs
function Wait-Helpers([int]$Seconds) {
    $until = (Get-Date).AddSeconds($Seconds)
    do {
        $helpers = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains('AMP_ModCache\helper') })
        if (-not $helpers) { return $true }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $until)
    foreach ($h in $helpers) { Note "stopping hung swap helper pid $($h.ProcessId)"; Stop-Process -Id $h.ProcessId -Force -ErrorAction SilentlyContinue }
    return $false
}

function Invoke-Scenario([string]$Name) {
    $s = $Scenarios[$Name]
    if (-not $s) { throw "unknown scenario $Name (known: $($Scenarios.Keys -join ', '))" }
    $result = [ordered]@{ scenario = $Name; status = 'FAIL'; reason = ''; seconds = 0; timings = '' }
    $started = Get-Date
    if ($s.Skip) {
        $why = & $s.Skip
        if ($why) { Note "E2E $Name SKIP - $why"; $result.status = 'SKIP'; $result.reason = $why; return [pscustomobject]$result }
    }
    if ($s.AmpBuild -eq $Release100) { Get-Release100 }
    # 'latest': the version of the latest GitHub release, in the expected version and in expectAmpVersion steps
    if ($s.ExpectAmpAfter -eq 'latest' -or @($s.Script | Where-Object { $_.version -eq 'latest' }).Count -gt 0) {
        $latest = Get-LatestRelease
        Note "latest release on GitHub: $latest"
        $s = $s.Clone()
        if ($s.ExpectAmpAfter -eq 'latest') { $s.ExpectAmpAfter = $latest }
        $s.Script = @($s.Script | ForEach-Object { $c = $_.Clone(); if ($c.version -eq 'latest') { $c.version = $latest }; $c })
    }

    # Preconditions: nothing may be running or left over from an earlier run
    if (Get-Process BladeAndSorcery -ErrorAction SilentlyContinue) { throw 'a BladeAndSorcery process is already running' }
    if (Test-Path -LiteralPath $CacheDir) { throw "$CacheDir exists before the run (left over?) - inspect it" }
    if (-not (Test-Path -LiteralPath (Join-Path $Fixtures $Dummy))) { & (Join-Path $PSScriptRoot 'make-fixtures.ps1') | Out-Null }
    if (-not (Get-Process steam -ErrorAction SilentlyContinue)) { throw 'Steam is not running' }
    Assert-Isolation $Game | ForEach-Object { Note $_ }
    Assert-RealSavesUnchanged | ForEach-Object { Note $_ }
    $modioBefore = @(Get-ModioShape)

    $run = Join-Path $RunsRoot ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $Name)
    $docs = Join-Path $run 'docs'
    $log = Join-Path $run 'game.log'
    $script = Join-Path $run 'script.json'
    $expect = Join-Path $run 'bot-expect.json'
    New-Item -ItemType Directory -Force -Path $run | Out-Null
    if ($run -match '\s') { throw 'run folder must not contain spaces' }
    $seed = Join-Path $docs 'My Games\BladeAndSorcery\Saves\Default'
    New-Item -ItemType Directory -Force -Path $seed | Out-Null
    Copy-Item -LiteralPath (Get-ChildItem -LiteralPath $SavesSrc -Filter *.chr | Sort-Object Name | Select-Object -First 1).FullName, "$SavesSrc\Options.opt" -Destination $seed
    ConvertTo-Json -InputObject @($s.Script) -Depth 5 | Set-Content -LiteralPath $script -Encoding ascii
    $x = @{ timeoutSec = 60 * $TimeoutMin - 60 } + $s.Expect
    ConvertTo-Json -InputObject $x -Depth 5 | Set-Content -LiteralPath $expect -Encoding ascii
    $botMods = Join-Path $run 'bot-mods'
    if ($s.BotMode -ne 'none') { Copy-ModSet $botMods $s.BotMods }
    if ($s.BotExtraFile) { Set-Content -LiteralPath (Join-Path $botMods $s.BotExtraFile) -Encoding ascii -Value 'AMP e2e: this copy differs from the mod.io file' }
    if ($s.BotSetup) { & $s.BotSetup $botMods }
    Note "run folder $run"

    $games = New-Object System.Collections.Generic.List[object]   # @{ proc; log; redirected } in start order
    $bot = $null; $t0 = $null; $staged = $null; $events = New-Object System.Collections.Generic.List[string]
    $safetyProblem = $null
    & 'C:\Projects\bns-mp\gametest\mods-aside.ps1' | ForEach-Object { Note $_ }
    try {
        Copy-ModSet $ModsDir $s.GameMods $(if ($s.AmpBuild) { $s.AmpBuild } else { $BuildDir })
        if ($s.GameSetup) { & $s.GameSetup $ModsDir }
        $staged = @(Get-ModsShape $ModsDir)

        $botArgs = if ($s.BotMode -eq 'host') { "--host $Port" } else { "--join 127.0.0.1 $Port" }
        if ($s.BotMode -ne 'none') {
        $bot = Start-Process -FilePath $BotExe -ArgumentList "$botArgs --mods $botMods --cache $(Join-Path $run 'bot-cache') --expect $expect" `
                             -RedirectStandardOutput (Join-Path $run 'bot.log') -RedirectStandardError (Join-Path $run 'bot.err') -PassThru -WindowStyle Hidden
        $null = $bot.Handle   # keeps ExitCode readable after the process ends
        Note "bot pid $($bot.Id) ($($s.BotMode))"
        }

        $gameArgs = '--doorstop-enabled', 'true', '--doorstop-target-assembly', 'AmpTestBoot\AmpTestBoot.dll', '-amp-test-docs', $docs,
                    '-vrmode', 'mockhmd', '-skipintro', '-skipmodswarning', '-screen-fullscreen', '0', '-screen-width', '640', '-screen-height', '360',
                    '-force-d3d11', '-level', 'Home', '-charindex', '0', '-logFile', $log, '-amp-autopilot', $script
        if ($s.GameArgs) { $gameArgs += $s.GameArgs }
        $relaunched = 0
        $t0 = Get-Date
        & $Steam -applaunch 629730 @gameArgs
        Note 'launched through Steam'

        $deadline = $t0.AddMinutes($TimeoutMin)
        $end = $null; $exitedAt = $null; $seen = @{}
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 500
            # Every game process started after our launch is ours (the first one, or AMP's relaunch through Steam)
            foreach ($gp in Get-Process BladeAndSorcery -ErrorAction SilentlyContinue) {
                if ($games | Where-Object { $_.proc.Id -eq $gp.Id }) { continue }
                $null = $gp.Handle
                if ($gp.StartTime -lt $t0) { $safetyProblem = "foreign game process $($gp.Id) appeared"; break }
                $cmd = Get-CommandLine $gp.Id
                if (-not $cmd -or -not $cmd.Contains('--doorstop-enabled true') -or -not $cmd.Contains("-amp-test-docs $docs")) {
                    Stop-Process -Id $gp.Id -Force
                    $safetyProblem = "UNISOLATED game process $($gp.Id) killed: $cmd"; break
                }
                if ($games | Where-Object { -not $_.proc.HasExited }) {
                    Stop-Process -Id $gp.Id -Force; $events.Add("killed duplicate game pid $($gp.Id)"); continue
                }
                $games.Add(@{ proc = $gp; log = ''; redirected = $false })
                $events.Add("game pid $($gp.Id) started +$([int]($gp.StartTime - $t0).TotalSeconds)s")
                Note "game pid $($gp.Id)"
                $exitedAt = $null
            }
            if ($safetyProblem) { break }
            if ($games.Count -eq 0) { if (((Get-Date) - $t0).TotalSeconds -gt 90) { $result.reason = 'no game process within 90 s'; break } else { continue } }

            # The newest process owns the log file (Unity truncates it on start); an exited one keeps what was read last
            $g = $games[$games.Count - 1]
            $text = Read-Shared $log
            if ($text -ne $null -and ($text.Length -ge $g.log.Length -or -not $g.proc.HasExited)) { $g.log = $text }
            if (-not $g.redirected) {
                $boot = Read-Shared (Join-Path $docs 'AmpTestBoot.log')
                if ($boot -and $boot.Contains("pid $($g.proc.Id)") -and $boot.Contains('save root redirected')) { $g.redirected = $true }
                elseif (((Get-Date) - $g.proc.StartTime).TotalSeconds -gt 120) { Stop-Process -Id $g.proc.Id -Force -ErrorAction SilentlyContinue; $safetyProblem = "save redirect not confirmed for pid $($g.proc.Id) (killed)"; break }
            }

            foreach ($raw in (($games | ForEach-Object { $_.log }) -join "`n") -split "`r?`n") {
                # Unity prefixes each Debug.Log line with a time stamp and the calling method
                if ($raw -notmatch '(?:^|: )(AMPTEST (?:START|STEP|OK|FAIL|DONE|INFO).*)$') { continue }
                $line = $Matches[1]
                if (-not $seen.ContainsKey($line)) { $seen[$line] = [int]((Get-Date) - $t0).TotalSeconds; Note "  $line" }
                if ($line -match '^AMPTEST (DONE|FAIL)') { $end = $line }
            }
            if ($end) { break }

            $g.proc.Refresh()
            if ($g.proc.HasExited) {
                if (-not $exitedAt) { $exitedAt = Get-Date; Note "game pid $($g.proc.Id) exited ($($g.proc.ExitCode)), waiting for a relaunch" }
                # The game closed for a restart it can't do itself (Quest flow): start it again with the same arguments, like the player would
                $restarts = @($seen.Keys | Where-Object { $_ -match '\(closing for the restart\)' }).Count
                if ($relaunched -lt $s.Relaunches -and $restarts -gt $relaunched -and ((Get-Date) - $exitedAt).TotalSeconds -gt 5 -and -not (Get-Process BladeAndSorcery -ErrorAction SilentlyContinue)) {
                    $relaunched++
                    & $Steam -applaunch 629730 @gameArgs
                    $events.Add("runner relaunched the game through Steam (+$([int]((Get-Date) - $t0).TotalSeconds)s)")
                    Note 'relaunched through Steam (standing in for the player)'
                }
                elseif (((Get-Date) - $exitedAt).TotalSeconds -gt 180) { $result.reason = "game exited (code $($g.proc.ExitCode)) without AMPTEST DONE/FAIL and wasn't relaunched"; break }
            }
            if ($bot -and $bot.HasExited -and $bot.ExitCode -ne 0) { $result.reason = 'bot failed before the game finished'; break }
        }
        if (-not $end -and -not $result.reason -and -not $safetyProblem) { $result.reason = "timed out after $TimeoutMin min" }

        # After DONE/FAIL the game quits by itself; otherwise (timeout, bot failure) it is stopped right away
        foreach ($g in $games) {
            if (-not $g.proc.WaitForExit($(if ($end) { 60000 } else { 0 }))) { Stop-Process -Id $g.proc.Id -Force; $g.proc.WaitForExit(30000) | Out-Null; $events.Add("stopped own game pid $($g.proc.Id)") }
        }
        $botEnd = 'BOT DONE (no bot in this scenario)'
        if ($bot) {
            if (-not $bot.WaitForExit(90000)) { Stop-Process -Id $bot.Id -Force; $events.Add("killed bot pid $($bot.Id)") }
            $botOut = Get-Content -LiteralPath (Join-Path $run 'bot.log') -ErrorAction SilentlyContinue
            $botEnd = $botOut | Where-Object { $_ -match '^BOT (DONE|FAIL)' } | Select-Object -Last 1
            Note "bot: $botEnd"
        }

        if ($s.ExpectAmpAfter) {
            # Keep swap: the new AMP stays in Mods, the replaced build is kept in AMP_ModCache\old\<journal id>
            Wait-Helpers 60 | Out-Null
            $manifest = Join-Path $ModsDir 'MultiplayerMod\manifest.json'
            $version = if (Test-Path -LiteralPath $manifest) { (Get-Content -Raw -LiteralPath $manifest | ConvertFrom-Json).ModVersion } else { 'missing' }
            $old = @(Get-ChildItem -Path (Join-Path $CacheDir 'old\*\MultiplayerMod\AMP.dll') -ErrorAction SilentlyContinue)
            $oldSame = $old.Count -gt 0 -and (Get-FileHash $old[0].FullName).Hash -eq (Get-FileHash (Join-Path $s.AmpBuild 'AMP.dll')).Hash
            $hl = Read-Shared (Join-Path $CacheDir 'helper\helper.log')
            if ($hl) { Set-Content -LiteralPath (Join-Path $run 'helper.log') -Value $hl }
            $events.Add("update: Mods\MultiplayerMod is $version, old build kept in the cache=$oldSame ($($old.Count) found)")
            if (-not ($version -eq $s.ExpectAmpAfter -and $oldSame) -and -not $result.reason) { $result.reason = "after the update Mods has AMP $version, old build kept=$oldSame" }
        }

        if ($s.WaitRestore -or $s.RestoreInGame) {
            # Temporary swap: the restore helper starts when the game quits and puts the staged set back (or the game did it while quitting)
            $helperLog = Join-Path $CacheDir 'helper\helper.log'
            $until = (Get-Date).AddSeconds($(if ($s.RestoreInGame) { 0 } else { 180 })); $restored = [bool]$s.RestoreInGame
            while ((Get-Date) -lt $until) {
                $hl = Read-Shared $helperLog
                if ($hl) {
                    $last = ($hl -split "`r?`n" | Where-Object { $_ })
                    $start = [array]::FindLastIndex([string[]]$last, [Predicate[string]] { param($l) $l.Contains('helper start: restore') })
                    if ($start -ge 0 -and ($last[$start..($last.Count - 1)] | Where-Object { $_ -match ' (done|failed|error)' })) { $restored = $true; break }
                }
                Start-Sleep -Seconds 1
            }
            Wait-Helpers 60 | Out-Null
            $hl = Read-Shared $helperLog
            if ($hl) { Set-Content -LiteralPath (Join-Path $run 'helper.log') -Value $hl }
            $journal = Join-Path $CacheDir 'session.json'
            $state = if (Test-Path -LiteralPath $journal) { (Get-Content -Raw -LiteralPath $journal | ConvertFrom-Json).state } else { 'gone' }
            $shapeNow = @(Get-ModsShape $ModsDir)
            $modsBack = -not (Compare-Object $staged $shapeNow)
            $folder = if ($s.CachedFolder) { $s.CachedFolder } else { $Dummy }
            $cached = @(Get-ChildItem -Path (Join-Path $CacheDir 'mods\*\entry.json') -ErrorAction SilentlyContinue | ForEach-Object { Get-Content -Raw $_.FullName | ConvertFrom-Json } | Where-Object { $_.folder -eq $folder })
            # modioFileId > 0: the files came from mod.io; 0 or missing: from the host
            $from = if ($cached.Count -eq 0) { 'nowhere' } elseif ($cached[0].modioFileId -gt 0) { "modio (file $($cached[0].modioFileId))" } else { 'host' }
            $fromOk = -not $s.CachedFrom -or $from -like "$($s.CachedFrom)*"
            $events.Add("restore: helper finished=$restored, journal=$state, Mods back to the staged set=$modsBack, $folder in cache from $from")
            if (-not $fromOk -and -not $result.reason) { $result.reason = "$folder came from $from, expected $($s.CachedFrom)" }
            if (-not ($restored -and ($state -eq 'gone' -or $state -eq 'Restored') -and $modsBack -and $cached.Count -gt 0)) {
                if (-not $modsBack) { Compare-Object $staged $shapeNow | Out-String | ForEach-Object { $events.Add("Mods diff: $_") } }
                if (-not $result.reason) { $result.reason = 'the Temporary swap was not undone correctly after quitting' }
            }
        }

        $gameOk = $end -like 'AMPTEST DONE*'
        $botOk = (-not $bot -or $bot.ExitCode -eq 0) -and $botEnd -like 'BOT DONE*'
        if (-not $gameOk -and -not $result.reason) { $result.reason = "game: $end" }
        if (-not $botOk -and -not $result.reason) { $result.reason = "bot: $botEnd" }
        if ($gameOk -and $botOk -and -not $result.reason) { $result.status = 'PASS' }
        $result.timings = (($seen.GetEnumerator() | Where-Object { $_.Key -match '^AMPTEST (OK|DONE|START)' } | Sort-Object Value | ForEach-Object { ($_.Key -replace '^AMPTEST ', '' -replace ' \(.*$', '') + '@' + $_.Value + 's' }) -join ' ')
    }
    finally {
        foreach ($g in $games) { if (-not $g.proc.HasExited) { Stop-Process -Id $g.proc.Id -Force -ErrorAction SilentlyContinue }; $g.proc.WaitForExit(30000) | Out-Null }
        # A process only leaves the list a moment after it exited; mods-restore refuses to run while any game process exists
        $until = (Get-Date).AddSeconds(60)
        while ((Get-Date) -lt $until -and @(Get-Process BladeAndSorcery -ErrorAction SilentlyContinue).Count -gt 0) { Start-Sleep -Milliseconds 500 }
        if ($bot -and -not $bot.HasExited) { Stop-Process -Id $bot.Id -Force -ErrorAction SilentlyContinue }
        Wait-Helpers 120 | Out-Null
        for ($i = 0; $i -lt $games.Count; $i++) { Set-Content -LiteralPath (Join-Path $run "game-$($i + 1).log") -Value $games[$i].log }
        # Everything the run put into the real install goes into the run folder (moved, never deleted); then the real Mods come back
        $after = Join-Path $run 'mods-after'
        New-Item -ItemType Directory -Force -Path $after | Out-Null
        Get-ChildItem -LiteralPath $ModsDir -Force | ForEach-Object { Move-Item -LiteralPath $_.FullName -Destination (Join-Path $after $_.Name) }
        if (Test-Path -LiteralPath $CacheDir) { Move-Item -LiteralPath $CacheDir -Destination (Join-Path $run 'AMP_ModCache') }
        & 'C:\Projects\bns-mp\gametest\mods-restore.ps1' | ForEach-Object { Note $_ }
        if (-not ((Get-Content -LiteralPath "$Game\doorstop_config.ini") -contains 'enabled=false')) { throw 'doorstop_config.ini is NOT enabled=false - STOP' }
        Assert-RealSavesUnchanged | ForEach-Object { Note $_ }   # throws on any difference
        $modioDiff = Compare-Object $modioBefore @(Get-ModioShape)
        if ($modioDiff) { $modioDiff | Out-String | Write-Host; throw 'SAFETY: the real mod.io data changed during the run - STOP' }
        Note 'real mod.io data unchanged' 
        $events | ForEach-Object { Note "  $_" }
    }
    if ($safetyProblem) { throw "SAFETY: $safetyProblem" }
    $result.seconds = [int]((Get-Date) - $started).TotalSeconds
    Note ("E2E {0} {1}{2}" -f $Name, $result.status, $(if ($result.reason) { " - $($result.reason)" } else { '' }))
    [pscustomobject]$result
}

if (-not $All -and -not $Scenario) { throw "use -Scenario <$($Scenarios.Keys -join '|')> or -All" }
if (-not $NoBuild) { Build-All; if ($All -or $Scenario -eq 'self-update') { Build-OldAmp } }
$names = if ($All) { @($Scenarios.Keys) } else { @($Scenario) }
$results = foreach ($n in $names) { Invoke-Scenario $n }
$results | Format-Table scenario, status, seconds, reason -AutoSize | Out-String -Width 250 | Write-Host
$results | ForEach-Object { Write-Host "$($_.scenario) timings: $($_.timings)" }
if ($results | Where-Object { $_.status -notin 'PASS', 'SKIP' }) { exit 1 }
