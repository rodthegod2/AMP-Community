using AMP.Data;
using AMP.Extension;
using AMP.GameInteraction.Components;
using AMP.Logging;
using AMP.Network.Packets.Implementation;
using AMP.Threading;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using ClientType = AMP.Network.Client.Client;

namespace AMP.ModSync {
    // Joiner side: compares the host's mods with ours, reports back, and shows what differs.
    internal static class ModSyncClient {
        private static readonly MessageAssembler assembler = new MessageAssembler();
        private const int QuestWaitMs = 45 * 1000; // a host still matching Quest versions sends its list again within 30 s
        private static int questWait;              // changes when a newer list arrives or the connection resets: an older wait gives up
        private static int planning;               // 1 once this connection planned with a list: a late resend must not replace a plan in use

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
            Interlocked.Increment(ref questWait);
            Interlocked.Exchange(ref planning, 0);
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
            string versionNote = HostModSet.GameVersionNote(set.gameVersion, ModSyncPaths.GameVersion, string.Equals(set.platform, ModSyncPaths.Platform, StringComparison.OrdinalIgnoreCase));
            if(versionNote != null) {
                Log.Warn(Defines.CLIENT, "Mod sync: " + versionNote.Replace("\n", " "));
                Dispatcher.Enqueue(() => {
                    if(ModManager.clientInstance == client) TextDisplay.ShowTextDisplay(new DisplayTextPacket("gameversion", versionNote, Color.yellow, Vector3.forward * 2 + Vector3.up * 0.4f, true, true, 20));
                });
            }
            if(Volatile.Read(ref planning) == 1) {
                Log.Info(Defines.CLIENT, "Mod sync: the host's updated mod list came after planning started, keeping the current plan.");
                return;
            }

            // Tells the host not to give up on us while a big first scan runs (and our platform), and the player why nothing happens yet
            Report(client, JoinerStatus.Checking, "");
            Dispatcher.Enqueue(() => {
                if(ModManager.clientInstance == client && !HostHasGreeted()) ShowNotice("Checking your mods...");
            });

            // A PC host still asking mod.io for Quest versions sends the list again once it knows them; plan with this one only if that doesn't come
            int wait = Interlocked.Increment(ref questWait);
            if(set.WaitForQuestIds(ModSyncPaths.Platform)) {
                Log.Info(Defines.CLIENT, "Mod sync: the host is still looking up Quest versions, waiting for its updated mod list.");
                Task.Delay(QuestWaitMs).ContinueWith(_ => {
                    if(Volatile.Read(ref questWait) != wait) return;
                    Log.Warn(Defines.CLIENT, "Mod sync: the host's updated mod list didn't come, using the first one.");
                    PlanAndHandle(client, set);
                });
                return;
            }
            PlanAndHandle(client, set);
        }

        private static void PlanAndHandle(ClientType client, HostModSet set) {
            if(Interlocked.Exchange(ref planning, 1) == 1) return;
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
                if(ModSwapClient.InGame && t.Result.crossPlatform && t.Result.Of(SyncAction.Add).Any(s => s.source == "modio")) {
                    ModManager.instance.StartCoroutine(LookUpModio(client, set, t.Result));
                } else {
                    Handle(client, set, t.Result);
                }
            }));
        }

        // Quest: what each download is on mod.io (title, this platform's file size), shown before the player decides. A mod mod.io has no file
        // for on this platform (or that is gone) is "not available" right away; when mod.io can't be asked, the download says so later.
        private static IEnumerator LookUpModio(ClientType client, HostModSet set, SyncPlan plan) {
            foreach(SyncStep step in plan.Of(SyncAction.Add).Where(s => s.source == "modio").ToList()) {
                if(ModManager.clientInstance != client) yield break;
                ModioClient.PlatformFile file = null;
                yield return ModioClient.Guarded(ModioClient.LookUpForThisPlatform(step.modioId, r => file = r),
                                                 e => Log.Warn(Defines.CLIENT, "Mod sync: looking up mod.io " + step.modioId + " failed. " + e.Message));
                if(file == null || file.error != null) {
                    if(file != null && file.unavailable) {
                        Log.Warn(Defines.CLIENT, "Mod sync: " + step.hostMod.name + " (mod.io " + step.modioId + ") isn't available for " + ModSyncPaths.Platform + ": " + file.error);
                        step.action = SyncAction.Unavailable;
                    } else {
                        // mod.io isn't answering: asking for each remaining mod could take minutes (and the host times the check out); the download reports it
                        Log.Warn(Defines.CLIENT, "Mod sync: couldn't look up mod.io " + step.modioId + " (" + step.hostMod.name + "): " + (file == null ? "no answer" : file.error) + ". Skipping the remaining lookups.");
                        break;
                    }
                    continue;
                }
                step.modioTitle = file.title;
                step.modioBytes = file.bytes;
            }
            Handle(client, set, plan);
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

        private static float lastKeepAlive;

        // Main thread, while a mod.io download of the setup moves: the host only sees its own transfers, so a repeated "needs_setup" (at
        // most once a minute) tells it the setup is still alive (hosts before 1.2.1 ignore it). Its setup time limit then waits.
        internal static void KeepSetupAlive() {
            if(Time.realtimeSinceStartup - lastKeepAlive < 60f) return;
            lastKeepAlive = Time.realtimeSinceStartup;
            Report(ModManager.clientInstance, JoinerStatus.NeedsSetup, "");
        }

        // Only offered when the host runs mod sync in "warn" mode
        internal static void JoinAnyway() {
            if(!AwaitingDecision) return;
            ClientType client = awaitingClient;
            awaitingClient = null;
            Report(client, JoinerStatus.Ready, Plan.Describe());
        }

        // The setup found nothing left to install (Quest: every download turned out unavailable on mod.io): join with the warning
        internal static void JoinWithoutSetup() {
            if(!AwaitingDecision) return;
            ClientType client = awaitingClient;
            awaitingClient = null;
            List<SyncStep> missing = Plan.Of(SyncAction.Unavailable).ToList();
            if(missing.Count > 0) ShowNotice("Joining without mods that aren't available on your platform:\n" + string.Join(", ", missing.Select(s => s.Mod.name)));
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
