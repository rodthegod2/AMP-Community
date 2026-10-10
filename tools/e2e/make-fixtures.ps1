# Creates tools\e2e\fixtures (git-ignored): read-only copies of a few small installed mods plus the synthetic AmpE2EDummy.
# Run it while the real Mods folder is in place (not during a test run). Existing fixtures are kept unless -Force.
param([switch]$Force)
$ErrorActionPreference = 'Stop'
. 'C:\Projects\bns-mp\gametest\mods-common.ps1'

$Fixtures = Join-Path $PSScriptRoot 'fixtures'
$Copied = 'InstantSloMo', '_TrueZeroEffort', 'Fascia'   # JSON mods, a disabled one ("_") and one with asset bundles

if ((Test-Path -LiteralPath $Fixtures) -and -not $Force) { "fixtures exist: $Fixtures"; return }
if (@(Get-UnrestoredAside)) { throw 'the real Mods folder is set aside (a run is active or was interrupted) - restore it first' }
if (Test-Path -LiteralPath $Fixtures) { Remove-Item -LiteralPath $Fixtures -Recurse -Force }   # our own generated copies only
New-Item -ItemType Directory -Path $Fixtures | Out-Null

foreach ($name in $Copied) {
    $src = Join-Path $ModsDir $name
    if (-not (Test-Path -LiteralPath (Join-Path $src 'manifest.json'))) { throw "installed mod not found: $src" }
    Copy-Item -LiteralPath $src -Destination (Join-Path $Fixtures $name) -Recurse
}

# Synthetic mod: fixed content (seeded random) so its hash never changes between runs
$dummy = Join-Path $Fixtures 'AmpE2EDummy'
New-Item -ItemType Directory -Path $dummy | Out-Null
Set-Content -LiteralPath (Join-Path $dummy 'manifest.json') -Encoding ascii -Value '{ "Name": "AMP E2E Dummy", "Description": "Synthetic mod for AMP end-to-end tests", "Author": "amp-e2e", "ModVersion": "1.0", "GameVersion": "1.0.0.0" }'
$bytes = New-Object byte[] (1MB)
(New-Object System.Random 4242).NextBytes($bytes)
[IO.File]::WriteAllBytes((Join-Path $dummy 'data.bin'), $bytes)
[IO.File]::WriteAllBytes((Join-Path $dummy 'empty.bin'), [byte[]]@())

Get-ChildItem -LiteralPath $Fixtures | ForEach-Object { '{0}  {1:N0} bytes' -f $_.Name, ((Get-ChildItem -LiteralPath $_.FullName -Recurse -File | Measure-Object Length -Sum).Sum) }
