using System.IO;
using ThunderRoad;
using UnityEngine;

namespace AMP.ModSync {
    // Unity paths may only be read on the main thread, so they are resolved once at startup and then used from background scans
    internal static class ModSyncPaths {
        internal static string ModsFolder  { get; private set; }
        internal static string CacheFolder { get; private set; }
        internal static string Platform    { get; private set; }

        internal static void Init() {
            ModsFolder  = FileManager.GetFullPath(FileManager.Type.JSONCatalog, FileManager.Source.Mods);
            CacheFolder = Path.Combine(Directory.GetParent(ModsFolder.TrimEnd('/', '\\')).FullName, "AMP_ModCache");
            Platform    = Application.platform == RuntimePlatform.Android ? "android" : "windows";
        }
    }
}
