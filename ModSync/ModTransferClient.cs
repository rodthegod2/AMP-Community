using AMP.Data;
using AMP.Logging;
using AMP.Network.Packets.Implementation;
using AMP.Threading;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClientType = AMP.Network.Client.Client;

namespace AMP.ModSync {
    // Joiner side: asks the host for mod folders, receives them into a staging folder, verifies them and moves them into the cache.
    // Download/Cancel run on the main thread, OnPacket/OnChunk on network threads; all state is guarded by `gate`.
    // Disk-heavy work that doesn't need the state (hashing in Commit, eviction) runs outside the lock.
    internal static class ModTransferClient {
        private const int PROGRESS_INTERVAL_MS = 250;

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
                modCache.CleanPartial();
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
                receiver      = null;
                receiving     = null;
                staging       = null;
                receivingId   = 0;
            }

            string json = JsonConvert.SerializeObject(new ModsRequest { folders = needed.Select(m => m.folder).ToList() });
            try {
                foreach(ModSyncPacket packet in ModSyncPacket.Create(ModSyncPacket.MODS_REQUEST, json)) Send(current, packet);
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
                if(!IsCurrent()) return;
                Finish(false, string.IsNullOrEmpty(end.error) ? "The host stopped the download." : end.error, false);
            }
        }

        private static void OnStart(string json) {
            TransferManifest manifest;
            try {
                manifest = JsonConvert.DeserializeObject<TransferManifest>(json);
            } catch(JsonException) {
                manifest = null;
            }

            ModEntry finishedMod = null;
            TransferReceiver finishedReceiver = null;
            string finishedStaging = null;
            ModCache finishedCache = null;
            int finishedSession = 0;

            lock(gate) {
                if(!IsCurrent()) return;
                ModEntry expected;
                if(manifest == null || manifest.folder == null || receiver != null
                   || !pending.TryGetValue(manifest.folder, out expected) || manifest.hash != expected.hash) {
                    Finish(false, "The host sent a mod that wasn't asked for.", true);
                    return;
                }

                string folder = null;
                try {
                    folder = cache.NewStagingFolder();
                    receiver    = TransferReceiver.Start(manifest, folder, expected.sizeBytes * 2 + 1024 * 1024);
                    staging     = folder;
                    receiving   = expected;
                    receivingId = manifest.transferId;
                } catch(InvalidDataException e) {
                    TryDelete(folder);
                    Finish(false, e.Message, true);
                    return;
                } catch(IOException e) {
                    TryDelete(folder);
                    Finish(false, "Couldn't prepare the download folder. " + e.Message, true);
                    return;
                } catch(UnauthorizedAccessException e) {
                    TryDelete(folder);
                    Finish(false, "Couldn't prepare the download folder. " + e.Message, true);
                    return;
                }

                if(receiver.Complete) { // a mod without files: nothing will be sent, go straight to verification
                    finishedMod = TakeFinished(out finishedReceiver, out finishedStaging);
                    finishedCache = cache;
                    finishedSession = session;
                }
            }
            if(finishedMod != null) Commit(finishedSession, finishedCache, finishedMod, finishedReceiver, finishedStaging);
        }

        // Network thread
        internal static void OnChunk(ModFilePacket packet) {
            ModEntry finishedMod = null;
            TransferReceiver finishedReceiver = null;
            string finishedStaging = null;
            ModCache finishedCache = null;
            int finishedSession = 0;
            Action<long, long, string> report = null;
            long received = 0, total = 0;
            string modName = null;

            lock(gate) {
                if(!IsCurrent() || receiver == null || packet.transferId != receivingId) return;
                if(packet.data == null || packet.data.bytes == null) return;

                bool accepted;
                try {
                    accepted = receiver.OnChunk(packet.fileIndex, packet.offset, packet.data.bytes);
                } catch(IOException e) {
                    Finish(false, "Couldn't save the download. " + e.Message, true);
                    return;
                } catch(UnauthorizedAccessException e) {
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
                    finishedMod = TakeFinished(out finishedReceiver, out finishedStaging);
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
            if(finishedMod != null) Commit(finishedSession, finishedCache, finishedMod, finishedReceiver, finishedStaging);
        }

        // Verifies and caches a fully received mod. Runs on the network thread without the lock: hashing a big mod takes seconds.
        private static void Commit(int forSession, ModCache modCache, ModEntry mod, TransferReceiver finished, string stagingFolder) {
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
                if(entry == null) {
                    Finish(false, mod.name + " failed verification.", true);
                    return;
                }
                pending.Remove(mod.folder);
                if(pending.Count > 0) return; // waiting for the next TRANSFER_START

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
        private static ModEntry TakeFinished(out TransferReceiver finished, out string stagingFolder) {
            ModEntry mod = receiving;
            finished = receiver;
            stagingFolder = staging;
            finishedBytes += finished.TotalBytes;
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
            TryDelete(staging);
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

        private static void TryDelete(string dir) {
            try {
                if(!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) Directory.Delete(dir, true);
            } catch(IOException) {
            } catch(UnauthorizedAccessException) {
            }
        }
    }
}
