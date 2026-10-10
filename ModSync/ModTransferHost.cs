using AMP.Data;
using AMP.Logging;
using AMP.Network.Data;
using AMP.Network.Packets.Implementation;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ServerType = AMP.Network.Server.Server;

namespace AMP.ModSync {
    // Server side: sends the mod folders a joiner asks for, one background thread per joiner.
    // Packet handlers run on network threads, the upload threads never touch Unity.
    internal static class ModTransferHost {
        private const int WINDOW = 8;                 // unacknowledged chunks per joiner: bounds the delay added to gameplay packets
        private const int MAX_REQUEST_PARTS = 10;     // a request is a short list of folder names
        private const int VERIFY_WAIT_MS = 10 * 60 * 1000; // time a joiner gets to verify one mod before the next one starts

        private class ActiveUpload {
            public ClientData client;
            public volatile bool cancelled;
            public volatile int transferId;
            public volatile TransferSender sender;
            public readonly SemaphoreSlim readyNext = new SemaphoreSlim(0); // released when the joiner has verified a mod

            public void Cancel() {
                cancelled = true;
                TransferSender current = sender;
                if(current != null) current.Cancel();
                readyNext.Release(); // wakes the upload thread if it waits for the joiner
            }
        }

        private static readonly ConcurrentDictionary<int, ActiveUpload> uploads = new ConcurrentDictionary<int, ActiveUpload>();
        private static readonly MessageAssembler assembler = new MessageAssembler();
        private static int nextTransferId;

        // Network thread
        internal static void OnPacket(ClientData client, ModSyncPacket packet) {
            switch(packet.kind) {
                case ModSyncPacket.MODS_REQUEST:    OnRequest(client, packet); break;
                case ModSyncPacket.CHUNK_ACK: {
                    ActiveUpload upload;
                    if(uploads.TryGetValue(client.ClientId, out upload) && upload.transferId == packet.transferId) {
                        TransferSender sender = upload.sender;
                        if(sender != null) sender.Ack();
                    }
                    break;
                }
                case ModSyncPacket.READY_NEXT: {
                    ActiveUpload upload;
                    if(uploads.TryGetValue(client.ClientId, out upload) && upload.transferId == packet.transferId) upload.readyNext.Release();
                    break;
                }
                case ModSyncPacket.TRANSFER_CANCEL: {
                    ActiveUpload upload;
                    if(uploads.TryGetValue(client.ClientId, out upload)) upload.Cancel();
                    break;
                }
            }
        }

        internal static void OnClientDisconnected(ClientData client) {
            assembler.Forget(client.ClientId + ":");
            ActiveUpload upload;
            if(uploads.TryRemove(client.ClientId, out upload)) upload.Cancel();
        }

        internal static void StopAll() {
            assembler.Forget("");
            foreach(int id in uploads.Keys.ToList()) {
                ActiveUpload upload;
                if(uploads.TryRemove(id, out upload)) upload.Cancel();
            }
        }

        private static void OnRequest(ClientData client, ModSyncPacket packet) {
            if(packet.parts < 1 || packet.parts > MAX_REQUEST_PARTS) return;
            if(!ModSyncHost.IsSettingUp(client)) return; // only joiners that still have to sort out their mods may download
            string json = assembler.Add(client.ClientId + ":" + packet.transferId, packet.part, packet.parts, packet.data);
            if(json == null) return;

            ModsRequest request;
            try {
                request = JsonConvert.DeserializeObject<ModsRequest>(json);
            } catch(JsonException) {
                return;
            }
            if(request == null || request.folders == null) return;

            ServerType server = ModManager.serverInstance;
            List<ModEntry> hostMods = ModSyncHost.HostMods;
            string modsFolder = ModSyncHost.HostModsFolder;
            if(server == null || hostMods == null || modsFolder == null) {
                SendEnd(server, client, 0, false, "The host's mod list isn't ready yet.");
                return;
            }

            // Only folders from the host's own scan, never AMP itself, in the order asked; names are matched, not used as paths
            List<ModEntry> mods = request.folders.Where(f => f != null).Distinct()
                .Select(f => hostMods.FirstOrDefault(m => m.folder == f && !m.isAmp))
                .Where(m => m != null).ToList();
            if(mods.Count == 0) {
                SendEnd(server, client, 0, false, "The host doesn't offer these mods.");
                return;
            }

            var upload = new ActiveUpload { client = client };
            lock(uploads) {
                ActiveUpload previous;
                if(uploads.TryGetValue(client.ClientId, out previous)) previous.Cancel();
                uploads[client.ClientId] = upload;
            }

            var thread = new Thread(() => Upload(server, upload, mods, modsFolder)) {
                IsBackground = true,
                Name = "AMP mod upload " + client.ClientId,
            };
            thread.Start();
        }

