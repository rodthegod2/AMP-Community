using System;
using System.IO;
using ThunderRoad;
using UnityEngine;

namespace AMP.ModSync {
    // Unity paths may only be read on the main thread, so they are resolved once at startup and then used from background scans
    internal static class ModSyncPaths {
        internal static string ModsFolder  { get; private set; }
        internal static string CacheFolder { get; private set; }
        internal static string Platform    { get; private set; }

        // False when Init couldn't find the Mods folder: mod sync then stays out of the way
        internal static bool Available { get { return !string.IsNullOrEmpty(ModsFolder); } }

        internal static void Init() {
            ModsFolder  = null;
            CacheFolder = null;
            Platform    = Application.platform == RuntimePlatform.Android ? "android" : "windows";

            string mods = FileManager.GetFullPath(FileManager.Type.JSONCatalog, FileManager.Source.Mods);
            if(string.IsNullOrEmpty(mods)) throw new InvalidOperationException("the Mods folder path is empty");
            mods = Path.GetFullPath(mods.TrimEnd('/', '\\')); // backslashes, no trailing separator: the helper gets these as command line arguments
            DirectoryInfo parent = Directory.GetParent(mods);
            if(parent == null) throw new InvalidOperationException("the Mods folder has no parent folder");

            CacheFolder = Path.Combine(parent.FullName, "AMP_ModCache");
            ModsFolder  = mods; // set last: Available means everything above worked
        }
    }
}
