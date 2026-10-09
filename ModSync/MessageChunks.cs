using System;
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

    // Reassembles parts that arrive from many senders. Memory stays bounded: at most MaxPending messages
    // (the oldest is evicted), none older than MaxAge, and no message longer than MaxMessageChars.
    public class MessageAssembler {
        private const int MaxPending = 16;
        private const int MaxMessageChars = 2000000;
        private const int MaxParts = MaxMessageChars / MessageChunks.MaxChars; // 133
        private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);

        private class Pending {
            public string[] slots;
            public DateTime started;
        }

        private readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>();
        private readonly Func<DateTime> now;

        public MessageAssembler() : this(() => DateTime.UtcNow) { }

        public MessageAssembler(Func<DateTime> now) {
            this.now = now;
        }

        // Returns the full message once every part arrived, otherwise null
        public string Add(string key, int part, int parts, string data) {
            if(parts <= 0 || parts > MaxParts || part < 0 || part >= parts) return null;
            lock(pending) {
                DateTime current = now();
                DropOlderThan(current);

                Pending entry;
                if(!pending.TryGetValue(key, out entry) || entry.slots.Length != parts) {
                    if(!pending.ContainsKey(key) && pending.Count >= MaxPending) EvictOldest();
                    entry = new Pending { slots = new string[parts], started = current };
                    pending[key] = entry;
                }
                entry.slots[part] = data ?? "";
                if(entry.slots.Any(s => s == null)) return null;
                pending.Remove(key);
                return string.Concat(entry.slots);
            }
        }

        // Forgets every pending message whose key starts with keyPrefix, e.g. "<clientId>:" when that client leaves
        public void Forget(string keyPrefix) {
            lock(pending) {
                foreach(string key in pending.Keys.Where(k => k.StartsWith(keyPrefix, StringComparison.Ordinal)).ToList()) pending.Remove(key);
            }
        }

        private void DropOlderThan(DateTime current) {
            foreach(string key in pending.Where(kv => current - kv.Value.started > MaxAge).Select(kv => kv.Key).ToList()) pending.Remove(key);
        }

        private void EvictOldest() {
            pending.Remove(pending.OrderBy(kv => kv.Value.started).First().Key);
        }
    }
}
