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

namespace AMP.ModSync {
    // Joiner side: compares the host's mods with ours, reports back, and shows what differs.
    internal static class ModSyncClient {
        private const int MaxReportDetailChars = 50000;
        private static readonly MessageAssembler assembler = new MessageAssembler();

        internal static HostModSet HostSet { get; private set; }
        internal static SyncPlan   Plan    { get; private set; }

        internal static void OnPacket(ModSyncPacket packet) {
            if(packet.kind != ModSyncPacket.HOST_MOD_SET) return;
            string json = assembler.Add("host:" + packet.transferId, packet.part, packet.parts, packet.data);
            if(json == null) return;

            if(!ModSyncPaths.Available) {
                Report(JoinerStatus.Ready, "mod sync unavailable on this client");
                return;
            }

            HostModSet set;
            try {
                set = JsonConvert.DeserializeObject<HostModSet>(json);
            } catch(JsonException e) {
                Log.Err(Defines.CLIENT, "Mod sync: the host sent an unreadable mod list. " + e.Message);
                Report(JoinerStatus.Ready, "unreadable mod list");
                return;
            }
            if(set == null) return;

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
                    Report(JoinerStatus.Ready, "mod check failed");
                    return;
                }
                Handle(set, t.Result);
            }));
        }

        private static void Handle(HostModSet set, SyncPlan plan) {
            if(ModManager.clientInstance == null) return; // left the server while the scan was running

            HostSet = set;
            Plan    = plan;
            if(plan.ampDiffers) Log.Warn(Defines.CLIENT, "Mod sync: your AMP build differs from the host's.");

            if(!plan.BlocksJoin) {
                List<SyncStep> missing = plan.Of(SyncAction.Unavailable).ToList();
                if(missing.Count > 0) {
                    ShowNotice("Joining without mods that aren't available on your platform:\n" + string.Join(", ", missing.Select(s => s.Mod.name)));
                }
                Report(JoinerStatus.Ready, missing.Count > 0 ? plan.Describe() : "");
                return;
            }

            Report(JoinerStatus.NeedsSetup, plan.Describe());
            bool canJoinAnyway = set.mode != ModSyncMode.Enforce;
            UI.IngameModUI ui = UI.IngameModUI.currentUI;
            if(ui != null) ui.ShowModSync(plan.Describe(), canJoinAnyway);
            ShowNotice("Your mods don't match the host's.\nOpen the book: Mods > AMP.");
        }

        // Only offered when the host runs mod sync in "warn" mode
        internal static void JoinAnyway() {
            if(Plan == null) return;
            Report(JoinerStatus.Ready, Plan.Describe());
        }

        internal static void Decline() {
            Report(JoinerStatus.Declined, "");
            ModManager.StopClient();
        }

        private static void Report(string status, string detail) {
            if(ModManager.clientInstance == null) return;
            // The host drops reports of more than a few packets, so a huge list is cut instead of lost
            if(detail != null && detail.Length > MaxReportDetailChars) detail = detail.Substring(0, MaxReportDetailChars) + "...";
            var report = new JoinerReport { platform = ModSyncPaths.Platform, status = status, detail = detail };
            foreach(ModSyncPacket packet in ModSyncPacket.Create(ModSyncPacket.JOINER_REPORT, JsonConvert.SerializeObject(report))) {
                packet.SendToServerReliable();
            }
        }

        private static void ShowNotice(string text) {
            TextDisplay.ShowTextDisplay(new DisplayTextPacket("modsync", text, Color.yellow, Vector3.forward * 2, true, true, 15));
        }
    }
}
