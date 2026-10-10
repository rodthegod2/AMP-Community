using System.Linq;

namespace AMP.ModSync {
    public static class JournalPlanner {
        // Disables first, then replaced mods are moved aside, then installs, then the load order
        public static SwapJournal Build(SyncPlan plan, string[] hostLoadOrder, string host, int port, string password, SwapMode mode) {
            var journal = new SwapJournal { mode = mode, host = host ?? "", port = port, password = password ?? "", rejoinPending = true,
                                            hostLoadOrder = hostLoadOrder ?? new string[0] };
            foreach(SyncStep step in plan.Of(SyncAction.Disable))
                journal.ops.Add(new SwapOp { kind = SwapOpKind.Disable, folder = step.localMod.folder });
            foreach(SyncStep step in plan.Of(SyncAction.Replace))
                journal.ops.Add(new SwapOp { kind = SwapOpKind.MoveAside, folder = step.localMod.folder });
            foreach(SyncStep step in plan.Of(SyncAction.Replace).Concat(plan.Of(SyncAction.Add)))
                journal.ops.Add(new SwapOp { kind = SwapOpKind.Install, folder = step.hostMod.folder, hash = step.hostMod.hash });
            if(plan.loadOrderDiffers)
                journal.ops.Add(new SwapOp { kind = SwapOpKind.LoadOrder });
            return journal;
        }
    }
}
