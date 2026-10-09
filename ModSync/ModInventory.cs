using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AMP.ModSync {
    // Reads a Mods folder the same way the game decides what a mod is: a folder with a manifest.json.
    public static class ModInventory {
        // Rewritten on every mod.io update check, so it must not change a mod's hash
        private static readonly string[] HashIgnoredFiles = { "id.modio" };
        private const string HashCacheFile = "hashcache.json";

        // hashAll = false skips hashing (cross-platform joiners can't compare hashes); AMP itself is always hashed
        public static List<ModEntry> Scan(string modsPath, string cacheDir, bool hashAll) {
            var result = new List<ModEntry>();
            if(!Directory.Exists(modsPath)) return result;

            HashCache cache = HashCache.Load(Path.Combine(cacheDir, HashCacheFile));
            foreach(string dir in Directory.GetDirectories(modsPath).OrderBy(d => d, StringComparer.OrdinalIgnoreCase)) {
                ModEntry entry = ReadEntry(dir);
                if(entry == null) continue;
                if(hashAll || entry.isAmp) entry.hash = HashFolder(dir, cache);
                result.Add(entry);
            }
            cache.Save();
            return result;
        }

        public static string[] ReadLoadOrder(string modsPath) {
            string path = Path.Combine(modsPath, "loadorder.json");
            if(!File.Exists(path)) return new string[0];
            try {
                JToken names = JObject.Parse(File.ReadAllText(path))["modNames"];
                return names == null ? new string[0] : names.ToObject<string[]>();
            } catch(JsonException) {
                return new string[0];
            }
        }

        internal static ModEntry ReadEntry(string dir) {
            string manifestPath = Path.Combine(dir, "manifest.json");
            if(!File.Exists(manifestPath)) return null; // the game doesn't load folders without a manifest either

            JObject manifest;
            try {
                manifest = JObject.Parse(File.ReadAllText(manifestPath));
            } catch(JsonException) {
                return null;
            }

            string folder = Path.GetFileName(dir);
            string[] files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
            return new ModEntry {
                folder    = folder,
                name      = Str(manifest, "Name", folder),
                author    = Str(manifest, "Author", ""),
                version   = Str(manifest, "ModVersion", ""),
                modioId   = ReadModioId(Path.Combine(dir, "id.modio")),
                hash      = "",
                hasCode   = files.Any(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)),
                sizeBytes = files.Sum(f => new FileInfo(f).Length),
                enabled   = !folder.StartsWith("_"),
                isAmp     = files.Any(f => Path.GetFileName(f).Equals("AMP.dll", StringComparison.OrdinalIgnoreCase)),
            };
        }

        // id.modio is JSON {"id":123,...} or, in older installs, just the number
        internal static long ReadModioId(string path) {
            if(!File.Exists(path)) return 0;
            string text = File.ReadAllText(path).Trim();
            long id;
            if(long.TryParse(text, out id)) return id;
            try {
                JToken token = JObject.Parse(text)["id"];
                return token == null ? 0 : token.Value<long>();
            } catch(JsonException) {
                return 0;
            } catch(FormatException) {
                return 0;
            }
        }

        internal static string HashFolder(string dir, HashCache cache) {
            var lines = new StringBuilder();
            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                                 .Where(f => !HashIgnoredFiles.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
                                 .Select(f => new KeyValuePair<string, string>(RelativePath(dir, f), f))
                                 .OrderBy(f => f.Key, StringComparer.Ordinal);
            foreach(var file in files) {
                lines.Append(file.Key).Append('\0').Append(cache.FileHash(file.Value)).Append('\n');
            }
            return HashCache.Hex(SHA256Bytes(Encoding.UTF8.GetBytes(lines.ToString())));
        }

        internal static string RelativePath(string root, string file) {
            return file.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
        }

        private static byte[] SHA256Bytes(byte[] data) {
            using(SHA256 sha = SHA256.Create()) return sha.ComputeHash(data);
        }

        private static string Str(JObject obj, string key, string fallback) {
            JToken token = obj[key];
            return token == null || token.Type == JTokenType.Null ? fallback : token.ToString();
        }
    }

    // Remembers file hashes by path, size and modification time, so rescanning gigabytes of mods is fast
    internal class HashCache {
        internal class Item {
            public long   size;
            public long   ticks;
            public string sha;
        }

        private readonly string path;
        private readonly Dictionary<string, Item> items;
        private bool dirty;

        private HashCache(string path, Dictionary<string, Item> items) {
            this.path  = path;
            this.items = items;
        }

        internal static HashCache Load(string path) {
            Dictionary<string, Item> items = null;
            try {
                if(File.Exists(path)) items = JsonConvert.DeserializeObject<Dictionary<string, Item>>(File.ReadAllText(path));
            } catch(JsonException) { } catch(IOException) { }
            return new HashCache(path, items ?? new Dictionary<string, Item>());
        }

        internal string FileHash(string file) {
            var info = new FileInfo(file);
            Item item;
            if(items.TryGetValue(file, out item) && item.size == info.Length && item.ticks == info.LastWriteTimeUtc.Ticks) return item.sha;

            string sha;
            using(FileStream stream = File.OpenRead(file))
            using(SHA256 hasher = SHA256.Create()) {
                sha = Hex(hasher.ComputeHash(stream));
            }
            items[file] = new Item { size = info.Length, ticks = info.LastWriteTimeUtc.Ticks, sha = sha };
            dirty = true;
            return sha;
        }

        internal void Save() {
            if(!dirty) return;
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(items));
                dirty = false;
            } catch(IOException) { } catch(UnauthorizedAccessException) { } // only a speed-up, losing it is fine
        }

        internal static string Hex(byte[] bytes) {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach(byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
