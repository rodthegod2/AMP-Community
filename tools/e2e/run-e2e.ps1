# In-game end-to-end tests (docs/superpowers/plans/2026-10-10-e2e-harness.md): the REAL game, launched through Steam with the
# isolation hook (saves in a test folder), driven by AMP's autopilot, against AmpBot as the other player.
#   run-e2e.ps1 -Scenario host-basic        one scenario
#   run-e2e.ps1 -All                        every scenario in order, then a table
# Safety (every run): real Mods folder set aside and restored (try/finally, verified), real saves compared with the baseline,
# only processes started by this run are stopped, every game process must carry the isolation arguments (else it is killed at once).
#   run-e2e.ps1 -All -Extended              also the long scenarios (Extended = $true: the player's real mod set, the soak)
param([string]$Scenario, [switch]$All, [switch]$Extended, [switch]$NoBuild, [int]$Port = 26951, [int]$TimeoutMin = 20)
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
$SsdpPort  = $Port + 10                                      # the bot's fake UPnP router (host-upnp-fake)
$Code127   = 'HW22-22DB-AWJ6'                               # join code of 127.0.0.1:26951 (pinned in Tests/ModSyncTests/NatTests.cs)
$CodeFake  = 'TE29-43VB-AX3L'                               # join code of 203.0.113.7:26951, the fake router's internet address (pinned there too)
# '*REAL*' in GameMods/BotMods: COPIES of every entry of the player's real Mods folder (read from the aside folder of this run, never changed).
# In Expect.downloaded it stands for every enabled real mod (a folder with a manifest.json, not starting with "_"), except the host's
# default personal mod HJ_NexusModManager, which joiners don't need.
$Real      = '*REAL*'
$script:RealSrc = $null
# The player's real mod.io data (login, subscriptions, installed mod.io mods): tests isolate mod.io into the run folder, this proves it
$ModioReal = @((Join-Path $env:LOCALAPPDATA 'mod.io\03852'), (Join-Path $env:LOCALAPPDATA 'mod.io\globalsettings.json'), (Join-Path $env:PUBLIC 'mod.io\3852'))

# GameMods/BotMods: fixture folders next to MultiplayerMod (the AMP build under test, always present on both sides); "Fixture>Folder" stages a
# fixture under another folder name. GameSetup/BotSetup: script blocks run on the staged folder (fixture variants). GameArgs: extra game arguments.
# Relaunches: how often the runner starts the game again after it closed for a restart (the Quest flow: the player starts it, not AMP).
# RestoreInGame: the swap is undone by the game itself when it quits (Quest), not by the helper.
# BotArgs: the bot's connection arguments instead of --host/--join. {RUN} in BotArgs and in script strings is the run folder.
# A synthetic mod big enough to cut a transfer off at a given share: manifest.json + $Files random files of $MB megabytes
function New-BigMod([string]$Root, [string]$Name = 'AmpE2EBig', [int]$Files = 3, [int]$MB = 16) {
    $dir = Join-Path $Root $Name
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Set-Content -LiteralPath (Join-Path $dir 'manifest.json') -Encoding ascii -Value ('{"Name": "' + $Name + '", "Description": "AMP e2e test mod (random data)", "Author": "AMP e2e", "ModVersion": "1.0", "GameVersion": "1.0"}')
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    $buffer = New-Object byte[] ($MB * 1MB)
    for ($i = 1; $i -le $Files; $i++) { $rng.GetBytes($buffer); [IO.File]::WriteAllBytes((Join-Path $dir "data$i.bin"), $buffer) }
}

