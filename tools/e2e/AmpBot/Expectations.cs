using AMP.ModSync;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;

namespace AmpBot {
    // What a run must see (--expect file.json). Every field is optional; unknown fields are an error so a typo can't pass silently.
    public class Expectations {
        public int timeoutSec = 300;       // the whole run, from start to the last expectation
        public int lingerSec = 3;          // stay connected/hosting this long after everything was seen

        // --join
        public bool? planBlocks;           // the plan for the host's mods must (not) block joining
        public bool download;              // download what the plan adds/replaces, verify it in the bot's cache, then join anyway
        public List<string> downloaded = new List<string>();  // folders that must have been downloaded and verified
        public List<string> files = new List<string>();       // "<folder>/<relative path>" that must exist in the verified downloads
        public bool greeted = true;        // ServerInfo must arrive
        public List<OptionValue> snapshot = new List<OptionValue>();       // must be in SETTINGS_SNAPSHOT (an empty title matches any)
        public List<OptionValue> settingChanged = new List<OptionValue>(); // must arrive as SETTING_CHANGED

        // --host
        public string modSyncMode = ModSyncMode.Warn;
        public List<OptionValue> sendSnapshot = new List<OptionValue>();   // sent to every greeted joiner
        public List<string> reports = new List<string>();     // JOINER_REPORT statuses that must arrive, in this order (checking excluded)
        public List<string> served = new List<string>();      // folders that must have been sent completely
        public int greetings = 1;          // joiners that must have been greeted

        public static Expectations Load(string path) {
            if(path == null) return new Expectations();
            var settings = new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error };
            return JsonConvert.DeserializeObject<Expectations>(File.ReadAllText(path), settings) ?? new Expectations();
        }
    }
}
