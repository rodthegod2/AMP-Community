using System;
using System.IO;

// Builds throwaway Mods folders under %TEMP% for the tests.
internal sealed class TestFolders : IDisposable {
    public readonly string Root;
    public readonly string Mods;
    public readonly string Cache;

    public TestFolders() {
        Root  = Path.Combine(Path.GetTempPath(), "amp-modsync-test-" + Guid.NewGuid().ToString("N"));
        Mods  = Path.Combine(Root, "Mods");
        Cache = Path.Combine(Root, "AMP_ModCache");
        Directory.CreateDirectory(Mods);
    }

    public string Mod(string folder, string name = null, string author = "Someone", string version = "1.0") {
        string dir = Path.Combine(Mods, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            "{ \"Name\": \"" + (name ?? folder) + "\", \"Author\": \"" + author + "\", \"ModVersion\": \"" + version + "\", \"GameVersion\": \"1.0.0.0\" }");
        return dir;
    }

    public void File_(string modDir, string relativePath, string content) {
        string path = Path.Combine(modDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, content);
    }

    public void Dispose() {
        try { Directory.Delete(Root, true); } catch(IOException) { } catch(UnauthorizedAccessException) { }
    }
}
