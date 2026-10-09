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
    }

    // Sent joiner -> host as JSON.
    public class JoinerReport {
        public int    protocol = HostModSet.CurrentProtocol;
        public string platform = "";
        public string status   = JoinerStatus.Ready;
        public string detail   = "";
    }
}
