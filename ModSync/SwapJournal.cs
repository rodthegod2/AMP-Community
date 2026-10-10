using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;

namespace AMP.ModSync {
    public enum SwapMode  { Temporary, Keep }
    // Planned: written, nothing changed yet. Applied: folders swapped. RestorePending: restore requested (runs when the game is closed). Restored: back to normal.
    public enum SwapState { Planned, Applied, RestorePending, Restored }
    public enum SwapOpKind { Disable, MoveAside, Install, LoadOrder }

    public class SwapOp {
        [JsonConverter(typeof(StringEnumConverter))]
        public SwapOpKind kind;
        public string folder = "";
        public string hash   = "";   // Install: cache entry
        public bool   done;          // set by the applier once the change is on disk
    }

    // Everything needed to apply and undo one mod swap, and to reconnect afterwards. Saved as AMP_ModCache/session.json.
    public class SwapJournal {
        public string id = Guid.NewGuid().ToString("N");
        [JsonConverter(typeof(StringEnumConverter))] public SwapMode  mode;
        [JsonConverter(typeof(StringEnumConverter))] public SwapState state = SwapState.Planned;
        public string   host     = "";
        public int      port;
        public string   password = "";
        public bool     rejoinPending;          // reconnect after the next start
        public string[] hostLoadOrder = new string[0];
        public bool     hadLoadOrder;           // there was a loadorder.json before the swap
        public List<SwapOp> ops = new List<SwapOp>();
        public string   lastError = "";

        public string ToJson() { return JsonConvert.SerializeObject(this, Formatting.Indented); }

        public static SwapJournal FromJson(string json) {
            try {
                return JsonConvert.DeserializeObject<SwapJournal>(json);
            } catch(JsonException) {
                return null;
            }
        }
    }
}
