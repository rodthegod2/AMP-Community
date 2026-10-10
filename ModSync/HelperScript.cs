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

        private static string Q(string path) {
            if(path.Contains("\"")) throw new ArgumentException("Path contains a quote: " + path);
            return "\"" + path + "\"";
        }
    }
}
