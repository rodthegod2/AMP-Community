using System;
using System.Collections.Generic;
using System.Linq;

namespace AMP.ModSync {
    // One Mod Options value as sent host -> joiner. The title is compared too, because indexes can differ between builds of a mod.
    public class OptionValue {
        public string mod;
        public string option;
        public string title;
        public int    index;
    }

    public class SettingsMessage {
        public List<OptionValue> values = new List<OptionValue>();
    }

    // Remembers the last value of each option the host told its joiners, so a menu apply that didn't change anything isn't broadcast again
    public class SentSettings {
        private readonly Dictionary<string, string> last = new Dictionary<string, string>();

        private static string Key(OptionValue v)   { return v.mod + "/" + v.option; }
        private static string Value(OptionValue v) { return v.index + "|" + v.title; }

        public void Clear() { lock(last) last.Clear(); }

        public void Remember(OptionValue value) {
            if(value == null) return;
            lock(last) last[Key(value)] = Value(value);
        }

        // True (and remembered) when the value differs from the last one known for that option
        public bool IsChange(OptionValue value) {
            if(value == null) return false;
            lock(last) {
                string before;
                if(last.TryGetValue(Key(value), out before) && before == Value(value)) return false;
                last[Key(value)] = Value(value);
                return true;
            }
        }
    }

    public static class SettingsRules {
        // AMP's own options in this category are the host's server settings; the rest of AMP's options are personal
        public const string AmpHostCategory = "Host settings";

        // Words that mark an option as a personal preference (sound, menus, controls, comfort) rather than gameplay
        private static readonly string[] PersonalWords = {
            "volume", "sound", "audio", "music", "voice", "mic", "microphone", "ui", "menu", "hud", "display",
            "graphic", "graphics", "haptic", "haptics", "vibration", "control", "controls", "key", "keybind",
            "pose", "subtitle", "subtitles", "language", "nametag", "nametags", "healthbar", "healthbars"
        };

        public static bool IsServerSide(string modFolder, string optionName, string category, bool isAmp,
                                        ICollection<string> personalMods, IDictionary<string, string> overrides) {
            if(personalMods != null && personalMods.Any(p => string.Equals(p, modFolder, StringComparison.OrdinalIgnoreCase))) return false;

            string chosen;
            if(overrides != null && overrides.TryGetValue(modFolder + "/" + optionName, out chosen)) {
                if(string.Equals(chosen, "server", StringComparison.OrdinalIgnoreCase))   return true;
                if(string.Equals(chosen, "personal", StringComparison.OrdinalIgnoreCase)) return false;
            }

            if(isAmp) return category == AmpHostCategory;

            foreach(string word in Words(optionName).Concat(Words(category))) {
                foreach(string personal in PersonalWords) {
                    // Short words must match exactly ("ui" must not hit "guide"), longer ones may be a prefix ("haptics")
                    if(word == personal || (personal.Length >= 4 && word.StartsWith(personal))) return false;
                }
            }
            return true;
        }

        // Index of the host's value in our own option, or -1 if we can't be sure it's the same value
        public static int MatchIndex(string[] titles, OptionValue value) {
            if(titles == null || titles.Length == 0 || value == null) return -1;
            if(value.index >= 0 && value.index < titles.Length && titles[value.index] == value.title) return value.index;
            return Array.IndexOf(titles, value.title);
        }

        private static IEnumerable<string> Words(string text) {
            if(string.IsNullOrEmpty(text)) return Enumerable.Empty<string>();
            return new string(text.Select(c => char.IsLetter(c) ? char.ToLowerInvariant(c) : ' ').ToArray())
                   .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
