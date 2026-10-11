using System;
using AMP.ModSync;

// Bugs found in the overnight test-and-harden pass (docs/overnight-2026-10-11.md)
internal static partial class Program {
    private static void OvernightTests() {
        SetupTimeoutTests();
    }

    // The host's 30 min setup limit kicked joiners whose big download over a slow upload was still running
    private static void SetupTimeoutTests() {
        DateTime now = new DateTime(2026, 10, 11, 3, 0, 0, DateTimeKind.Utc);
        TimeSpan stall = TimeSpan.FromMinutes(2);
        T("setup limit: no download running -> the limit applies", !TransferRules.DownloadStillMoving(now, null, stall));
        T("setup limit: a download acknowledged 10 s ago keeps the joiner", TransferRules.DownloadStillMoving(now, now.AddSeconds(-10), stall));
        T("setup limit: a download stalled for 2 min doesn't", !TransferRules.DownloadStillMoving(now, now - stall, stall));
        T("setup limit: a download stalled for longer doesn't", !TransferRules.DownloadStillMoving(now, now.AddMinutes(-30), stall));
    }
}
