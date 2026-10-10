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
        // Plus OS folder metadata and logs, which appear and change without the mod changing
        private static readonly string[] HashIgnoredFiles = { ModioIdFile, "Thumbs.db", "desktop.ini", ".DS_Store", ModioThumbnail };
        // Written into a mod folder by the game's mod manager when it shows the mod (not part of the mod.io download). Counted by AMP 1.0.0.
        internal const string ModioThumbnail = "modio-thumbnail.png";
        private const string HashCacheFile = "hashcache.json";

        // hashAll = false skips hashing (cross-platform joiners can't compare hashes); AMP itself is always hashed
        public static List<ModEntry> Scan(string modsPath, string cacheDir, bool hashAll) {
            var result = new List<ModEntry>();
            if(!Directory.Exists(modsPath)) return result;

            HashCache cache = HashCache.Load(Path.Combine(cacheDir, HashCacheFile));
            bool completed = false;
            try {
                foreach(string dir in Directory.GetDirectories(modsPath).OrderBy(d => d, StringComparer.OrdinalIgnoreCase)) {
                    try {
                        string[] files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
                        ModEntry entry = ReadEntry(dir, files);
                        if(entry == null) continue;
                        if(hashAll || entry.isAmp) {
                            entry.hash = HashFolder(dir, files, cache);
                            if(files.Any(f => Path.GetFileName(f).Equals(ModioThumbnail, StringComparison.OrdinalIgnoreCase))) entry.legacyHash = HashFolder(dir, files, cache, true);
                        }
                        result.Add(entry);
                    } catch(Exception e) when (e is IOException || e is UnauthorizedAccessException) {
                        // a folder that vanished or can't be read is skipped, never hashed partially
                    }
                }
                completed = true;
            } finally {
                cache.Save(hashAll && completed);
            }
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

        internal static ModEntry ReadEntry(string dir, string[] files) {
            string manifestPath = Path.Combine(dir, "manifest.json");
            if(!File.Exists(manifestPath)) return null; // the game doesn't load folders without a manifest either

            JObject manifest;
            try {
                manifest = JObject.Parse(File.ReadAllText(manifestPath));
            } catch(JsonException) {
                return null;
            } catch(ArgumentException) {
                return null; // older Newtonsoft throws this on duplicate keys
            }

            string folder = Path.GetFileName(dir);
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
                return token != null && token.Type == JTokenType.Integer ? token.Value<long>() : 0;
            } catch(JsonException) {
                return 0;
            } catch(ArgumentException) {
                return 0;
            } catch(FormatException) {
                return 0;
            } catch(OverflowException) {
                return 0;
            } catch(InvalidCastException) {
                return 0; // ids too big for a long are stored as BigInteger and can't be read as one
            }
        }

        // legacy: the rules of AMP 1.0.0, which counted modio-thumbnail.png (a 1.0.0 host's hashes and transfers include it)
        internal static string HashFolder(string dir, string[] files, HashCache cache, bool legacy = false) {
            var lines = new StringBuilder();
            var ordered = files.Where(f => IsHashedFile(f) || (legacy && Path.GetFileName(f).Equals(ModioThumbnail, StringComparison.OrdinalIgnoreCase)))
                               .Select(f => new KeyValuePair<string, string>(RelativePath(dir, f), f))
                               .OrderBy(f => f.Key, StringComparer.Ordinal);
            foreach(var file in ordered) {
                lines.Append(file.Key).Append('\0').Append(cache.FileHash(file.Value)).Append('\n');
            }
            return HashCache.Hex(SHA256Bytes(Encoding.UTF8.GetBytes(lines.ToString())));
        }

        internal const string ModioIdFile = "id.modio";

        // Any spelling: Android file systems are case-sensitive, and an id.modio of any case must never get into a mod we install
        public static bool IsModioIdFile(string path) {
            return Path.GetFileName(path ?? "").Equals(ModioIdFile, StringComparison.OrdinalIgnoreCase);
        }

        // Deletes every id.modio (any case, any depth) under folder; returns how many
        public static int StripModioIds(string folder) {
            int removed = 0;
            foreach(string file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Where(IsModioIdFile)) {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
                removed++;
            }
            return removed;
        }

        // Files that count for a mod's hash (and therefore get transferred)
        internal static bool IsHashedFile(string path) {
            string name = Path.GetFileName(path);
            if(HashIgnoredFiles.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
            if(name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
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

    // Remembers file hashes by path, size and modification time, so rescanning gigabytes of mods is fast.
    // After a full scan only files seen by it are kept, so the cache doesn't grow with deleted mods.
    internal class HashCache {
        internal class Item {
            public long   size;
            public long   ticks;
            public string sha;
        }

        private readonly string path;
        private readonly Dictionary<string, Item> items;
        private readonly HashSet<string> visited = new HashSet<string>();
        private bool dirty;

        private HashCache(string path, Dictionary<string, Item> items) {
            this.path  = path;
            this.items = items;
        }

        internal static HashCache Load(string path) {
            Dictionary<string, Item> items = null;
            try {
                if(File.Exists(path)) items = JsonConvert.DeserializeObject<Dictionary<string, Item>>(File.ReadAllText(path));
            } catch(JsonException) { } catch(IOException) { } catch(UnauthorizedAccessException) { }
            return new HashCache(path, items ?? new Dictionary<string, Item>());
        }

        internal string FileHash(string file) {
            visited.Add(file);
            var info = new FileInfo(file);
            Item item;
            if(items.TryGetValue(file, out item) && item.size == info.Length && item.ticks == info.LastWriteTimeUtc.Ticks) return item.sha;

            string sha;
            // Share read/write/delete so files the game or a mod tool has open can still be hashed
            using(var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
            using(SHA256 hasher = SHA256.Create()) {
                sha = Hex(hasher.ComputeHash(stream));
            }
            items[file] = new Item { size = info.Length, ticks = info.LastWriteTimeUtc.Ticks, sha = sha };
            dirty = true;
            return sha;
        }

        // Only a scan that hashed every mod knows which entries are stale; a partial scan keeps the others
        internal void Save(bool pruneUnvisited) {
            Dictionary<string, Item> kept = pruneUnvisited
                ? items.Where(kv => visited.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value)
                : items;
            if(kept.Count != items.Count) dirty = true;
            if(!dirty) return;
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(kept));
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
