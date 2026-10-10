using System.Linq;

namespace AMP.ModSync {
    public enum StartupAction { None, Forget, Rejoin, OfferRestore, RestoreAndClose }

    public static class JournalPlanner {
        // Disables first, then replaced mods are moved aside, then installs, then the load order
        public static SwapJournal Build(SyncPlan plan, string[] hostLoadOrder, string host, int port, string password, SwapMode mode) {
            var journal = new SwapJournal { mode = mode, host = host ?? "", port = port, password = password ?? "", rejoinPending = true,
                                            hostLoadOrder = hostLoadOrder ?? new string[0] };
            foreach(SyncStep step in plan.Of(SyncAction.Disable))
                journal.ops.Add(new SwapOp { kind = SwapOpKind.Disable, folder = step.localMod.folder });
            foreach(SyncStep step in plan.Of(SyncAction.Replace).Where(s => !s.hostMod.isAmp))
                journal.ops.Add(new SwapOp { kind = SwapOpKind.MoveAside, folder = step.localMod.folder });
            // AMP's old folder and its replacement are an adjacent pair, so AMP is never missing for longer than one step
            foreach(SyncStep step in plan.Steps(SyncAction.Replace, SyncAction.Add).Where(s => s.hostMod.isAmp)) {
                if(step.localMod != null) journal.ops.Add(new SwapOp { kind = SwapOpKind.MoveAside, folder = step.localMod.folder, keep = true });
                journal.ops.Add(new SwapOp { kind = SwapOpKind.Install, folder = step.hostMod.folder, hash = step.hostMod.hash, keep = true });
            }
            // Installed under the host's folder name, so the next join pairs it with the host's mod by folder
            foreach(SyncStep step in plan.Steps(SyncAction.Replace, SyncAction.Add).Where(s => !s.hostMod.isAmp))
                journal.ops.Add(new SwapOp { kind = SwapOpKind.Install, folder = step.hostMod.folder, hash = step.cacheHash ?? step.hostMod.hash });
            if(plan.loadOrderDiffers)
                journal.ops.Add(new SwapOp { kind = SwapOpKind.LoadOrder });
            return journal;
        }

        // An address the next start can reconnect to by itself (Direct/IP join); a Steam lobby or a missing port can't be rejoined
        public static bool CanRejoin(string host, int port) {
            return !string.IsNullOrWhiteSpace(host) && port > 0 && port <= 65535;
        }

        // What a game start does with the journal it finds (after a half-applied "Planned" was turned into RestorePending).
        // inGame (Quest): there is no helper, the game undoes the swap itself and then has to close (its catalogs came from the swapped folders).
        public static StartupAction OnStartup(SwapJournal journal, bool inGame) {
            if(journal == null) return StartupAction.None;
            if(journal.IsFinishedKeepOnly()) return StartupAction.Forget;
            switch(journal.state) {
                case SwapState.Planned:
                case SwapState.Restored:       return StartupAction.Forget;
                case SwapState.RestorePending: return inGame ? StartupAction.RestoreAndClose : StartupAction.OfferRestore;
                case SwapState.Applied:
                    if(journal.rejoinPending) return StartupAction.Rejoin;
                    return inGame ? StartupAction.RestoreAndClose : StartupAction.None; // PC: the helper restores on quit; a crash leaves "Restore my mods"
                default:                       return StartupAction.None;
            }
        }

        // AMP replaces its own folder with a new build (an AMP update from a release): one kept pair of ops, nothing to rejoin afterwards.
        // newAmp.folder is where the new build is installed; newAmp.hash names the cache entry.
        public static SwapJournal BuildAmpUpdate(ModEntry newAmp, ModEntry localAmp) {
            var plan = new SyncPlan();
            plan.steps.Add(new SyncStep { action = SyncAction.Replace, hostMod = newAmp, localMod = localAmp, source = "host" });
            SwapJournal journal = Build(plan, null, "", 0, "", SwapMode.Keep);
            journal.rejoinPending = false;
            return journal;
        }
    }
}
