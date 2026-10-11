using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AMP.ModSync {
    public class TransferFile {
        public string path; // relative, '/' separated, validated
        public long   size;
    }

    // Host -> joiner: one mod folder about to be sent
    public class TransferManifest {
        public int                requestId; // the joiner's MODS_REQUEST this answers
        public int                transferId;
        public string             folder;
        public string             hash;
        public long               modioId;
        public List<TransferFile> files = new List<TransferFile>();

        public long TotalBytes { get { return files.Sum(f => f.size); } }
    }

    // Joiner -> host
    public class ModsRequest {
        public List<string> folders = new List<string>();
    }

    // Host -> joiner: a transfer finished or failed on the host side
    public class TransferEnd {
        public int    requestId;
        public int    transferId;
        public bool   ok;
        public string error = "";
    }

    public static class TransferRules {
        private static readonly string[] ReservedNames = { "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

        // A path from the network, made safe to combine with a target folder; null if it could escape it or isn't a valid file name
        public static string NormalizeRelativePath(string path) {
            if(string.IsNullOrEmpty(path)) return null;
            path = path.Replace('\\', '/');
            if(path.StartsWith("/") || path.Contains(":")) return null;

            string[] parts = path.Split('/');
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach(string part in parts) {
                if(part.Length == 0 || part == "." || part == "..") return null;
                if(part.IndexOfAny(invalid) >= 0) return null;
                if(part.EndsWith(".") || part.EndsWith(" ")) return null;
                string stem = part.Split('.')[0];
                if(ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase)) return null;
            }
            return string.Join("/", parts);
        }

        // A chunk's base64 text from the network; null if it isn't valid. An empty chunk (empty file) is "" and decodes to no bytes.
        public static byte[] DecodeChunk(string data) {
            try {
                return Convert.FromBase64String(data ?? "");
            } catch(FormatException) {
                return null;
            }
        }

        // The host's setup time limit doesn't end a joiner's download that is still moving: lastProgress is when the joiner last
        // acknowledged a chunk (or asked for the next mod) of its running download from the host, null when none is running
        public static bool DownloadStillMoving(DateTime now, DateTime? lastProgress, TimeSpan stall) {
            return lastProgress.HasValue && now - lastProgress.Value < stall;
        }

        public static List<TransferFile> ListTransferFiles(string folder) {
            return Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                            .Select(f => new { full = f, rel = ModInventory.RelativePath(folder, f) })
                            .Where(f => ModInventory.IsHashedFile(f.rel))
                            .OrderBy(f => f.rel, StringComparer.Ordinal)
                            .Select(f => new TransferFile { path = f.rel, size = new FileInfo(f.full).Length })
                            .ToList();
        }
    }
}
