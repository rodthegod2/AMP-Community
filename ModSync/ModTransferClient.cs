using AMP.Data;
using AMP.Logging;
using AMP.Network.Packets.Implementation;
using AMP.Threading;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClientType = AMP.Network.Client.Client;

namespace AMP.ModSync {
    // Joiner side: asks the host for mod folders, receives them into a staging folder, verifies them and moves them into the cache.
    // Download/Cancel run on the main thread, OnPacket/OnChunk on network threads; all state is guarded by `gate`.
    // Disk-heavy work that doesn't need the state (hashing in Commit, eviction) runs outside the lock.
    internal static class ModTransferClient {
        private const int PROGRESS_INTERVAL_MS = 250;
        private const int IDLE_TIMEOUT_MS = 60 * 1000; // no manifest or chunk for this long while waiting for the host: give up

        private static readonly object gate = new object();
        private static readonly MessageAssembler assembler = new MessageAssembler();
        private static bool partialsCleaned;

        // Everything below belongs to one Download; `session` changes whenever it ends, so late work can tell it is stale
        private static int session;
        private static bool active;
        private static ClientType client;
        private static ModCache cache;
        private static Dictionary<string, ModEntry> pending;  // folder -> mod still to receive
        private static List<string> keepHashes;                // every mod of this download, protected from eviction
        private static Action<long, long, string> progress;
        private static Action<bool, string> done;
        private static long totalBytes;
        private static long finishedBytes;                      // bytes of mods already received
        private static DateTime lastProgress;
        private static int requestId;                          // id of our MODS_REQUEST; the host echoes it in every manifest and end message
        private static DateTime lastActivity;
        private static int committing;                         // mods being verified right now: the host is allowed to be quiet then
        private static Timer idleTimer;

        private static TransferReceiver receiver;
        private static ModEntry receiving;
        private static string staging;
        private static int receivingId;

        // Main thread. progress(received, total, mod name) and done(ok, error) are always called on the main thread.
        internal static void Download(List<ModEntry> mods, Action<long, long, string> progress, Action<bool, string> done) {
            Cancel();

            ClientType current = ModManager.clientInstance;
            if(current == null || !ModSyncPaths.Available) {
                Dispatcher.Enqueue(() => done(false, "Not connected."));
                return;
            }

            var modCache = new ModCache(ModSyncPaths.CacheFolder);
            if(!partialsCleaned) {
                modCache.CleanPartial(TimeSpan.FromHours(1)); // folders of a game instance that is downloading right now are younger
                partialsCleaned = true;
            }

            var needed = new List<ModEntry>();
            foreach(ModEntry mod in mods.GroupBy(m => m.folder).Select(g => g.First())) {
                if(modCache.Find(mod.hash) != null) modCache.Touch(mod.hash);
                else needed.Add(mod);
            }
            if(needed.Count == 0) {
                Dispatcher.Enqueue(() => done(true, ""));
                return;
            }

            lock(gate) {
                session++;
                active        = true;
                client        = current;
                cache         = modCache;
                pending       = needed.ToDictionary(m => m.folder);
                keepHashes    = mods.Select(m => m.hash).ToList();
                ModTransferClient.progress = progress;
                ModTransferClient.done     = done;
                totalBytes    = needed.Sum(m => m.sizeBytes);
                finishedBytes = 0;
                lastProgress  = DateTime.MinValue;
                lastActivity  = DateTime.UtcNow;
                committing    = 0;
                receiver      = null;
                receiving     = null;
                staging       = null;
                receivingId   = 0;
            }

            string json = JsonConvert.SerializeObject(new ModsRequest { folders = needed.Select(m => m.folder).ToList() });
            List<ModSyncPacket> packets = ModSyncPacket.Create(ModSyncPacket.MODS_REQUEST, json);
            lock(gate) {
                if(!active) return; // cancelled in the meantime
                requestId = packets[0].transferId;
                int forSession = session;
                idleTimer = new Timer(_ => CheckIdle(forSession), null, 5000, 5000);
            }
            try {
                foreach(ModSyncPacket packet in packets) Send(current, packet);
            } catch(Exception e) {
                Log.Err(Defines.CLIENT, "Mod sync: couldn't request the mods. " + e.Message);
                lock(gate) Finish(false, "Couldn't ask the host for the mods.", false);
            }
        }

        // Main thread (and when a connection ends). Silent: done is not called for a download the caller itself stopped.
        internal static void Cancel() {
            lock(gate) {
                if(!active) return;
                SendCancel();
                Abandon();
            }
        }

        // Network thread
        internal static void OnPacket(ModSyncPacket packet) {
            if(packet.kind != ModSyncPacket.TRANSFER_START && packet.kind != ModSyncPacket.TRANSFER_END) return;
            if(packet.parts < 1) return;
            string json = assembler.Add("xfer:" + packet.kind + ":" + packet.transferId, packet.part, packet.parts, packet.data);
            if(json == null) return;

            if(packet.kind == ModSyncPacket.TRANSFER_START) OnStart(json);
            else OnEnd(json);
        }

