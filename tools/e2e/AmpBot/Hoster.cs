using AMP.ModSync;
using Netamite.Network.Packet;
using Netamite.Server.Data;
using Netamite.Server.Implementation;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace AmpBot {
    // --host: behaves like an AMP host towards joiners (ModSyncHost + ModTransferHost + the start of Server.GreetPlayer)
    public class Hoster {
        // Defines.MOD_VERSION of the AMP build under test; a different value only makes the joiner show a warning
        public const string AmpVersion = "Beta 6";
        private const int Window = 8;
        private const int ReadyWaitMs = 120 * 1000;

        private readonly int port;
        private readonly string modsFolder;
        private readonly Expectations x;
        private readonly Checklist list;
        private readonly MessageAssembler assembler = new MessageAssembler();
        private readonly ConcurrentDictionary<int, Upload> uploads = new ConcurrentDictionary<int, Upload>();
        private readonly List<string> reportsSeen = new List<string>();
        private IPServer server;
        private List<ModEntry> hostMods;
        private string hostModSetJson;
        private int nextTransferId;
        private int greetings;

        private class Upload {
            public volatile bool cancelled;
            public volatile int transferId;
            public volatile TransferSender sender;
            public readonly SemaphoreSlim readyNext = new SemaphoreSlim(0);
        }

        public Hoster(int port, string modsFolder, string cacheFolder, Expectations x, Checklist list) {
            this.port = port;
            this.modsFolder = modsFolder;
            this.x = x;
            this.list = list;

            for(int i = 0; i < x.reports.Count; i++) list.Expect("report " + (i + 1) + " " + x.reports[i]);
            foreach(string folder in x.served) list.Expect("served " + folder);
            for(int i = 1; i <= x.greetings; i++) list.Expect("greeting " + i);

            List<ModEntry> mods = ModInventory.Scan(modsFolder, cacheFolder, true);
            hostMods = mods.Where(m => m.enabled).ToList();
            ModEntry amp = mods.FirstOrDefault(m => m.isAmp && m.enabled);
            hostModSetJson = JsonConvert.SerializeObject(new HostModSet {
                platform  = "windows",
                mode      = x.modSyncMode,
                mods      = hostMods,
                loadOrder = ModInventory.ReadLoadOrder(modsFolder),
                ampBuild  = amp != null ? amp.hash : "",
            });
            Out.Info("hosting " + hostMods.Count + " mods: " + string.Join(", ", hostMods.Select(m => m.folder)));
        }

        public void Start() {
            server = new IPServer((short) port, 8);
            server.OnConnect += OnConnect;
            server.OnDataReceived += OnPacket;
            server.OnDisconnect += (c, reason) => {
                Out.Info("client " + c.ClientId + " left: " + reason);
                Upload upload;
                if(uploads.TryRemove(c.ClientId, out upload)) Cancel(upload);
            };
            server.Start();
            Out.Info("listening on port " + port);
        }

        public void Stop() {
            try { server?.Stop(); } catch(Exception) { }
        }

        private void OnConnect(ClientInformation c) {
            Out.Info("client " + c.ClientId + " (" + c.ClientName + ") connected, sending the mod list");
            foreach(ModSyncPacket packet in ModSyncPacket.Create(ModSyncPacket.HOST_MOD_SET, hostModSetJson)) server.SendTo(c, packet);
        }

        private void OnPacket(ClientInformation c, NetPacket p) {
            try {
                ModSyncPacket packet = p as ModSyncPacket;
                if(packet == null) {
                    if(p is LevelChangePacket) Out.Info("client " + c.ClientId + " sent a level change: " + ((LevelChangePacket) p).level);
                    return;
                }
                Upload upload;
                switch(packet.kind) {
                    case ModSyncPacket.JOINER_REPORT: {
                        string json = assembler.Add(c.ClientId + ":" + packet.transferId, packet.part, packet.parts, packet.data);
                        if(json != null) OnReport(c, JsonConvert.DeserializeObject<JoinerReport>(json));
                        break;
                    }
                    case ModSyncPacket.MODS_REQUEST: {
                        string json = assembler.Add(c.ClientId + ":" + packet.transferId, packet.part, packet.parts, packet.data);
                        if(json != null) OnRequest(c, packet.transferId, JsonConvert.DeserializeObject<ModsRequest>(json));
                        break;
                    }
                    case ModSyncPacket.CHUNK_ACK:
                        if(uploads.TryGetValue(c.ClientId, out upload) && upload.transferId == packet.transferId) upload.sender?.Ack();
                        break;
                    case ModSyncPacket.READY_NEXT:
                        if(uploads.TryGetValue(c.ClientId, out upload) && upload.transferId == packet.transferId) upload.readyNext.Release();
                        break;
                    case ModSyncPacket.TRANSFER_CANCEL:
                        Out.Info("client " + c.ClientId + " cancelled the download");
                        if(uploads.TryGetValue(c.ClientId, out upload)) Cancel(upload);
                        break;
                }
            } catch(Exception e) {
                list.Fail("handling " + p.GetType().Name + " threw " + e);
            }
        }

        private void OnReport(ClientInformation c, JoinerReport report) {
            if(report == null) return;
            Out.Info("client " + c.ClientId + " reports " + report.status + " (" + report.platform + ")" + (string.IsNullOrEmpty(report.detail) ? "" : ": " + report.detail));
            if(report.status != JoinerStatus.Checking) {
                lock(reportsSeen) {
                    reportsSeen.Add(report.status);
                    int n = reportsSeen.Count;
                    if(n <= x.reports.Count) {
                        if(x.reports[n - 1] == report.status) list.Mark("report " + n + " " + report.status);
                        else list.Fail("report " + n + " was " + report.status + ", expected " + x.reports[n - 1]);
                    }
                }
            }
            if(report.status == JoinerStatus.Ready) Greet(c);
        }

        // The part of Server.GreetPlayer a joiner needs to count as joined: ServerInfo first, then the settings snapshot
        private void Greet(ClientInformation c) {
            server.SendTo(c, new ServerInfoPacket(AmpVersion, server.MaxClients));
            if(x.sendSnapshot.Count > 0) {
                string json = JsonConvert.SerializeObject(new SettingsMessage { values = x.sendSnapshot });
                foreach(ModSyncPacket packet in ModSyncPacket.Create(ModSyncPacket.SETTINGS_SNAPSHOT, json)) server.SendTo(c, packet);
            }
            list.Mark("greeting " + Interlocked.Increment(ref greetings), "client " + c.ClientId);
        }

        private void OnRequest(ClientInformation c, int requestId, ModsRequest request) {
            if(request == null || request.folders == null) return;
            List<ModEntry> mods = request.folders.Distinct().Select(f => hostMods.FirstOrDefault(m => m.folder == f)).Where(m => m != null).ToList();
            Out.Info("client " + c.ClientId + " requests " + string.Join(", ", request.folders));
            var upload = new Upload();
            uploads[c.ClientId] = upload;
            new Thread(() => Send(c, requestId, upload, mods)) { IsBackground = true, Name = "upload " + c.ClientId }.Start();
        }

        // Same protocol as ModTransferHost.Upload: manifest, chunks with an ack window, end; READY_NEXT before the next mod
        private void Send(ClientInformation c, int requestId, Upload upload, List<ModEntry> mods) {
            try {
                foreach(ModEntry mod in mods) {
                    if(upload.cancelled) return;
                    string folder = Path.Combine(modsFolder, mod.folder);
                    var manifest = new TransferManifest {
                        requestId = requestId, transferId = Interlocked.Increment(ref nextTransferId),
                        folder = mod.folder, hash = mod.hash, modioId = mod.modioId, files = TransferRules.ListTransferFiles(folder),
                    };
                    var sender = new TransferSender(manifest, folder, Window, 0,
                        (file, offset, data) => server.SendTo(c, new ModFilePacket(manifest.transferId, file, offset, Convert.ToBase64String(data))));
                    upload.transferId = manifest.transferId;
                    upload.sender = sender;
                    foreach(ModSyncPacket packet in ModSyncPacket.Create(ModSyncPacket.TRANSFER_START, JsonConvert.SerializeObject(manifest))) server.SendTo(c, packet);
                    bool ok = sender.Run();
                    if(upload.cancelled) return;
                    End(c, requestId, manifest.transferId, ok, ok ? "" : "Sending " + mod.name + " was interrupted.");
                    if(!ok) { list.Fail("sending " + mod.folder + " failed (no acks?)"); return; }
                    list.Mark("served " + mod.folder, manifest.files.Count + " files, " + manifest.TotalBytes + " bytes");
                    if(mod != mods[mods.Count - 1] && !upload.readyNext.Wait(ReadyWaitMs)) { list.Fail("no READY_NEXT after " + mod.folder); return; }
                }
            } catch(Exception e) {
                list.Fail("upload threw " + e);
            }
        }

        private void End(ClientInformation c, int requestId, int transferId, bool ok, string error) {
            string json = JsonConvert.SerializeObject(new TransferEnd { requestId = requestId, transferId = transferId, ok = ok, error = error });
            foreach(ModSyncPacket packet in ModSyncPacket.Create(ModSyncPacket.TRANSFER_END, json)) server.SendTo(c, packet);
        }

        private static void Cancel(Upload upload) {
            upload.cancelled = true;
            upload.sender?.Cancel();
            upload.readyNext.Release();
        }
    }
}
