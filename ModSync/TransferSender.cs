using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace AMP.ModSync {
    // Sends one mod folder as 32 KB chunks. Blocking: run it on its own thread.
    // At most `window` chunks are unacknowledged at any time, so the shared reliable queue (and gameplay latency) stays small.
    public sealed class TransferSender {
        public const int ChunkSize = 32 * 1024; // packet stays far below Netamite's 64 KB limit

        public int AckTimeoutMs = 30 * 1000;

        private readonly TransferManifest manifest;
        private readonly string sourceFolder;
        private readonly int window;
        private readonly int bytesPerSecond;               // 0 = unlimited
        private readonly Action<int, long, byte[]> sendChunk; // (file index, offset, data)
        private readonly object gate = new object();
        private int inFlight;
        private volatile bool cancelled;

        public TransferSender(TransferManifest manifest, string sourceFolder, int window, int bytesPerSecond, Action<int, long, byte[]> sendChunk) {
            this.manifest       = manifest;
            this.sourceFolder   = sourceFolder;
            this.window         = Math.Max(1, window);
            this.bytesPerSecond = Math.Max(0, bytesPerSecond);
            this.sendChunk      = sendChunk;
        }

        public void Ack() {
            lock(gate) {
                if(inFlight > 0) inFlight--;
                Monitor.PulseAll(gate);
            }
        }

        public void Cancel() {
            cancelled = true;
            lock(gate) Monitor.PulseAll(gate);
        }

        // True when every chunk was sent; false when cancelled, the joiner stopped acking, or a file changed
        public bool Run() {
            var clock = Stopwatch.StartNew();
            long sent = 0;
            for(int index = 0; index < manifest.files.Count; index++) {
                TransferFile file = manifest.files[index];
                using(var stream = new FileStream(Path.Combine(sourceFolder, file.path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, ChunkSize)) {
                    long offset = 0;
                    do {
                        int length = (int) Math.Min(ChunkSize, file.size - offset);
                        byte[] buffer = new byte[length];
                        if(!ReadExactly(stream, buffer)) return false; // file shrank since the manifest was made
                        if(!WaitForCredit()) return false;
                        sendChunk(index, offset, buffer);
                        offset += length;
                        sent   += length;
                        Throttle(sent, clock);
                    } while(offset < file.size);
                }
            }
            return !cancelled;
        }

        private bool WaitForCredit() {
            lock(gate) {
                while(inFlight >= window && !cancelled) {
                    if(!Monitor.Wait(gate, AckTimeoutMs)) return false;
                }
                if(cancelled) return false;
                inFlight++;
                return true;
            }
        }

        private void Throttle(long sent, Stopwatch clock) {
            if(bytesPerSecond == 0) return;
            long due = sent * 1000L / bytesPerSecond;
            long wait = due - clock.ElapsedMilliseconds;
            if(wait > 0) Thread.Sleep((int) Math.Min(wait, 1000));
        }

        private static bool ReadExactly(Stream stream, byte[] buffer) {
            int read = 0;
            while(read < buffer.Length) {
                int n = stream.Read(buffer, read, buffer.Length - read);
                if(n <= 0) return false;
                read += n;
            }
            return true;
        }
    }
}
