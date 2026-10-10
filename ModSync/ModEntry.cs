using Newtonsoft.Json;

namespace AMP.ModSync {
    // One mod folder as the mod sync sees it. Plain data so it can travel as JSON and be tested without the game.
    public class ModEntry {
        public string folder;
        public string name;
        public string author;
        public string version;
        public long   modioId;    // from id.modio, 0 if the mod didn't come from mod.io
        public string hash;       // SHA-256 over relative paths + file hashes, "" if not computed
        public bool   hasCode;    // contains a .dll
        public long   sizeBytes;
        public bool   enabled;    // the game skips folders starting with "_"
        public bool   isAmp;      // the multiplayer mod itself
        // Host -> joiner only (appended in 1.1.0, older builds ignore it / send 0): the mod.io mod a Quest player gets for this mod.
        // > 0: that mod.io id has a live Android/Oculus file; -1: the host checked, there is no Quest version; 0: unknown (older host)
        public long   questModioId;
        // Local only (never sent): the hash by AMP 1.0.0's rules, set when the folder has a modio-thumbnail.png (1.0.0 counted it), so a
        // 1.0.0 host's hash of the same files still matches; null otherwise
        [JsonIgnore] public string legacyHash;

        // This folder holds exactly the files of that hash (either rule)
        public bool HasHash(string other) {
            return !string.IsNullOrEmpty(other) && (other == hash || other == legacyHash);
        }
    }
}
