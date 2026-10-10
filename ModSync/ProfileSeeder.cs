using System;
using System.IO;

namespace AMP.ModSync {
    // The multiplayer save folder starts as a copy of the player's normal save (settings + characters), once.
    // Characters loaded there may lose items from mods that aren't installed - only the copies, never the originals.
    public static class ProfileSeeder {
        public const string ProfileFolderName = "AMP_Multiplayer";

        // Returns true if the folder exists afterwards. Never overwrites anything in an existing profile.
        public static bool EnsureSeeded(string defaultSaveFolder, string profileFolder) {
            try {
                if(Directory.Exists(profileFolder)) return true;
                string staging = profileFolder + ".seeding";
                if(Directory.Exists(staging)) Directory.Delete(staging, true);
                Directory.CreateDirectory(staging);
                if(Directory.Exists(defaultSaveFolder)) {
                    string options = Path.Combine(defaultSaveFolder, "Options.opt");
                    if(File.Exists(options)) File.Copy(options, Path.Combine(staging, "Options.opt"));
                    foreach(string character in Directory.GetFiles(defaultSaveFolder, "*.chr")) {
                        File.Copy(character, Path.Combine(staging, Path.GetFileName(character)));
                    }
                }
                Directory.Move(staging, profileFolder); // appears complete or not at all
                return true;
            } catch(IOException) {
                return Directory.Exists(profileFolder);
            } catch(UnauthorizedAccessException) {
                return Directory.Exists(profileFolder);
            }
        }
    }
}
