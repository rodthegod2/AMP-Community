using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AMP.ModSync {
    public class CacheEntry {
        public string hash;
        public string folder;
        public string name;
        public long   modioId;
        public long   sizeBytes;
        public long   lastUsedTicks;
    }

    // Downloaded mods, stored by content hash outside the Mods folder so the game never loads them:
    //   <root>/mods/<hash>/content/...   the mod folder's files
    //   <root>/mods/<hash>/entry.json    CacheEntry
    //   <root>/partial/<guid>/           downloads in progress
    public sealed class ModCache {
        private readonly string root;

        public ModCache(string root) { this.root = root; }

        private string ModsRoot    { get { return Path.Combine(root, "mods"); } }
        private string PartialRoot { get { return Path.Combine(root, "partial"); } }

        public string ContentFolder(string hash) { return Path.Combine(ModsRoot, hash, "content"); }
        private string EntryFile(string hash)   { return Path.Combine(ModsRoot, hash, "entry.json"); }

        public string NewStagingFolder() {
            string path = Path.Combine(PartialRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        // Verifies the staged files against the expected mod hash and moves them into the cache. Null (and staging deleted) on mismatch.
        public CacheEntry Commit(string stagingFolder, ModEntry expected) {
            try {
                string[] files = Directory.GetFiles(stagingFolder, "*", SearchOption.AllDirectories);
                string hash = ModInventory.HashFolder(stagingFolder, files, HashCache.Load(Path.Combine(stagingFolder, "..", "nohashcache.json")));
                if(!IsSafeKey(expected.hash) || hash != expected.hash) {
                    Directory.Delete(stagingFolder, true);
                    return null;
                }

                string content = ContentFolder(hash);
                if(Directory.Exists(content)) {
                    Directory.Delete(stagingFolder, true);
                } else {
                    Directory.CreateDirectory(Path.GetDirectoryName(content));
                    Directory.Move(stagingFolder, content);
                }

                var entry = new CacheEntry {
                    hash          = hash,
                    folder        = expected.folder,
                    name          = expected.name,
                    modioId       = expected.modioId,
                    sizeBytes     = files.Sum(f => new FileInfo(Path.Combine(content, ModInventory.RelativePath(stagingFolder, f))).Length),
                    lastUsedTicks = DateTime.UtcNow.Ticks,
                };
                File.WriteAllText(EntryFile(hash), JsonConvert.SerializeObject(entry));
                return entry;
            } catch(IOException) {
                TryDelete(stagingFolder);
                return null;
            } catch(UnauthorizedAccessException) {
                TryDelete(stagingFolder);
                return null;
            }
        }

        public CacheEntry Find(string hash) {
            if(!IsSafeKey(hash) || !Directory.Exists(ContentFolder(hash)) || !File.Exists(EntryFile(hash))) return null;
            try {
                return JsonConvert.DeserializeObject<CacheEntry>(File.ReadAllText(EntryFile(hash)));
            } catch(JsonException) {
                return null;
            } catch(IOException) {
                return null;
            }
        }

        public void Touch(string hash) {
            CacheEntry entry = Find(hash);
            if(entry == null) return;
            entry.lastUsedTicks = DateTime.UtcNow.Ticks;
            try { File.WriteAllText(EntryFile(hash), JsonConvert.SerializeObject(entry)); } catch(IOException) { }
        }

        // Deletes least recently used entries until the cache is at most maxBytes; `keep` hashes are never deleted
        public void Evict(long maxBytes, ICollection<string> keep) {
            if(!Directory.Exists(ModsRoot)) return;
            List<CacheEntry> entries = Directory.GetDirectories(ModsRoot).Select(d => Find(Path.GetFileName(d))).Where(e => e != null).ToList();
            long total = entries.Sum(e => e.sizeBytes);
            foreach(CacheEntry entry in entries.OrderBy(e => e.lastUsedTicks)) {
                if(total <= maxBytes) break;
                if(keep != null && keep.Contains(entry.hash)) continue;
                if(TryDelete(Path.Combine(ModsRoot, entry.hash))) total -= entry.sizeBytes;
            }
        }

        public void CleanPartial() {
            if(!Directory.Exists(PartialRoot)) return;
            foreach(string dir in Directory.GetDirectories(PartialRoot)) TryDelete(dir);
        }

        // Hashes come from the network: only accept 64 lowercase hex chars as folder names
        private static bool IsSafeKey(string hash) {
            return hash != null && hash.Length == 64 && hash.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
        }

        private static bool TryDelete(string dir) {
            try {
                if(Directory.Exists(dir)) Directory.Delete(dir, true);
                return true;
            } catch(IOException) {
                return false;
            } catch(UnauthorizedAccessException) {
                return false;
            }
        }
    }
}
