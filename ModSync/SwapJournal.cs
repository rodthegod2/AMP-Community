using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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
        public bool   started;       // set (and saved) just before the change, so a crash in between can still be undone
        public bool   done;          // set by the applier once the change is on disk
        public bool   keep;          // never undone, even by a Temporary restore (AMP's own update)
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
        public int      restoreFailures;        // restores that didn't finish; after two the player may forget the swap
        public List<string> conflicts = new List<string>(); // folders moved aside because they were in the way (never deleted)

        // AMP's own update: applied, every op kept, nothing to rejoin. Nothing is left to undo, so the journal can go.
        public bool IsFinishedKeepOnly() {
            return state == SwapState.Applied && !rejoinPending && ops.Count > 0 && ops.All(o => o.keep && o.done);
        }

        public string ToJson() { return JsonConvert.SerializeObject(this, Formatting.Indented); }

        // Written to a side file first and swapped in, so a crash never leaves half a journal
        public void SaveTo(string path) {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllText(temp, ToJson());
            if(File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }

        // The side file is only used when the real one is unreadable (a crash between writing and swapping)
        public static SwapJournal LoadFrom(string path) {
            return TryRead(path) ?? TryRead(path + ".tmp");
        }

        private static SwapJournal TryRead(string path) {
            try {
                return File.Exists(path) ? FromJson(File.ReadAllText(path)) : null;
            } catch(IOException) {
                return null;
            } catch(System.UnauthorizedAccessException) {
                return null;
            }
        }

        public static SwapJournal FromJson(string json) {
            try {
                return JsonConvert.DeserializeObject<SwapJournal>(json);
            } catch(JsonException) {
                return null;
            }
        }
    }
}
