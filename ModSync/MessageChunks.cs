using System.Collections.Generic;
using System.Linq;

namespace AMP.ModSync {
    // Netamite strings and packets are limited to 64 KB, so long JSON messages travel in parts.
    public static class MessageChunks {
        public const int MaxChars = 15000; // UTF-8 can need 4 bytes per char: 15000 * 4 stays under 64 KB

        public static List<string> Split(string text) {
            var parts = new List<string>();
            text = text ?? "";
            int index = 0;
            do {
                int length = System.Math.Min(MaxChars, text.Length - index);
                // Don't cut an emoji (surrogate pair) in half, each part is encoded on its own
                if(length > 0 && index + length < text.Length && char.IsHighSurrogate(text[index + length - 1])) length--;
                parts.Add(text.Substring(index, length));
                index += length;
            } while(index < text.Length);
            return parts;
        }
    }

    public class MessageAssembler {
        private const int MaxParts = 1000;
        private readonly Dictionary<string, string[]> pending = new Dictionary<string, string[]>();

        // Returns the full message once every part arrived, otherwise null
        public string Add(string key, int part, int parts, string data) {
            if(parts <= 0 || parts > MaxParts || part < 0 || part >= parts) return null;
            lock(pending) {
                string[] slots;
                if(!pending.TryGetValue(key, out slots) || slots.Length != parts) {
                    slots = new string[parts];
                    pending[key] = slots;
                }
                slots[part] = data ?? "";
                if(slots.Any(s => s == null)) return null;
                pending.Remove(key);
                return string.Concat(slots);
            }
        }
    }
}
