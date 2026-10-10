using System;
using System.IO;
using System.Linq;

namespace AMP.ModSync {
    // The multiplayer save folder starts as a copy of the player's normal save (settings + characters), once.
    // Characters loaded there may lose items from mods that aren't installed - only the copies, never the originals.
    public static class ProfileSeeder {
        public const string ProfileFolderName = "AMP_Multiplayer";

        // Returns true if the folder exists afterwards. Never overwrites anything in an existing profile.
        public static bool EnsureSeeded(string defaultSaveFolder, string profileFolder) {
            try {
                if(Directory.Exists(profileFolder)) {
                    // A folder the game created on its own (no settings, no characters) is not a seeded profile: fill it, never overwriting
                    if(File.Exists(Path.Combine(profileFolder, "Options.opt")) || Directory.GetFiles(profileFolder, "*.chr").Length > 0) return true;
                    CopySeed(defaultSaveFolder, profileFolder);
                    return true;
                }
                string staging = profileFolder + ".seeding";
                if(Directory.Exists(staging)) Directory.Delete(staging, true);
                Directory.CreateDirectory(staging);
                CopySeed(defaultSaveFolder, staging);
                Directory.Move(staging, profileFolder); // appears complete or not at all
                return true;
            } catch(IOException) {
                return Directory.Exists(profileFolder);
            } catch(UnauthorizedAccessException) {
                return Directory.Exists(profileFolder);
            }
        }

        // Settings and characters only; files already in the target stay as they are
        private static void CopySeed(string from, string to) {
            if(!Directory.Exists(from)) return;
            string options = Path.Combine(from, "Options.opt");
            var files = Directory.GetFiles(from, "*.chr").ToList();
            if(File.Exists(options)) files.Add(options);
            foreach(string file in files) {
                string target = Path.Combine(to, Path.GetFileName(file));
                if(!File.Exists(target)) File.Copy(file, target);
            }
        }
    }
}