        private static void OnEnd(string json) {
            TransferEnd end;
            try {
                end = JsonConvert.DeserializeObject<TransferEnd>(json);
            } catch(JsonException) {
                return;
            }
            if(end == null || end.ok) return; // success is detected by the receiver having every byte
            lock(gate) {
                if(!IsCurrent() || end.requestId != requestId) return; // an answer to an older request
                Finish(false, string.IsNullOrEmpty(end.error) ? "The host stopped the download." : end.error, false);
            }
        }

        private static void OnStart(string json) {
            TransferManifest manifest;
            try {
                manifest = JsonConvert.DeserializeObject<TransferManifest>(json);
            } catch(Exception) { // whatever the host sent, it must not escape into the network thread
                manifest = null;
            }

            ModEntry finishedMod = null;
            TransferReceiver finishedReceiver = null;
            string finishedStaging = null;
            ModCache finishedCache = null;
            int finishedSession = 0;
            int finishedId = 0;

            lock(gate) {
                if(!IsCurrent()) return;
                if(manifest != null && manifest.requestId != requestId) return; // a leftover of an older request must never become active
                ModEntry expected = null;
                // The folder name becomes a cache entry field: it must be one we asked for and a plain single folder name
                if(manifest == null || manifest.folder == null || receiver != null
                   || TransferRules.NormalizeRelativePath(manifest.folder) != manifest.folder || manifest.folder.Contains("/")
                   || !pending.TryGetValue(manifest.folder, out expected) || manifest.hash != expected.hash) {
                    Finish(false, "The host sent a mod that wasn't asked for.", true);
                    return;
                }

                string folder = null;
                try {
                    long maxBytes = expected.sizeBytes * 2 + 1024 * 1024;
                    long cacheBytes = Math.Max(0, ModManager.safeFile.modSettings.modCacheMaxMB) * 1024L * 1024L;
                    if(cacheBytes > 0) maxBytes = Math.Min(maxBytes, cacheBytes); // a mod that can't fit the cache is not worth receiving
                    folder = cache.NewStagingFolder();
                    receiver    = TransferReceiver.Start(manifest, folder, maxBytes);
                    staging     = folder;
                    receiving   = expected;
                    receivingId = manifest.transferId;
                    lastActivity = DateTime.UtcNow;
                } catch(Exception e) {
                    DeleteLater(folder);
                    Finish(false, e is InvalidDataException ? e.Message : "Couldn't prepare the download folder. " + e.Message, true);
                    return;
                }

                if(receiver.Complete) { // a mod without files: nothing will be sent, go straight to verification
                    finishedMod = TakeFinished(out finishedReceiver, out finishedStaging, out finishedId);
                    finishedCache = cache;
                    finishedSession = session;
                }
            }
            if(finishedMod != null) StartCommit(finishedSession, finishedCache, finishedMod, finishedReceiver, finishedStaging, finishedId);
        }

        // Network thread
        internal static void OnChunk(ModFilePacket packet) {
            ModEntry finishedMod = null;
            TransferReceiver finishedReceiver = null;
            string finishedStaging = null;
            ModCache finishedCache = null;
            int finishedSession = 0;
            int finishedId = 0;
            Action<long, long, string> report = null;
            long received = 0, total = 0;
            string modName = null;

            lock(gate) {
                if(!IsCurrent() || receiver == null || packet.transferId != receivingId) return;
                lastActivity = DateTime.UtcNow;
                byte[] bytes = TransferRules.DecodeChunk(packet.data); // an empty file arrives as one empty chunk, which must be acknowledged too
                if(bytes == null) {
                    Finish(false, "The host sent data that isn't a valid file chunk.", true);
                    return;
                }

                bool accepted;
                try {
                    accepted = receiver.OnChunk(packet.fileIndex, packet.offset, bytes);
                } catch(Exception e) {
                    Finish(false, "Couldn't save the download. " + e.Message, true);
                    return;
                }
                if(!accepted) {
                    Finish(false, "The host sent data that doesn't fit the mod.", true);
                    return;
                }

                try {
                    Send(client, new ModSyncPacket(ModSyncPacket.CHUNK_ACK, packet.transferId, 0, 1, ""));
                } catch(Exception e) {
                    Log.Err(Defines.CLIENT, "Mod sync: couldn't acknowledge a chunk. " + e.Message);
                }

                if(receiver.Complete) {
                    finishedMod = TakeFinished(out finishedReceiver, out finishedStaging, out finishedId);
                    finishedCache = cache;
                    finishedSession = session;
                } else if((DateTime.UtcNow - lastProgress).TotalMilliseconds >= PROGRESS_INTERVAL_MS) {
                    lastProgress = DateTime.UtcNow;
                    report  = progress;
                    received = finishedBytes + receiver.ReceivedBytes;
                    total    = totalBytes;
                    modName  = receiving.name;
                }
            }

            if(report != null) {
                string name = modName;
                long r = received, t = total;
                Dispatcher.Enqueue(() => report(r, t, name));
            }
            if(finishedMod != null) StartCommit(finishedSession, finishedCache, finishedMod, finishedReceiver, finishedStaging, finishedId);
        }

