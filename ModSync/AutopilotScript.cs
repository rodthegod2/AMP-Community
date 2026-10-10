using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AMP.ModSync {
    // The game-independent half of the end-to-end autopilot: the script format, its validation, the command line, and the saved progress.
    // Script = JSON array of objects, each with "step" (the step name), that step's arguments and an optional "timeout" in seconds. Example:
    //   [ {"step":"waitSpawn"}, {"step":"host","port":27000,"maxPlayers":4}, {"step":"wait","seconds":10}, {"step":"quit"} ]
    public class AutopilotStep {
        public string Name;
        public int TimeoutSec;
        public Dictionary<string, object> Args = new Dictionary<string, object>(); // string, long or bool

        public string Str(string key)  { return (string) Args[key]; }
        public long   Int(string key)  { return (long) Args[key]; }
        public bool   Bool(string key) { return (bool) Args[key]; }
        public string Str(string key, string fallback) { object v; return Args.TryGetValue(key, out v) ? (string) v : fallback; }
        public long   Int(string key, long fallback)   { object v; return Args.TryGetValue(key, out v) ? (long) v : fallback; }
    }

    public class AutopilotScript {
        public const int DefaultTimeoutSec = 120;
        public const int MaxTimeoutSec = 3600;

        public List<AutopilotStep> Steps = new List<AutopilotStep>();

        // Step name -> its arguments as "key:type" (type: int[min-max], string, bool, mode, folder); a trailing ? makes one optional
        private static readonly Dictionary<string, string> Specs = new Dictionary<string, string> {
            { "waitSpawn",           "" },
            { "host",                "port:int[1-32767] maxPlayers:int[1-64]? password:string?" }, // 32767: Netamite stores ports as short
            { "join",                "address:string port:int[1-32767] password:string?" },
            { "expectModSync",       "blocks:bool" },
            { "downloadAndRestart",  "mode:mode" },
            { "expectRejoined",      "" },
            { "expectModFolder",     "folder:folder present:bool" },
            { "setOption",           "mod:string option:string title:string" },
            { "expectOption",        "mod:string option:string title:string" },
            { "expectProfileActive", "active:bool" },
            { "disconnect",          "" },
            { "wait",                "seconds:int[0-3600]" },
            { "quit",                "" },
        };

        private static readonly Regex SpecToken = new Regex(@"^(\w+):(\w+)(?:\[(\d+)-(\d+)\])?(\?)?$");

        // Returns null and an error that names the step when the script is unusable
        public static AutopilotScript Parse(string json, out string error) {
            error = null;
            JToken root;
            try {
                // No date detection: a title like "2020-01-01" is text
                using(var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None }) root = JToken.ReadFrom(reader);
            } catch(JsonException e) {
                error = "the script is not valid JSON: " + e.Message;
                return null;
            }
            JArray array = root as JArray;
            if(array == null) { error = "the script must be a JSON array of steps"; return null; }
            if(array.Count == 0) { error = "the script has no steps"; return null; }

            var script = new AutopilotScript();
            for(int i = 0; i < array.Count; i++) {
                string problem;
                AutopilotStep step = ParseStep(array[i] as JObject, out problem);
                if(step == null) { error = "step " + i + (problem.StartsWith("(") ? " " : ": ") + problem; return null; }
                if(step.Name == "quit" && i != array.Count - 1) { error = "step " + i + " (quit): quit must be the last step"; return null; }
                script.Steps.Add(step);
            }
            return script;
        }

        private static AutopilotStep ParseStep(JObject obj, out string problem) {
            problem = null;
            if(obj == null) { problem = "must be a JSON object"; return null; }
            JToken nameToken = obj["step"];
            if(nameToken == null || nameToken.Type != JTokenType.String) { problem = "needs a \"step\" name"; return null; }
            string name = (string) nameToken;
            string spec;
            if(!Specs.TryGetValue(name, out spec)) { problem = "unknown step \"" + name + "\""; return null; }

            string at = "(" + name + "): ";
            var step = new AutopilotStep { Name = name, TimeoutSec = DefaultTimeoutSec };
            var known = new HashSet<string> { "step", "timeout" };

            foreach(string token in spec.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) {
                Match m = SpecToken.Match(token);
                string key = m.Groups[1].Value, type = m.Groups[2].Value;
                bool optional = m.Groups[5].Success;
                known.Add(key);

                JToken value = obj[key];
                if(value == null || value.Type == JTokenType.Null) {
                    if(optional && value == null) continue;
                    problem = at + "\"" + key + "\" is required"; return null;
                }

                switch(type) {
                    case "int": {
                        if(value.Type != JTokenType.Integer) { problem = at + "\"" + key + "\" must be a whole number"; return null; }
                        long number = (long) value, min = long.Parse(m.Groups[3].Value), max = long.Parse(m.Groups[4].Value);
                        if(number < min || number > max) { problem = at + "\"" + key + "\" must be " + min + "-" + max; return null; }
                        step.Args[key] = number;
                        break;
                    }
                    case "bool":
                        if(value.Type != JTokenType.Boolean) { problem = at + "\"" + key + "\" must be true or false"; return null; }
                        step.Args[key] = (bool) value;
                        break;
                    default: { // string, mode, folder
                        if(value.Type != JTokenType.String) { problem = at + "\"" + key + "\" must be a string"; return null; }
                        string text = (string) value;
                        if(text.Trim().Length == 0) { problem = at + "\"" + key + "\" must not be blank"; return null; }
                        if(type == "mode") {
                            SwapMode mode;
                            if(!Enum.TryParse(text, true, out mode) || !Enum.IsDefined(typeof(SwapMode), mode)) { problem = at + "\"" + key + "\" must be Temporary or Keep"; return null; }
                            text = mode.ToString();
                        } else if(type == "folder" && (text.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || text == "." || text == "..")) {
                            problem = at + "\"" + key + "\" must be a plain folder name"; return null;
                        }
                        step.Args[key] = text;
                        break;
                    }
                }
            }

            // A wait must not run into its own timeout
            if(name == "wait") step.TimeoutSec = (int) step.Int("seconds") + 30;

            JToken timeout = obj["timeout"];
            if(timeout != null) {
                if(timeout.Type != JTokenType.Integer || (long) timeout < 1 || (long) timeout > MaxTimeoutSec) { problem = at + "\"timeout\" must be 1-" + MaxTimeoutSec + " seconds"; return null; }
                step.TimeoutSec = (int) (long) timeout;
            }

            foreach(JProperty property in obj.Properties()) {
                if(!known.Contains(property.Name)) { problem = at + "unknown key \"" + property.Name + "\""; return null; }
            }
            return step;
        }

        // The value after a flag (args[0] is the program, never a flag); null when the flag or its value is missing
        public static string FindArg(string[] args, string flag) {
            if(args == null) return null;
            for(int i = 1; i < args.Length - 1; i++) {
                if(args[i] == flag) return args[i + 1];
            }
            return null;
        }
    }

    // Progress of a script, saved after every step so a relaunched game continues where the last one stopped.
    // It only belongs to the script it was made for (hash), so a new script never inherits an old position.
    public class AutopilotState {
        public string scriptHash = "";
        public int step;                                                   // index of the next step to run
        public Dictionary<string, string> values = new Dictionary<string, string>(); // scratch values steps leave for later steps (or a later start)

        public static string HashOf(string scriptText) {
            using(var sha = SHA256.Create()) {
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(scriptText)).Select(b => b.ToString("x2")));
            }
        }

        // A missing, unreadable or foreign state is a fresh start
        public static AutopilotState Load(string path, string scriptHash) {
            try {
                if(File.Exists(path)) {
                    AutopilotState state = JsonConvert.DeserializeObject<AutopilotState>(File.ReadAllText(path));
                    if(state != null && state.scriptHash == scriptHash && state.step >= 0 && state.values != null) return state;
                }
            } catch(Exception e) {
                if(!(e is JsonException || e is IOException || e is UnauthorizedAccessException)) throw;
            }
            return new AutopilotState { scriptHash = scriptHash };
        }

        // Written to a side file first and swapped in, so a quit in the middle never leaves half a file
        public void Save(string path) {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(this, Formatting.Indented));
            if(File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
    }
}
