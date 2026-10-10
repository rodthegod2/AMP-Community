using System;
using System.Collections.Generic;

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
