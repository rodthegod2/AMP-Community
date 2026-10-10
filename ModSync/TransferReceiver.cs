using System;
using System.IO;
using System.Linq;

namespace AMP.ModSync {
    // Writes one incoming mod folder into a staging folder. Chunks must arrive in order (the sender sends them in order over a reliable stream).
    public sealed class TransferReceiver : IDisposable {
        private readonly TransferManifest manifest;
        private readonly string stagingFolder;
        private readonly string[] safePaths;
        private readonly long[] written;
        private readonly bool[] finished;
        private int remaining;
        private int openIndex = -1;
        private FileStream open;

        public long TotalBytes    { get; private set; }
        public long ReceivedBytes { get; private set; }
        public bool Complete      { get; private set; }

        private TransferReceiver(TransferManifest manifest, string stagingFolder, string[] safePaths) {
            this.manifest      = manifest;
            this.stagingFolder = stagingFolder;
            this.safePaths     = safePaths;
            written            = new long[manifest.files.Count];
            finished           = new bool[manifest.files.Count];
            remaining          = manifest.files.Count;
            TotalBytes         = manifest.TotalBytes;
            Complete           = remaining == 0;
        }

        // Throws InvalidDataException for a manifest we must not accept
        public static TransferReceiver Start(TransferManifest manifest, string stagingFolder, long maxBytes) {
            if(manifest == null || manifest.files == null) throw new InvalidDataException("No file list.");
            string[] safe = manifest.files.Select(f => f == null ? null : TransferRules.NormalizeRelativePath(f.path)).ToArray();
            if(safe.Any(p => p == null)) throw new InvalidDataException("Unsafe file path in mod.");
            if(safe.Distinct(StringComparer.OrdinalIgnoreCase).Count() != safe.Length) throw new InvalidDataException("Duplicate file in mod.");
            if(manifest.files.Any(f => f.size < 0)) throw new InvalidDataException("Invalid file size.");
            if(manifest.TotalBytes > maxBytes) throw new InvalidDataException("Mod is larger than the allowed download size.");

            if(Directory.Exists(stagingFolder)) Directory.Delete(stagingFolder, true);
            Directory.CreateDirectory(stagingFolder);
            return new TransferReceiver(manifest, stagingFolder, safe);
        }

        // True if the chunk was written (acknowledge it); false if it doesn't fit the manifest (abort the transfer)
        public bool OnChunk(int fileIndex, long offset, byte[] data) {
            if(Complete || data == null || fileIndex < 0 || fileIndex >= written.Length) return false;
            if(finished[fileIndex]) return false; // a repeated chunk must never reopen (and truncate) a finished file
            if(offset != written[fileIndex]) return false;
            if(offset + data.Length > manifest.files[fileIndex].size) return false;
            if(fileIndex < openIndex) return false;

            if(fileIndex != openIndex) {
                CloseOpen();
                string path = Path.Combine(stagingFolder, safePaths[fileIndex].Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                open = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                openIndex = fileIndex;
            }

            open.Write(data, 0, data.Length);
            written[fileIndex] += data.Length;
            ReceivedBytes      += data.Length;
            if(written[fileIndex] == manifest.files[fileIndex].size) {
                CloseOpen();
                finished[fileIndex] = true;
                remaining--;
            }

            Complete = remaining == 0;
            return true;
        }

        private void CloseOpen() {
            if(open != null) open.Dispose();
            open = null;
            openIndex = -1;
        }

        public void Dispose() { CloseOpen(); }
    }
}