        // Verifying and caching hashes the whole mod (seconds for a big one): a worker does it, never the network receive thread
        private static void StartCommit(int forSession, ModCache modCache, ModEntry mod, TransferReceiver finished, string stagingFolder, int transferId) {
            Task.Run(() => Commit(forSession, modCache, mod, finished, stagingFolder, transferId));
        }

        private static void Commit(int forSession, ModCache modCache, ModEntry mod, TransferReceiver finished, string stagingFolder, int transferId) {
            finished.Dispose();
            CacheEntry entry = null;
            try {
                entry = modCache.Commit(stagingFolder, mod);
            } catch(Exception e) {
                Log.Err(Defines.CLIENT, "Mod sync: committing " + mod.name + " failed. " + e.Message);
                TryDelete(stagingFolder);
            }

            ModCache evictCache = null;
            List<string> keep = null;
            lock(gate) {
                if(session != forSession) return; // cancelled meanwhile; whatever was cached stays valid
                committing--;
                lastActivity = DateTime.UtcNow;
                if(entry == null) {
                    Finish(false, mod.name + " failed verification.", true);
                    return;
                }
                pending.Remove(mod.folder);
                if(pending.Count > 0) { // the host waits for this before it starts the next mod
                    try {
                        Send(client, new ModSyncPacket(ModSyncPacket.READY_NEXT, transferId, 0, 1, ""));
                    } catch(Exception e) {
                        Log.Err(Defines.CLIENT, "Mod sync: couldn't tell the host to continue. " + e.Message);
                    }
                    return;
                }

                evictCache = cache;
                keep = keepHashes;
            }

            try {
                evictCache.Evict(Math.Max(0, ModManager.safeFile.modSettings.modCacheMaxMB) * 1024L * 1024L, keep);
            } catch(Exception e) {
                Log.Err(Defines.CLIENT, "Mod sync: cleaning the mod cache failed. " + e.Message);
            }
            lock(gate) {
                if(session != forSession) return;
                Finish(true, "", false);
            }
        }

        // Inside the lock: hands out the finished receiver and staging folder, the download waits for the next manifest
        private static ModEntry TakeFinished(out TransferReceiver finished, out string stagingFolder, out int transferId) {
            transferId = receivingId;
            ModEntry mod = receiving;
            finished = receiver;
            stagingFolder = staging;
            finishedBytes += finished.TotalBytes;
            committing++;
            receiver = null;
            receiving = null;
            staging = null;
            receivingId = 0;
            return mod;
        }

        private static bool IsCurrent() {
            return active && client != null && ModManager.clientInstance == client;
        }

        // Inside the lock: ends the download and reports it
        private static void Finish(bool ok, string error, bool tellHost) {
            if(!active) return;
            if(tellHost) SendCancel();
            Action<bool, string> callback = done;
            Abandon();
            Dispatcher.Enqueue(() => callback(ok, error));
        }

        // Inside the lock: drops the receiver and its half-written files and forgets the download
        private static void Abandon() {
            if(receiver != null) receiver.Dispose();
            DeleteLater(staging); // recursive deletes don't belong under the lock or on the main thread
            if(idleTimer != null) idleTimer.Dispose();
            idleTimer = null;
            committing = 0;
            session++;
            active = false;
            client = null;
            cache = null;
            pending = null;
            keepHashes = null;
            progress = null;
            done = null;
            receiver = null;
            receiving = null;
            staging = null;
            receivingId = 0;
            assembler.Forget("");
        }

        private static void SendCancel() {
            if(client == null || ModManager.clientInstance != client) return;
            try {
                Send(client, new ModSyncPacket(ModSyncPacket.TRANSFER_CANCEL, receivingId, 0, 1, ""));
            } catch(Exception e) {
                Log.Err(Defines.CLIENT, "Mod sync: couldn't tell the host to stop. " + e.Message);
            }
        }

        // Straight to the connection this download belongs to, never to whichever one is current by then
        private static void Send(ClientType target, ModSyncPacket packet) {
            target.netclient.SendReliable(packet);
        }

        private static void DeleteLater(string dir) {
            if(string.IsNullOrEmpty(dir)) return;
            Task.Run(() => TryDelete(dir));
        }

        // Timer thread: the host went quiet while we were waiting for it
        private static void CheckIdle(int forSession) {
            lock(gate) {
                if(!active || session != forSession || committing > 0) return;
                if((DateTime.UtcNow - lastActivity).TotalMilliseconds < IDLE_TIMEOUT_MS) return;
                Finish(false, "The host stopped sending the mods.", true);
            }
        }

        private static void TryDelete(string dir) {
            try {
                if(!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) Directory.Delete(dir, true);
            } catch(IOException) {
            } catch(UnauthorizedAccessException) {
            }
        }
    }
}
