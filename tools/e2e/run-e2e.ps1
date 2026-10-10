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
$PvP       = @{ mod = 'MultiplayerMod'; option = 'PvP' }

# GameMods/BotMods: fixture folders next to MultiplayerMod (the AMP build under test, always present on both sides)
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
    'join-download-temporary' = @{
        GameMods = $Base; BotMods = $Base + $Dummy; BotMode = 'host'; WaitRestore = $true
        Script   = @(@{ step = 'waitSpawn' }, @{ step = 'join'; address = '127.0.0.1'; port = $Port }, @{ step = 'expectModSync'; blocks = $true },
                     @{ step = 'downloadAndRestart'; mode = 'Temporary'; timeout = 300 }, @{ step = 'expectRejoined'; timeout = 300 },
                     @{ step = 'expectModFolder'; folder = $Dummy; present = $true }, @{ step = 'expectProfileActive'; active = $true }, @{ step = 'quit' })
        Expect   = @{ reports = @('needs_setup', 'ready'); served = @($Dummy); greetings = 1; lingerSec = 10 }
    }
}

function Note([string]$text) { Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $text) }

# Unity keeps its log open; read it without locking it
function Read-Shared([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return '' }
    try {
        $fs = [IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite, Delete')
        try { return (New-Object IO.StreamReader($fs)).ReadToEnd() } finally { $fs.Dispose() }
    } catch { return $null }
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

function Copy-ModSet([string]$Target, [string[]]$Names) {
    New-Item -ItemType Directory -Force -Path $Target | Out-Null
    $amp = Join-Path $Target 'MultiplayerMod'
    New-Item -ItemType Directory -Force -Path $amp | Out-Null
    Copy-Item -Path "$BuildDir\AMP.dll", "$BuildDir\AMP.pdb", "$BuildDir\Netamite*.dll", "$BuildDir\Newtonsoft.Json.dll" -Destination $amp
    Copy-Item -LiteralPath (Join-Path $Repo 'BS Files\manifest.json') -Destination $amp
    foreach ($n in $Names) { Copy-Item -LiteralPath (Join-Path $Fixtures $n) -Destination (Join-Path $Target $n) -Recurse }
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

    # Preconditions: nothing may be running or left over from an earlier run
    if (Get-Process BladeAndSorcery -ErrorAction SilentlyContinue) { throw 'a BladeAndSorcery process is already running' }
    if (Test-Path -LiteralPath $CacheDir) { throw "$CacheDir exists before the run (left over?) - inspect it" }
    if (-not (Test-Path -LiteralPath (Join-Path $Fixtures $Dummy))) { & (Join-Path $PSScriptRoot 'make-fixtures.ps1') | Out-Null }
    if (-not (Get-Process steam -ErrorAction SilentlyContinue)) { throw 'Steam is not running' }
    Assert-Isolation $Game | ForEach-Object { Note $_ }
    Assert-RealSavesUnchanged | ForEach-Object { Note $_ }

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
    Copy-ModSet $botMods $s.BotMods
    Note "run folder $run"

    $games = New-Object System.Collections.Generic.List[object]   # @{ proc; log; redirected } in start order
    $bot = $null; $t0 = $null; $staged = $null; $events = New-Object System.Collections.Generic.List[string]
    $safetyProblem = $null
    & 'C:\Projects\bns-mp\gametest\mods-aside.ps1' | ForEach-Object { Note $_ }
    try {
        Copy-ModSet $ModsDir $s.GameMods
        $staged = @(Get-ModsShape $ModsDir)

        $botArgs = if ($s.BotMode -eq 'host') { "--host $Port" } else { "--join 127.0.0.1 $Port" }
        $bot = Start-Process -FilePath $BotExe -ArgumentList "$botArgs --mods $botMods --cache $(Join-Path $run 'bot-cache') --expect $expect" `
                             -RedirectStandardOutput (Join-Path $run 'bot.log') -RedirectStandardError (Join-Path $run 'bot.err') -PassThru -WindowStyle Hidden
        $null = $bot.Handle   # keeps ExitCode readable after the process ends
        Note "bot pid $($bot.Id) ($($s.BotMode))"

        $gameArgs = '--doorstop-enabled', 'true', '--doorstop-target-assembly', 'AmpTestBoot\AmpTestBoot.dll', '-amp-test-docs', $docs,
                    '-vrmode', 'mockhmd', '-skipintro', '-skipmodswarning', '-screen-fullscreen', '0', '-screen-width', '640', '-screen-height', '360',
                    '-force-d3d11', '-level', 'Home', '-charindex', '0', '-logFile', $log, '-amp-autopilot', $script
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
                elseif (((Get-Date) - $exitedAt).TotalSeconds -gt 180) { $result.reason = "game exited (code $($g.proc.ExitCode)) without AMPTEST DONE/FAIL and wasn't relaunched"; break }
            }
            if ($bot.HasExited -and $bot.ExitCode -ne 0) { $result.reason = 'bot failed before the game finished'; break }
        }
        if (-not $end -and -not $result.reason -and -not $safetyProblem) { $result.reason = "timed out after $TimeoutMin min" }

        # After DONE/FAIL the game quits by itself; otherwise (timeout, bot failure) it is stopped right away
        foreach ($g in $games) {
            if (-not $g.proc.WaitForExit($(if ($end) { 60000 } else { 0 }))) { Stop-Process -Id $g.proc.Id -Force; $g.proc.WaitForExit(30000) | Out-Null; $events.Add("stopped own game pid $($g.proc.Id)") }
        }
        if (-not $bot.WaitForExit(90000)) { Stop-Process -Id $bot.Id -Force; $events.Add("killed bot pid $($bot.Id)") }
        $botOut = Get-Content -LiteralPath (Join-Path $run 'bot.log') -ErrorAction SilentlyContinue
        $botEnd = $botOut | Where-Object { $_ -match '^BOT (DONE|FAIL)' } | Select-Object -Last 1
        Note "bot: $botEnd"

        if ($s.WaitRestore) {
            # Temporary swap: the restore helper starts when the game quits and puts the staged set back
            $helperLog = Join-Path $CacheDir 'helper\helper.log'
            $until = (Get-Date).AddSeconds(180); $restored = $false
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
            $cached = @(Get-ChildItem -Path (Join-Path $CacheDir 'mods\*\entry.json') -ErrorAction SilentlyContinue | Where-Object { (Get-Content -Raw $_.FullName | ConvertFrom-Json).folder -eq $Dummy })
            $events.Add("restore: helper finished=$restored, journal=$state, Mods back to the staged set=$modsBack, $Dummy in cache=$($cached.Count -gt 0)")
            if (-not ($restored -and ($state -eq 'gone' -or $state -eq 'Restored') -and $modsBack -and $cached.Count -gt 0)) {
                if (-not $modsBack) { Compare-Object $staged $shapeNow | Out-String | ForEach-Object { $events.Add("Mods diff: $_") } }
                if (-not $result.reason) { $result.reason = 'the Temporary swap was not undone correctly after quitting' }
            }
        }

        $gameOk = $end -like 'AMPTEST DONE*'
        $botOk = $bot.ExitCode -eq 0 -and $botEnd -like 'BOT DONE*'
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
        $events | ForEach-Object { Note "  $_" }
    }
    if ($safetyProblem) { throw "SAFETY: $safetyProblem" }
    $result.seconds = [int]((Get-Date) - $started).TotalSeconds
    Note ("E2E {0} {1}{2}" -f $Name, $result.status, $(if ($result.reason) { " - $($result.reason)" } else { '' }))
    [pscustomobject]$result
}

if (-not $All -and -not $Scenario) { throw "use -Scenario <$($Scenarios.Keys -join '|')> or -All" }
if (-not $NoBuild) { Build-All }
$names = if ($All) { @($Scenarios.Keys) } else { @($Scenario) }
$results = foreach ($n in $names) { Invoke-Scenario $n }
$results | Format-Table scenario, status, seconds, reason -AutoSize | Out-String -Width 250 | Write-Host
$results | ForEach-Object { Write-Host "$($_.scenario) timings: $($_.timings)" }
if ($results | Where-Object { $_.status -ne 'PASS' }) { exit 1 }
