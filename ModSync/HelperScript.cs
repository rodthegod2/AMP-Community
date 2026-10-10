using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AMP.ModSync {
    // The PC helper: runs after the game has exited, swaps the Mods folder via JournalApplier (loaded from a copy of AMP.dll), optionally relaunches through Steam.
    public static class HelperScript {
        public const string SteamAppId = "629730";

        public static string Script() {
            return
@"param([int]$GamePid, [string]$AmpDll, [string]$JsonDll, [string]$Journal, [string]$Mods, [string]$Cache, [string]$Action, [int]$Relaunch, [string]$Log, [string]$RelaunchSpec = '')
$ErrorActionPreference = 'Continue'
function Write-Log($text) { Add-Content -Path $Log -Encoding UTF8 -Value ((Get-Date).ToString('s') + ' ' + $text) }
Write-Log ""helper start: $Action pid=$GamePid""
if($GamePid -gt 0) {
    Wait-Process -Id $GamePid -Timeout 300 -ErrorAction SilentlyContinue
    if(Get-Process -Id $GamePid -ErrorAction SilentlyContinue) { Write-Log 'failed: game still running'; exit 1 }
}
Start-Sleep -Seconds 2
try {
    # The game's Newtonsoft.Json.dll claims a strong name but isn't signed: Windows PowerShell refuses to load it from a file (""Strong name validation failed""),
    # also when AMP.dll is loaded from the same folder and binds to it. Loading both from bytes skips that check; AMP's Newtonsoft reference is answered by hand.
    $json = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($JsonDll))
    [AppDomain]::CurrentDomain.add_AssemblyResolve([ResolveEventHandler]{ param($sender, $e) if($e.Name -like 'Newtonsoft.Json,*') { return $json }; return $null })
    [void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes($AmpDll))
    $result = [AMP.ModSync.JournalApplier]::RunFromHelper($Journal, $Mods, $Cache, $Action)
    if($result -eq 'busy') { Write-Log 'busy: another helper is working' }
    elseif($result) { Write-Log (""failed: "" + $result) } else { Write-Log 'done' }
    $note = [AMP.ModSync.JournalApplier]::LastNote
    if($note) { Write-Log (""note: "" + $note) }
} catch {
    Write-Log (""error: "" + $_.Exception.Message)
}
if($Relaunch -eq 1) {
    if($RelaunchSpec) {
        # End-to-end tests: the exact command line that restarts the test (RelaunchSpec = base64 of ""program`n arguments"")
        try {
            $parts = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($RelaunchSpec)) -split ""`n"", 2
            if($parts.Count -gt 1 -and $parts[1]) { Start-Process -FilePath $parts[0] -ArgumentList $parts[1] } else { Start-Process -FilePath $parts[0] }
            Write-Log 'relaunched (test command)'
        } catch { Write-Log (""error: relaunch failed: "" + $_.Exception.Message) }
    } else { Start-Process ('steam://rungameid/" + SteamAppId + @"'); Write-Log 'relaunched' }
}
";
        }

        // Command for a short-lived powershell that starts the helper through WMI, so the helper isn't part of the game's process tree/job
        public static string LauncherArguments(string scriptPath, int gamePid, string ampDll, string jsonDll, string journal, string mods, string cache, string action, bool relaunch, string log, string relaunchSpec = null) {
            string inner = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File " + Q(scriptPath)
                         + " -GamePid " + gamePid + " -AmpDll " + Q(ampDll) + " -JsonDll " + Q(jsonDll) + " -Journal " + Q(journal)
                         + " -Mods " + Q(mods) + " -Cache " + Q(cache) + " -Action " + action + " -Relaunch " + (relaunch ? 1 : 0) + " -Log " + Q(log)
                         + (relaunch && !string.IsNullOrEmpty(relaunchSpec) ? " -RelaunchSpec " + relaunchSpec : "");
            // The exit code tells the game whether Windows really created the helper (0 = yes)
            string command = "$ErrorActionPreference = 'Stop'; $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = '" + inner.Replace("'", "''") + "' }; exit ([int]$r.ReturnValue)";
            return "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        }

        // End-to-end tests: the game was started through Steam with test arguments, so the relaunch has to be too (steam://rungameid would lose them).
        // commandLine is Environment.GetCommandLineArgs(): argv[0] is dropped, and so is the "-vrmode openvr" Steam puts first (a later -vrmode still wins).
        public static string[] SteamRelaunchArguments(string appId, string[] commandLine) {
            var args = new List<string> { "-applaunch", appId };
            if(commandLine == null || commandLine.Length < 2) return args.ToArray();
            int start = 1;
            if(commandLine.Length >= 3 && commandLine[1] == "-vrmode" && commandLine[2] == "openvr") start = 3;
            args.AddRange(commandLine.Skip(start));
            return args.ToArray();
        }

        // What the helper script expects in -RelaunchSpec: base64 (no characters that need quoting) of "program, a line break, arguments"
        public static string RelaunchSpec(string program, string[] arguments) {
            return Convert.ToBase64String(Encoding.Unicode.GetBytes(program + "\n" + QuoteArguments(arguments)));
        }

        // One command line string, quoted the way CommandLineToArgvW reads it back
        public static string QuoteArguments(IEnumerable<string> arguments) {
            return string.Join(" ", arguments.Select(QuoteArgument));
        }

        private static string QuoteArgument(string argument) {
            if(argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return argument;
            var sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach(char c in argument) {
                if(c == '\\') { backslashes++; continue; }
                // Backslashes right before a quote are doubled, plus one that escapes the quote
                sb.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
                backslashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', backslashes * 2); // right before the closing quote
            return sb.Append('"').ToString();
        }

        private static string Q(string path) {
            if(path.Contains("\"")) throw new ArgumentException("Path contains a quote: " + path);
            // A trailing backslash would escape the closing quote
            return "\"" + path.Replace('/', '\\').TrimEnd('\\') + "\"";
        }
    }
}
