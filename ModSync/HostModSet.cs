using System;
using System.Collections.Generic;
using System.Linq;

namespace AMP.ModSync {
    // "off": old behaviour (no mod list); "warn": show differences, joiners may still join; "enforce": joiners must match
    public static class ModSyncMode {
        public const string Off     = "off";
        public const string Warn    = "warn";
        public const string Enforce = "enforce";
    }

    public static class JoinerStatus {
        public const string Checking   = "checking"; // got the host's list, still comparing (a first scan of many mods can take minutes)
        public const string Ready      = "ready";
        public const string NeedsSetup = "needs_setup";
        public const string Declined   = "declined";
    }

    // Sent host -> joiner as JSON. Fields are only ever appended, so newer builds can still talk to older ones.
    public class HostModSet {
        public const int CurrentProtocol = 1;

        public int            protocol     = CurrentProtocol;
        public string         platform     = "";
        public string         ampBuild     = "";
        public string         mode         = ModSyncMode.Warn;
        public List<ModEntry> mods         = new List<ModEntry>();
        public string[]       personalMods = new string[0];
        public string[]       loadOrder    = new string[0];
        // Appended in 1.1.0: the host is still looking up Quest versions (questModioId) on mod.io. A joiner on another platform waits for
        // the list the host sends again once that is done; same-platform joiners don't need them and go ahead.
        public bool           questPending;
        // Appended in 1.2.1: the host's Blade & Sorcery version (GameSettings.GetVersionString), "" from older hosts. Never blocks joining.
        public string         gameVersion  = "";

        // What a joiner is told when its game version differs from the host's; null when they match or one is unknown/unreadable.
        // Only major.minor.patch is compared (PC and Nomad build numbers have different formats); on the same platform another build
        // number (a hotfix) gets a mild note. Never blocks joining.
        public static string GameVersionNote(string hostVersion, string myVersion, bool samePlatform) {
            string host = (hostVersion ?? "").Trim(), mine = (myVersion ?? "").Trim();
            if(host.Length == 0 || mine.Length == 0 || host == mine) return null;
            string hostRelease = Release(host), myRelease = Release(mine);
            if(hostRelease == null || myRelease == null) return null; // a format we don't know: say nothing rather than something wrong
            if(hostRelease == myRelease) {
                return samePlatform ? "The host's game build is " + host + ", yours is " + mine + " (same version, another hotfix). Usually fine." : null;
            }
            return "The host runs Blade & Sorcery " + hostRelease + ", you run " + myRelease + ".\nBoth should be on the same game version: expect missing items or desyncs.";
        }

        // "1.3.1.260730005" -> "1.3.1"; null unless it starts with three numbers
        private static string Release(string version) {
            string[] parts = version.Split('.');
            if(parts.Length < 3) return null;
            int n;
            for(int i = 0; i < 3; i++) if(!int.TryParse(parts[i], out n)) return null;
            return parts[0] + "." + parts[1] + "." + parts[2];
        }

        // Whether a joiner on this platform plans with this list or waits for the next one
        public bool WaitForQuestIds(string joinerPlatform) {
            return questPending && !string.Equals(platform, joinerPlatform, StringComparison.OrdinalIgnoreCase);
        }
    }

    // Sent joiner -> host as JSON.
    public class JoinerReport {
        public int    protocol = HostModSet.CurrentProtocol;
        public string platform = "";
        public string status   = JoinerStatus.Ready;
        public string detail   = "";
    }
}