        // Upload thread
        private static void Upload(ServerType server, ActiveUpload upload, List<ModEntry> mods, string modsFolder) {
            ClientData client = upload.client;
            int currentId = 0;
            try {
                long maxBytes = Math.Max(0, ModManager.safeFile.hostingSettings.maxModTransferMB) * 1024L * 1024L;
                int bytesPerSecond = (int) Math.Min(int.MaxValue, Math.Max(0, ModManager.safeFile.hostingSettings.modUploadKBps) * 1024L);

                foreach(ModEntry mod in mods) {
                    if(upload.cancelled || ModManager.serverInstance != server) return;

                    currentId = Interlocked.Increment(ref nextTransferId);
                    string folder = Path.Combine(modsFolder, mod.folder);
                    var manifest = new TransferManifest {
                        transferId = currentId,
                        folder     = mod.folder,
                        hash       = mod.hash,
                        modioId    = mod.modioId,
                        files      = TransferRules.ListTransferFiles(folder),
                    };
                    if(manifest.TotalBytes > maxBytes) {
                        Log.Info(Defines.SERVER, $"Mod sync: refusing to send {mod.name} to {client.ClientName}, it is larger than maxModTransferMB.");
                        SendEnd(server, client, currentId, false, $"{mod.name} is too large for this host's download limit.");
                        return;
                    }

                    var sender = new TransferSender(manifest, folder, WINDOW, bytesPerSecond, (fileIndex, offset, data) => {
                        server.netamiteServer.SendTo(client, new ModFilePacket(manifest.transferId, fileIndex, offset, data));
                    });
                    upload.transferId = currentId;
                    upload.sender = sender;
                    if(upload.cancelled) { sender.Cancel(); return; } // cancelled between the check above and the sender being visible

                    foreach(ModSyncPacket packet in ModSyncPacket.Create(ModSyncPacket.TRANSFER_START, JsonConvert.SerializeObject(manifest))) {
                        server.netamiteServer.SendTo(client, packet);
                    }
                    bool ok = sender.Run();
                    if(upload.cancelled) return; // the joiner asked to stop or left: nothing more to say
                    SendEnd(server, client, currentId, ok, ok ? "" : $"Sending {mod.name} was interrupted.");
                    if(!ok) return;
                    if(mod != mods[mods.Count - 1] && !WaitForReady(upload)) {
                        if(!upload.cancelled) SendEnd(server, client, currentId, false, "Verifying the mod took too long.");
                        return;
                    }
                }
            } catch(Exception e) {
                Log.Err(Defines.SERVER, "Mod sync: sending mods to " + client.ClientName + " failed. " + e.Message);
                if(!upload.cancelled) SendEnd(server, client, currentId, false, "The host couldn't send the mod.");
            } finally {
                // Forget this upload unless a newer one already replaced it
                ((ICollection<KeyValuePair<int, ActiveUpload>>) uploads).Remove(new KeyValuePair<int, ActiveUpload>(client.ClientId, upload));
            }
        }

        // The joiner hashes a finished mod before it wants the next one; its acks stop meanwhile, so don't start sending yet
        private static bool WaitForReady(ActiveUpload upload) {
            if(!upload.readyNext.Wait(VERIFY_WAIT_MS)) return false;
            return !upload.cancelled;
        }

        private static void SendEnd(ServerType server, ClientData client, int transferId, bool ok, string error) {
            if(server == null) return;
            try {
                string json = JsonConvert.SerializeObject(new TransferEnd { transferId = transferId, ok = ok, error = error });
                foreach(ModSyncPacket packet in ModSyncPacket.Create(ModSyncPacket.TRANSFER_END, json)) {
                    server.netamiteServer.SendTo(client, packet);
                }
            } catch(Exception e) {
                Log.Err(Defines.SERVER, "Mod sync: couldn't tell " + client.ClientName + " the transfer ended. " + e.Message);
            }
        }
    }
}
