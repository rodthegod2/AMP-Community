using AMP.Data;
using AMP.Extension;
using AMP.GameInteraction.Components;
using AMP.Logging;
using AMP.Network.Packets.Implementation;
using AMP.Threading;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using ClientType = AMP.Network.Client.Client;

namespace AMP.ModSync {
    // Joiner side: compares the host's mods with ours, reports back, and shows what differs.
    internal static class ModSyncClient {
        private static readonly MessageAssembler assembler = new MessageAssembler();

        // The connection that is waiting for the player to decide; once it is gone (or replaced) nobody is waiting
        private static ClientType awaitingClient;

        internal static HostModSet HostSet { get; private set; }
        internal static SyncPlan   Plan    { get; private set; }

        // True while the mod sync screen has to stay up: the plan blocks joining and the player hasn't chosen yet
        internal static bool AwaitingDecision {
            get { return awaitingClient != null && awaitingClient == ModManager.clientInstance; }
        }

        // Main thread, when a new connection starts: nothing of the previous server may leak into it
        internal static void Reset() {
            assembler.Forget("");
            ModTransferClient.Cancel();
            ModSwapClient.Reset();
            HostSet       = null;
            Plan           = null;
            awaitingClient = null;
        }

        internal static void OnPacket(ModSyncPacket packet) {
            if(packet.kind != ModSyncPacket.HOST_MOD_SET) return;
            ClientType client = ModManager.clientInstance;
            if(client == null) return;
            string json = assembler.Add("host:" + packet.transferId, packet.part, packet.parts, packet.data);
            if(json == null) return;

            if(!ModSyncPaths.Available) {
                Report(client, JoinerStatus.Ready, "mod sync unavailable on this client");
                return;
            }

            HostModSet set;
            try {
                set = JsonConvert.DeserializeObject<HostModSet>(json);
            } catch(JsonException e) {
                Log.Err(Defines.CLIENT, "Mod sync: the host sent an unreadable mod list. " + e.Message);
                Report(client, JoinerStatus.Ready, "unreadable mod list");
                return;
            }
            if(set == null) return;

            // Tells the host not to give up on us while a big first scan runs, and the player why nothing happens yet
            Report(client, JoinerStatus.Checking, "");
            Dispatcher.Enqueue(() => {
                if(ModManager.clientInstance == client && !HostHasGreeted()) ShowNotice("Checking your mods...");
            });

            string modsFolder  = ModSyncPaths.ModsFolder;
            string cacheFolder = ModSyncPaths.CacheFolder;
            string platform    = ModSyncPaths.Platform;
            bool samePlatform  = string.Equals(set.platform, platform, StringComparison.OrdinalIgnoreCase);

            Task.Run(() => {
                List<ModEntry> local = ModInventory.Scan(modsFolder, cacheFolder, samePlatform);
                return SyncPlanner.Plan(set, local, ModInventory.ReadLoadOrder(modsFolder), platform);
            }).ContinueWith(t => Dispatcher.Enqueue(() => {
                if(t.IsFaulted) {
                    // A scan problem must not lock anyone out
                    Log.Err(Defines.CLIENT, "Mod sync: checking your mods failed. " + t.Exception.GetBaseException().Message);
                    Report(client, JoinerStatus.Ready, "mod check failed");
                    return;
                }
                Handle(client, set, t.Result);
            }));
        }

        private static void Handle(ClientType client, HostModSet set, SyncPlan plan) {
            if(ModManager.clientInstance != client) return; // left or switched servers while the scan was running
            if(HostHasGreeted()) return;                    // the host gave up waiting and let us in, nothing left to decide

            HostSet = set;
            Plan    = plan;
            if(plan.ampDiffers) Log.Warn(Defines.CLIENT, "Mod sync: your AMP build differs from the host's.");

            if(!plan.BlocksJoin) {
                List<SyncStep> missing = plan.Of(SyncAction.Unavailable).ToList();
                if(missing.Count > 0) {
                    ShowNotice("Joining without mods that aren't available on your platform:\n" + string.Join(", ", missing.Select(s => s.Mod.name)));
                }
                Report(client, JoinerStatus.Ready, missing.Count > 0 ? plan.Describe() : "");
                return;
            }

            awaitingClient = client;
            Report(client, JoinerStatus.NeedsSetup, plan.Describe());
            // If the book isn't open yet, UpdateConnectionScreen shows the page when it is
            UI.IngameModUI ui = UI.IngameModUI.currentUI;
            if(ui != null) ui.ShowModSync(plan.Describe(), set.mode != ModSyncMode.Enforce);
            ShowNotice("Your mods don't match the host's.\nOpen the book: Mods > AMP.");
        }

        // Only offered when the host runs mod sync in "warn" mode
        internal static void JoinAnyway() {
            if(!AwaitingDecision) return;
            ClientType client = awaitingClient;
            awaitingClient = null;
            Report(client, JoinerStatus.Ready, Plan.Describe());
        }

        internal static void Decline() {
            ClientType client = ModManager.clientInstance;
            awaitingClient = null;
            Report(client, JoinerStatus.Declined, "");
            ModManager.StopClient();
        }

        // ServerInfoPacket is the first thing of the host's greeting
        private static bool HostHasGreeted() {
            var sync = ModManager.clientSync;
            return sync != null && sync.syncData.server_config != null;
        }

        // Only sends while the connection it was made for is still the active one
        private static void Report(ClientType client, string status, string detail) {
            if(client == null || ModManager.clientInstance != client) return;
            foreach(ModSyncPacket packet in BuildReport(status, detail)) {
                packet.SendToServerReliable();
            }
        }

        // The host drops reports of more than MAX_REPORT_PARTS packets, so a long description is shortened until it fits
        private static List<ModSyncPacket> BuildReport(string status, string detail) {
            detail = detail ?? "";
            while(true) {
                string json = JsonConvert.SerializeObject(new JoinerReport { platform = ModSyncPaths.Platform, status = status, detail = detail });
                if(detail.Length == 0 || MessageChunks.Split(json).Count <= ModSyncHost.MAX_REPORT_PARTS) {
                    return ModSyncPacket.Create(ModSyncPacket.JOINER_REPORT, json);
                }
                int length = detail.Length / 2;
                if(length > 0 && char.IsHighSurrogate(detail[length - 1])) length--; // don't leave half an emoji
                detail = detail.Substring(0, length);
            }
        }

        internal static void ShowNotice(string text) {
            TextDisplay.ShowTextDisplay(new DisplayTextPacket("modsync", text, Color.yellow, Vector3.forward * 2, true, true, 15));
        }
    }
}
