using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace AMP.ModSync {
    // Unpacks a zip downloaded from the network (an AMP release or a mod from mod.io). Nothing is written until every entry name has been checked.
    public static class ReleasePackage {
        private const int MaxEntries = 10000;

        // Extracts into a fresh folder under stagingRoot and returns the one folder holding AMP.dll and manifest.json
        public static string ExtractAmpFolder(string zipPath, string stagingRoot, long maxBytes) {
            using(Stream stream = File.OpenRead(zipPath)) return ExtractAmpFolder(stream, stagingRoot, maxBytes);
        }

        // For a caller that already holds the zip open (it hashed the same stream), so the file can't change in between. Leaves the stream open.
        public static string ExtractAmpFolder(Stream zipStream, string stagingRoot, long maxBytes) {
            string dest = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
            try {
                using(var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, true)) {
                    Extract(zip, dest, maxBytes);
                }
                return FindAmpFolder(dest);
            } catch {
                if(Directory.Exists(dest)) Directory.Delete(dest, true);
                throw;
            }
        }

        // Extracts a whole zip into dest (created; must not exist yet). Leaves the stream open. Throws InvalidDataException for unsafe or oversized zips.
        public static void ExtractAll(Stream zipStream, string dest, long maxBytes) {
            using(var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, true)) Extract(zip, dest, maxBytes);
        }

        // The mod folder of an extracted mod.io zip, picked the way the game installs one: manifest.json at the top means the top is the mod,
        // otherwise the one top-level folder that has a manifest.json (zips are usually "<ModFolder>/manifest.json"). Throws InvalidDataException otherwise.
        public static string FindModFolder(string root) {
            if(File.Exists(Path.Combine(root, "manifest.json"))) return root;
            string[] folders = Directory.GetDirectories(root).Where(d => File.Exists(Path.Combine(d, "manifest.json"))).ToArray();
            if(folders.Length != 1) throw new InvalidDataException("The mod must have one folder with a manifest.json, found " + folders.Length + ".");
            return folders[0];
        }

        private static void Extract(ZipArchive zip, string dest, long maxBytes) {
            if(zip.Entries.Count > MaxEntries) throw new InvalidDataException("The zip has more than " + MaxEntries + " files.");

            var paths = new List<string>();
            long declared = 0;
            foreach(ZipArchiveEntry entry in zip.Entries) {
                string path = TransferRules.NormalizeRelativePath(entry.FullName.TrimEnd('/'));
                if(path == null) throw new InvalidDataException("The zip has an unsafe file name: " + entry.FullName);
                paths.Add(path);
                declared += entry.Length;
            }
            if(declared > maxBytes) throw new InvalidDataException("The zip is larger than " + maxBytes + " bytes.");

            Directory.CreateDirectory(dest);
            long written = 0;
            for(int i = 0; i < zip.Entries.Count; i++) {
                if(zip.Entries[i].FullName.EndsWith("/")) continue; // folders are created by their files
                if(paths[i].StartsWith("__MACOSX/")) continue;      // macOS metadata, never part of a mod
                string target = Path.Combine(dest, paths[i].Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                using(Stream input = zip.Entries[i].Open())
                using(Stream output = File.Create(target)) {
                    written = Copy(input, output, written, maxBytes);
                }
            }
        }

        // Counts the real bytes, not the size the zip claims, so a lying entry can't write past the limit
        private static long Copy(Stream input, Stream output, long written, long maxBytes) {
            byte[] buffer = new byte[81920];
            int read;
            while((read = input.Read(buffer, 0, buffer.Length)) > 0) {
                written += read;
                if(written > maxBytes) throw new InvalidDataException("The zip is larger than " + maxBytes + " bytes.");
                output.Write(buffer, 0, read);
            }
            return written;
        }

        private static string FindAmpFolder(string dest) {
            string[] folders = Directory.GetFiles(dest, "AMP.dll", SearchOption.AllDirectories)
                                        .Select(f => Path.GetDirectoryName(f))
                                        .Where(d => File.Exists(Path.Combine(d, "manifest.json")))
                                        .ToArray();
            if(folders.Length != 1) throw new InvalidDataException("The release must contain exactly one folder with AMP.dll and manifest.json, found " + folders.Length + ".");
            return folders[0];
        }
    }
}