# The bot host cuts the game's download off (Expect.dropAtPercent/dropHow): the setup must end with a clear error, Mods unchanged,
# and joining again must download the rest and swap normally
function New-DropScenario([int]$Percent, [string]$How) {
    @{
        GameMods = $Base; BotMods = $Base + $Dummy; BotMode = 'host'; BotSetup = { param($mods) New-BigMod $mods }; WaitRestore = $true; CachedFolder = 'AmpE2EBig'
        Script   = @(@{ step = 'waitSpawn' }, @{ step = 'join'; address = '127.0.0.1'; port = $Port }, @{ step = 'expectModSync'; blocks = $true },
                     @{ step = 'expectDownloadFails'; mode = 'Temporary'; contains = 'connection to the host was lost' }, @{ step = 'expectModFolder'; folder = 'AmpE2EBig'; present = $false },
                     @{ step = 'wait'; seconds = 10 }, @{ step = 'join'; address = '127.0.0.1'; port = $Port }, @{ step = 'expectModSync'; blocks = $true },
                     @{ step = 'downloadAndRestart'; mode = 'Temporary'; timeout = 300 }, @{ step = 'expectRejoined'; timeout = 300 },
                     @{ step = 'expectModFolder'; folder = 'AmpE2EBig'; present = $true }, @{ step = 'quit' })
        Expect   = @{ reports = @('needs_setup', 'needs_setup', 'ready'); served = @('AmpE2EBig'); greetings = 1; dropAtPercent = $Percent; dropHow = $How; lingerSec = 10 }
    }
}

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
        Expect   = @{ greeted = $true; quietBeforeGreeting = $true; play = $true; playItem = 'SwordShortCommon'; equipment = @(); seePlayers = 1; seePlayerMoves = 3; seeItems = @('DaggerCommon'); seeItemMoves = 3; broughtToHost = $true; lingerSec = 15 }
    }
    'host-imbue' = @{
        # The bot imbues its own sword with Fire, energy 50: the host's game shows exactly that (it was scaled by the receiving weapon's
        # imbue rate: 100), still 5 s later, and doesn't send the imbue back (audit finding 2)
        GameMods = $Base + $Dummy; BotMods = $Base + $Dummy; BotMode = 'join'
        Script   = @(@{ step = 'waitSpawn' }, @{ step = 'host'; port = $Port; maxPlayers = 4 }, @{ step = 'waitPlayers'; count = 2; timeout = 300 },
                     @{ step = 'expectItemSynced'; dataId = 'SwordShortCommon' }, @{ step = 'expectImbue'; dataId = 'SwordShortCommon'; spell = 'Fire'; minEnergy = 49; maxEnergy = 51; holdSeconds = 5 },
                     @{ step = 'wait'; seconds = 3 }, @{ step = 'quit' })
        Expect   = @{ greeted = $true; play = $true; playItem = 'SwordShortCommon'; imbueSpell = 'Fire'; imbueAmount = 50; lingerSec = 15 }
    }
    'host-level-change' = @{
        # The host loads another level while the bot plays (audit finding 1): the player who starts a level change got no clear from the
        # server and kept every old item/NPC entry. After the load the host's sync data must match the server's.
        GameMods = $Base + $Dummy; BotMods = $Base + $Dummy; BotMode = 'join'
        Script   = @(@{ step = 'waitSpawn' }, @{ step = 'host'; port = $Port; maxPlayers = 4 }, @{ step = 'waitPlayers'; count = 2; timeout = 300 },
                     @{ step = 'expectItemSynced'; dataId = 'SwordShortCommon' }, @{ step = 'spawnItem'; dataId = 'DaggerCommon' }, @{ step = 'expectSyncConsistent' },
                     @{ step = 'loadLevel'; level = 'Arena'; mode = 'Sandbox'; timeout = 300 }, @{ step = 'wait'; seconds = 10 }, @{ step = 'expectSyncConsistent' },
                     @{ step = 'quit' })
        Expect   = @{ greeted = $true; play = $true; playItem = 'SwordShortCommon'; seeItems = @('DaggerCommon'); lingerSec = 90 }
    }
    'host-holder-leaves' = @{
        # The bot puts its sword on its back, then leaves (audit finding 4): the sword was destroyed with the avatar on every client and stayed
        # "held" by nobody on the server. It must stay in the host's game, free, also on the server.
        GameMods = $Base + $Dummy; BotMods = $Base + $Dummy; BotMode = 'join'
        Script   = @(@{ step = 'waitSpawn' }, @{ step = 'host'; port = $Port; maxPlayers = 4 }, @{ step = 'waitPlayers'; count = 2; timeout = 300 },
                     @{ step = 'expectItemSynced'; dataId = 'SwordShortCommon' }, @{ step = 'expectItemHeld'; dataId = 'SwordShortCommon' },
                     @{ step = 'expectItemFree'; dataId = 'SwordShortCommon'; holdSeconds = 5 }, @{ step = 'quit' })
        ExpectLog = @('Player notice: AmpBot left')
        Expect   = @{ greeted = $true; play = $true; playItem = 'SwordShortCommon'; snapSlot = 'BackRight'; lingerSec = 1 }
    }
    'host-map-change-off' = @{
        # Map changes off: a player who walks into another level is sent back to the host's level instead of kicked (audit QOL 1)
        GameMods = $Base + $Dummy; BotMods = $Base + $Dummy; BotMode = 'join'
        Script   = @(@{ step = 'waitSpawn' }, @{ step = 'host'; port = $Port; maxPlayers = 4 }, @{ step = 'setOption'; mod = 'MultiplayerMod'; option = 'Players Change Map'; title = 'Disabled' },
                     @{ step = 'waitPlayers'; count = 2; timeout = 300 }, @{ step = 'wait'; seconds = 15 },
                     # the host itself still changes the map (it was told "Only the host changes the map" before)
                     @{ step = 'loadLevel'; level = 'Arena'; mode = 'Sandbox'; timeout = 300 }, @{ step = 'wait'; seconds = 10 }, @{ step = 'expectLevel'; level = 'Arena' }, @{ step = 'quit' })
        Expect   = @{ greeted = $true; wanderTo = 'Arena'; lingerSec = 2 }
    }
    'join-npc-health' = @{
        # The bot host's NPC is already damaged (20 of 100): the joining game must spawn it at 20, not full health (audit 5), and spawning it
        # must not change the game's shared HumanMale catalog entry (audit 13)
        GameMods = $Base; BotMods = $Base; BotMode = 'host'
        Script   = @(@{ step = 'waitSpawn' }, @{ step = 'join'; address = '127.0.0.1'; port = $Port }, @{ step = 'expectModSync'; blocks = $false },
                     @{ step = 'expectCreatureHealth'; type = 'HumanMale'; health = 20 }, @{ step = 'wait'; seconds = 3 }, @{ step = 'quit' })
        Expect   = @{ reports = @('ready'); greetings = 1; spawnCreature = 'HumanMale'; creatureHealth = 20; creatureMaxHealth = 100; lingerSec = 10 }
    }
    'host-joincode' = @{
        # A friend on the same Wi-Fi: the game hosts, writes its Same Wi-Fi code (this PC's LAN address), the bot joins with that code.
        # Test runs never talk to the real router: the port mapping must say Off.
        GameMods = $Base + $Dummy; BotMods = $Base + $Dummy; BotMode = 'join'; BotArgs = '--join-code-file {RUN}\joincode.txt'
        Script   = @(@{ step = 'waitSpawn' }, @{ step = 'host'; port = $Port; maxPlayers = 4 }, @{ step = 'expectPortMapping'; state = 'Off' },
                     @{ step = 'writeJoinCode'; file = '{RUN}\joincode.txt'; kind = 'lan' }, @{ step = 'waitPlayers'; count = 2; timeout = 300 }, @{ step = 'wait'; seconds = 5 }, @{ step = 'quit' })
        Expect   = @{ planBlocks = $false; greeted = $true }
    }
    'host-upnp-fake' = @{
        # The full UPnP flow against the bot's fake router (-amp-test-ssdp, honoured only in test runs): SSDP, description, external address,
        # TCP+UDP mapped with lease 7200, the Internet code is the fake's internet address (203.0.113.7), the bot joins with the Same Wi-Fi code
        # (127.0.0.1: this PC as the router sees it), both mappings removed on quit
        GameMods = $Base + $Dummy; BotMods = $Base + $Dummy; BotMode = 'join'; BotArgs = "--join-code-file {RUN}\joincode.txt --fake-igd $SsdpPort"
        GameArgs = @('-amp-test-ssdp', "127.0.0.1:$SsdpPort")
        Skip     = { if ($Port -ne 26951) { "the pinned code is for port 26951 (this run uses $Port)" } }
        Script   = @(@{ step = 'waitSpawn' }, @{ step = 'host'; port = $Port; maxPlayers = 4 }, @{ step = 'expectPortMapping'; state = 'Open'; internetCode = $CodeFake },
                     @{ step = 'writeJoinCode'; file = '{RUN}\joincode.txt'; kind = 'lan' }, @{ step = 'waitPlayers'; count = 2; timeout = 300 }, @{ step = 'wait'; seconds = 5 }, @{ step = 'quit' })
        Expect   = @{ planBlocks = $false; greeted = $true; lingerSec = 2 }
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
    'join-code' = @{
        # The game joins by typing a join code (no port): the same parsing as the book's "Join Code / IP" page
        GameMods = $Base + $Dummy; BotMods = $Base + $Dummy; BotMode = 'host'
        Skip     = { if ($Port -ne 26951) { "the pinned code is for port 26951 (this run uses $Port)" } }
        Script   = @(@{ step = 'waitSpawn' }, @{ step = 'join'; address = $Code127 }, @{ step = 'expectModSync'; blocks = $false }, @{ step = 'wait'; seconds = 3 }, @{ step = 'quit' })
        Expect   = @{ reports = @('ready'); greetings = 1; lingerSec = 10 }
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
    'host-realmods' = @{
        # The player's real mod set (copies): the game hosts with all of it, the bot joins with nothing and downloads every enabled mod
        # through AMP's transfer (the host's default upload limit applies). Measures the host's scan, the transfer and the game's memory.
        Extended = $true; TimeoutMin = 60; Purge = $true
        GameMods = @($Real); BotMods = @(); BotMode = 'join'
        Script   = @(@{ step = 'waitSpawn' }, @{ step = 'host'; port = $Port; maxPlayers = 4 }, @{ step = 'waitPlayers'; count = 2; timeout = 3300 }, @{ step = 'wait'; seconds = 30 }, @{ step = 'quit' })
        Expect   = @{ planBlocks = $true; download = $true; downloaded = @($Real); greeted = $true }
    }
    'join-realmods' = @{
        # The reverse: the bot hosts the real set, the game joins with nothing, downloads everything (mod.io first where mod.io has the
        # host's version, the rest from the host), swaps Temporary, restarts, rejoins with everything; after quitting Mods is the staged set again
        Extended = $true; TimeoutMin = 90; Purge = $true; WaitRestore = $true; CachedFolder = "Kashimo's Technique"
        GameMods = @(); BotMods = @($Real); BotMode = 'host'
        # The bot hashes its 2 GB before it listens (about 17 s): the game waits a bit before joining
        Script   = @(@{ step = 'waitSpawn' }, @{ step = 'wait'; seconds = 40 }, @{ step = 'join'; address = '127.0.0.1'; port = $Port }, @{ step = 'expectModSync'; blocks = $true },
                     @{ step = 'downloadAndRestart'; mode = 'Temporary'; timeout = 3600 }, @{ step = 'expectRejoined'; timeout = 900 },
                     @{ step = 'expectModFolder'; folder = "Kashimo's Technique"; present = $true }, @{ step = 'expectModFolder'; folder = 'WaterSpell'; present = $true },
                     @{ step = 'expectModFolder'; folder = 'AT_Destiny Megapack'; present = $true }, @{ step = 'expectProfileActive'; active = $true }, @{ step = 'wait'; seconds = 30 }, @{ step = 'quit' })
        Expect   = @{ reports = @('needs_setup', 'ready'); greetings = 1; lingerSec = 10 }
    }
    'soak' = @{
        # 60+ minutes of hosting: the bot plays (avatar, walking, its sword) and leaves + rejoins every 10 minutes; the game spawns a dagger and
        # switches PvP on and off every minute. The fake router gives a 120 s lease (-amp-test-lease, test runs only): about 60 renewals,
        # each logged by the bot, and the delete on quit. perf.csv has the game's memory and handles every 30 s.
        Extended = $true; TimeoutMin = 85; PerfEverySec = 30
        GameMods = $Base + $Dummy; BotMods = $Base + $Dummy; BotMode = 'join'
        BotArgs  = "--join 127.0.0.1 $Port --soak 62 --rejoin-every 600 --fake-igd $SsdpPort --fake-igd-lease 120"
        GameArgs = @('-amp-test-ssdp', "127.0.0.1:$SsdpPort", '-amp-test-lease', '120')
        Script   = @(@{ step = 'waitSpawn' }, @{ step = 'host'; port = $Port; maxPlayers = 4 }, @{ step = 'expectPortMapping'; state = 'Open' }) +
                   @(1..70 | ForEach-Object { @{ step = 'spawnItem'; dataId = 'DaggerCommon' }, @{ step = 'wait'; seconds = 15 },
                                               @{ step = 'setOption'; mod = 'MultiplayerMod'; option = 'PvP'; title = 'Enabled' }, @{ step = 'wait'; seconds = 15 },
                                               @{ step = 'setOption'; mod = 'MultiplayerMod'; option = 'PvP'; title = 'Disabled' }, @{ step = 'wait'; seconds = 25 } }) +
                   @(@{ step = 'quit' })
        Expect   = @{ greeted = $true; play = $true; playItem = 'SwordShortCommon'; seePlayers = 1; seePlayerMoves = 3; seeItems = @('DaggerCommon');
                      settingChanged = @(@{ mod = 'MultiplayerMod'; option = 'PvP'; title = 'Disabled' }); lingerSec = 0 }
    }
    # The host's connection is reset (like a lost network) at 30/60/95% of the download; the host quits at 50% (and hosts again 5 s later)
    'join-drop-30'    = New-DropScenario 30 'reset'
    'join-drop-60'    = New-DropScenario 60 'reset'
    'join-drop-95'    = New-DropScenario 95 'reset'
    'join-host-quits' = New-DropScenario 50 'quit'
    'join-badnet' = @{
        # A bad internet line (AmpBot's proxy: 75 ms each way = 150 ms round trip, 2% loss, a 5 s stall every 45 s) between the game and the
        # bot host: the game downloads 49 MB through it, swaps, restarts, rejoins through it and plays (avatars, walking, items both ways)
        GameMods = $Base; BotMods = $Base + $Dummy; BotMode = 'host'; BotSetup = { param($mods) New-BigMod $mods }; WaitRestore = $true; CachedFolder = 'AmpE2EBig'
        BotArgs  = "--host $Port --proxy-port $($Port + 20) --latency-ms 75 --loss 0.02 --stall-every 45 --stall-ms 5000"
        Script   = @(@{ step = 'waitSpawn' }, @{ step = 'join'; address = '127.0.0.1'; port = $Port + 20 }, @{ step = 'expectModSync'; blocks = $true },
                     @{ step = 'downloadAndRestart'; mode = 'Temporary'; timeout = 900 }, @{ step = 'expectRejoined'; timeout = 300 },
                     @{ step = 'expectModFolder'; folder = 'AmpE2EBig'; present = $true },
                     @{ step = 'expectRemotePlayers'; count = 1 }, @{ step = 'expectRemotePlayerMoved'; minDistance = 1 }, @{ step = 'expectItemSynced'; dataId = 'SwordShortCommon' },
                     @{ step = 'spawnItem'; dataId = 'DaggerCommon' }, @{ step = 'wait'; seconds = 20 }, @{ step = 'quit' })
        Expect   = @{ reports = @('needs_setup', 'ready'); served = @('AmpE2EBig'); greetings = 1; play = $true; playItem = 'SwordShortCommon'; seePlayers = 1; seePlayerMoves = 3
                      seeItems = @('DaggerCommon'); seeItemMoves = 3; lingerSec = 20 }
    }
    'host-badnet' = @{
        # The same line between the game host and the bot joiner: the bot downloads 49 MB through it (ack window over 150 ms), then both play
        GameMods = $Base + $Dummy; GameSetup = { param($mods) New-BigMod $mods }; BotMods = $Base + $Dummy; BotMode = 'join'
        BotArgs  = "--join 127.0.0.1 $Port --proxy-port $($Port + 20) --latency-ms 75 --loss 0.02 --stall-every 45 --stall-ms 5000"
        Script   = @(@{ step = 'waitSpawn' }, @{ step = 'host'; port = $Port; maxPlayers = 4 }, @{ step = 'waitPlayers'; count = 2; timeout = 900 },
                     @{ step = 'expectRemotePlayers'; count = 1 }, @{ step = 'expectRemotePlayerMoved'; minDistance = 1 }, @{ step = 'expectItemSynced'; dataId = 'SwordShortCommon' },
                     @{ step = 'spawnItem'; dataId = 'DaggerCommon' }, @{ step = 'wait'; seconds = 20 }, @{ step = 'quit' })
        Expect   = @{ planBlocks = $true; download = $true; downloaded = @('AmpE2EBig'); greeted = $true; play = $true; playItem = 'SwordShortCommon'; equipment = @()
                      seePlayers = 1; seePlayerMoves = 3; seeItems = @('DaggerCommon'); seeItemMoves = 3; lingerSec = 20 }
    }
    'host-fuzz' = @{
        # A hostile joiner (AmpBot --fuzz) sends malformed, oversized, out-of-order and duplicate MOD_SYNC/MOD_FILE and gameplay packets and
        # raw garbage frames, reconnecting when dropped; then a normal joiner must still get in. CheckWrites: nothing new or changed in the
        # install outside the Mods folder and the mod cache.
        GameMods = $Base + $Dummy; BotMods = $Base + $Dummy; BotMode = 'join'; BotArgs = "--join 127.0.0.1 $Port --fuzz"; CheckWrites = $true; NoNaN = $true
        # The bot stays after its normal join (lingerSec) until the game quits
        Script   = @(@{ step = 'waitSpawn' }, @{ step = 'host'; port = $Port; maxPlayers = 4 }, @{ step = 'wait'; seconds = 100; timeout = 200 },
                     @{ step = 'waitPlayers'; count = 2; timeout = 300 }, @{ step = 'wait'; seconds = 10 }, @{ step = 'quit' })
        Expect   = @{ planBlocks = $false; greeted = $true; lingerSec = 100 }
    }
    'host-owner-race' = @{
        # Two players (two bot connections, same mods) grab the same item at the same moment, 8 rounds x 300 grabs each: after every round
        # exactly one of them may believe it owns the item (audit finding 6: owner updates raced across the server's network threads)
        GameMods = $Base + $Dummy; BotMods = $Base + $Dummy; BotMode = 'join'; BotArgs = "--join 127.0.0.1 $Port --owner-race"
        Script   = @(@{ step = 'waitSpawn' }, @{ step = 'host'; port = $Port; maxPlayers = 4 }, @{ step = 'waitPlayers'; count = 3; timeout = 300 },
                     @{ step = 'wait'; seconds = 60 }, @{ step = 'quit' })
        Expect   = @{ greeted = $false }
    }
    'host-slow-download' = @{
        # The host's setup limit (shortened to 8 s with -amp-test-setup-timeout) must not kick a joiner whose download (64 MB at the host's
        # 4 MB/s upload limit, about 16 s) is still moving
        GameMods = $Base + $Dummy; GameSetup = { param($mods) New-BigMod $mods -Files 4 }; BotMods = $Base + $Dummy; BotMode = 'join'
        GameArgs = @('-amp-test-setup-timeout', '8')
        Script   = @(@{ step = 'waitSpawn' }, @{ step = 'host'; port = $Port; maxPlayers = 4 }, @{ step = 'waitPlayers'; count = 2; timeout = 300 }, @{ step = 'wait'; seconds = 5 }, @{ step = 'quit' })
        Expect   = @{ planBlocks = $true; download = $true; downloaded = @('AmpE2EBig'); greeted = $true }
    }
    'join-killed-swapped' = @{
        # PC: the game is killed while the host's mods are swapped in (no quit, so no restore helper). The next start (the runner starts it,
        # like the player) says the host's mods are still installed; "Restore my mods" restores through the helper, which relaunches the game
        GameMods = $Base; BotMods = $Base + $Dummy; BotMode = 'host'; Relaunches = 1; WaitRestore = $true
        Script   = @(@{ step = 'waitSpawn' }, @{ step = 'join'; address = '127.0.0.1'; port = $Port }, @{ step = 'expectModSync'; blocks = $true },
                     @{ step = 'downloadAndRestart'; mode = 'Temporary'; timeout = 300 }, @{ step = 'expectRejoined'; timeout = 300 },
                     @{ step = 'expectModFolder'; folder = $Dummy; present = $true }, @{ step = 'killGame' },
                     @{ step = 'waitSpawn' }, @{ step = 'expectLeftOverSwap' }, @{ step = 'expectModFolder'; folder = $Dummy; present = $true }, @{ step = 'restoreMods' },
                     @{ step = 'waitSpawn' }, @{ step = 'expectModFolder'; folder = $Dummy; present = $false }, @{ step = 'quit' })
        Expect   = @{ reports = @('needs_setup', 'ready'); served = @($Dummy); greetings = 1; lingerSec = 10 }
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

# Every file of the game install except the Mods folder and AMP's mod cache: name, size, write time (CheckWrites)
function Get-InstallShape {
    # StreamingAssets\Logs: the game's own Player.log / Player-prev.log, written on every start
    $skip = @((Join-Path $Game 'BladeAndSorcery_Data\StreamingAssets\Mods\'), (Join-Path $Game 'BladeAndSorcery_Data\StreamingAssets\AMP_ModCache\'), (Join-Path $Game 'BladeAndSorcery_Data\StreamingAssets\Logs\'))
    Get-ChildItem -LiteralPath $Game -Recurse -File -Force -ErrorAction SilentlyContinue | Where-Object { $f = $_.FullName; -not ($skip | Where-Object { $f.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) }) } |
        ForEach-Object { '{0}|{1}|{2}' -f $_.FullName.Substring($Game.Length + 1), $_.Length, $_.LastWriteTimeUtc.Ticks }
}

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
        if ($n -eq $Real) {
            if (-not $script:RealSrc) { throw 'the real Mods folder is not set aside (no source for *REAL*)' }
            Get-ChildItem -LiteralPath $script:RealSrc -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $Target $_.Name) -Recurse }
            continue
        }
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
    (ConvertTo-Json -InputObject @($s.Script) -Depth 5).Replace('{RUN}', $run.Replace('\', '\\')) | Set-Content -LiteralPath $script -Encoding ascii
    $timeoutMin = if ($s.TimeoutMin) { $s.TimeoutMin } else { $TimeoutMin }
    $botMods = Join-Path $run 'bot-mods'
    Note "run folder $run"

    $games = New-Object System.Collections.Generic.List[object]   # @{ proc; log; redirected } in start order
    $bot = $null; $t0 = $null; $staged = $null; $events = New-Object System.Collections.Generic.List[string]
    $safetyProblem = $null
    $perf = Join-Path $run 'perf.csv'; $nextPerf = Get-Date
    & 'C:\Projects\bns-mp\gametest\mods-aside.ps1' | ForEach-Object { Note $_ }
    try {
        $script:RealSrc = Join-Path (@(Get-UnrestoredAside))[0].FullName 'Mods'
        $x = @{ timeoutSec = 60 * $timeoutMin - 60 } + $s.Expect
        if ($x.downloaded -contains $Real) {
            $x.downloaded = @(Get-ChildItem -LiteralPath $script:RealSrc -Directory | Where-Object { -not $_.Name.StartsWith('_') -and $_.Name -ne 'HJ_NexusModManager' -and (Test-Path -LiteralPath (Join-Path $_.FullName 'manifest.json')) } | ForEach-Object Name)
            Note "expecting $($x.downloaded.Count) real mods to be downloaded"
        }
        ConvertTo-Json -InputObject $x -Depth 5 | Set-Content -LiteralPath $expect -Encoding ascii
        if ($s.BotMode -ne 'none') { Copy-ModSet $botMods $s.BotMods }
        if ($s.BotExtraFile) { Set-Content -LiteralPath (Join-Path $botMods $s.BotExtraFile) -Encoding ascii -Value 'AMP e2e: this copy differs from the mod.io file' }
        if ($s.BotSetup) { & $s.BotSetup $botMods }
        Copy-ModSet $ModsDir $s.GameMods $(if ($s.AmpBuild) { $s.AmpBuild } else { $BuildDir })
        if ($s.GameSetup) { & $s.GameSetup $ModsDir }
        $staged = @(Get-ModsShape $ModsDir)
        $installBefore = if ($s.CheckWrites) { @(Get-InstallShape) } else { $null }

        $botArgs = if ($s.BotArgs) { $s.BotArgs.Replace('{RUN}', $run) } elseif ($s.BotMode -eq 'host') { "--host $Port" } else { "--join 127.0.0.1 $Port" }
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

        $deadline = $t0.AddMinutes($timeoutMin)
        'seconds,pid,workingSetMB,privateMB,handles,threads,botWorkingSetMB,commitGB' | Set-Content -LiteralPath $perf -Encoding ascii
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
            if ((Get-Date) -ge $nextPerf -and -not $g.proc.HasExited) {
                $nextPerf = (Get-Date).AddSeconds($(if ($s.PerfEverySec) { $s.PerfEverySec } else { 10 }))
                try {
                    $os = Get-CimInstance Win32_OperatingSystem
                    $botWs = if ($bot -and -not $bot.HasExited) { $bot.Refresh(); [int]($bot.WorkingSet64 / 1MB) } else { '' }
                    '{0},{1},{2},{3},{4},{5},{6},{7:0.0}' -f [int]((Get-Date) - $t0).TotalSeconds, $g.proc.Id, [int]($g.proc.WorkingSet64 / 1MB), [int]($g.proc.PrivateMemorySize64 / 1MB),
                        $g.proc.HandleCount, $g.proc.Threads.Count, $botWs, (($os.TotalVirtualMemorySize - $os.FreeVirtualMemory) / 1MB) | Add-Content -LiteralPath $perf -Encoding ascii
                } catch { }
            }
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
        if (-not $end -and -not $result.reason -and -not $safetyProblem) { $result.reason = "timed out after $timeoutMin min" }

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
                if ((($games | ForEach-Object { $_.log }) -join "`n") -match 'Mod swap helper: helper start: restore[^\r\n]*\| done') { $restored = $true; break }
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
            # A game start after the helper reads helper.log into its own log and deletes the file
            if (-not $restored -and (($games | ForEach-Object { $_.log }) -join "`n") -match 'Mod swap helper: helper start: restore[^\r\n]*\| done') { $restored = $true }
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

        if ($s.CheckWrites) {
            $writes = @(Compare-Object $installBefore @(Get-InstallShape) | Where-Object { $_.SideIndicator -eq '=>' } | ForEach-Object { $_.InputObject })
            $events.Add("install: $($writes.Count) new or changed file(s) outside Mods and AMP_ModCache" + $(if ($writes) { ': ' + ($writes -join '; ') } else { '' }))
            if ($writes -and -not $result.reason) { $result.reason = "the game wrote into the install outside Mods/AMP_ModCache: $($writes[0])" }
        }
        $allLog = ($games | ForEach-Object { $_.log }) -join "`n"
        $exceptions = @([regex]::Matches($allLog, '(?m)^.*Exception.*$') | ForEach-Object { $_.Value })
        $events.Add("game log: $($exceptions.Count) exception line(s), $(@($exceptions | Where-Object { $_ -match 'AMP' }).Count) mentioning AMP")
        foreach ($pattern in @($s.ExpectLog)) {
            if ($pattern -and $allLog -notmatch $pattern -and -not $result.reason) { $result.reason = "the game log has no line matching '$pattern'" }
        }
        $nan = [regex]::Matches($allLog, 'Infinity or NaN|infinite value').Count
        if ($s.NoNaN -and $nan -gt 0 -and -not $result.reason) { $result.reason = "the game log has $nan NaN/infinity errors (bad numbers from the network reached the game)" }
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
        # Copies of the real mods are large: once the run is over only the logs stay (they are copies, the real Mods are back in place)
        $runBytes = (Get-ChildItem -LiteralPath $run -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
        if ($s.Purge -and $runBytes -gt 1GB) {
            foreach ($big in 'bot-mods', 'bot-cache', 'mods-after', 'AMP_ModCache', 'docs\modio') {
                $p = Join-Path $run $big
                if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force -ErrorAction SilentlyContinue }
            }
            Note ("removed the mod copies of this run ({0:N1} GB), kept the logs" -f ($runBytes / 1GB))
        }
    }
    if ($safetyProblem) { throw "SAFETY: $safetyProblem" }
    $result.seconds = [int]((Get-Date) - $started).TotalSeconds
    Note ("E2E {0} {1}{2}" -f $Name, $result.status, $(if ($result.reason) { " - $($result.reason)" } else { '' }))
    [pscustomobject]$result
}

if (-not $All -and -not $Scenario) { throw "use -Scenario <$($Scenarios.Keys -join '|')> or -All" }
if (-not $NoBuild) { Build-All; if ($All -or $Scenario -eq 'self-update') { Build-OldAmp } }
$names = if ($All) { @($Scenarios.Keys | Where-Object { $Extended -or -not $Scenarios[$_].Extended }) } else { @($Scenario) }
$results = foreach ($n in $names) { Invoke-Scenario $n }
$results | Format-Table scenario, status, seconds, reason -AutoSize | Out-String -Width 250 | Write-Host
$results | ForEach-Object { Write-Host "$($_.scenario) timings: $($_.timings)" }
if ($results | Where-Object { $_.status -notin 'PASS', 'SKIP' }) { exit 1 }
