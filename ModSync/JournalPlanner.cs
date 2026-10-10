using System.Linq;

namespace AMP.ModSync {
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
            foreach(SyncStep step in plan.Steps(SyncAction.Replace, SyncAction.Add).Where(s => !s.hostMod.isAmp))
                journal.ops.Add(new SwapOp { kind = SwapOpKind.Install, folder = step.hostMod.folder, hash = step.hostMod.hash });
            if(plan.loadOrderDiffers)
                journal.ops.Add(new SwapOp { kind = SwapOpKind.LoadOrder });
            return journal;
        }
    }
}
