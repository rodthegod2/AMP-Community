using System;
using System.Collections.Generic;
using System.Linq;
using AMP.ModSync;

// Bugs found in the overnight test-and-harden pass (docs/overnight-2026-10-11.md)
internal static partial class Program {
    private static void OvernightTests() {
        SetupTimeoutTests();
        PacketNumberTests();
        GameVersionTests();
        ItemCapTests();
        LevelChangeRuleTests();
    }

    // Review I4: the pull-back must not drag a player out of the main menu, and must not hit the host itself
    private static void LevelChangeRuleTests() {
        Func<string, bool, bool, AMP.Network.Data.LevelChangeDecision> d = AMP.Network.Data.LevelChangeRules.Decide;
        T("level change: quitting to MainMenu with map changes off is not pulled back", d("MainMenu", false, false) == AMP.Network.Data.LevelChangeDecision.KickMainMenu);
        T("level change: MainMenu in any case", d("mainmenu", true, false) == AMP.Network.Data.LevelChangeDecision.KickMainMenu && d("MAINMENU", false, true) == AMP.Network.Data.LevelChangeDecision.KickMainMenu);
        T("level change: the host changes the map even when map changes are off", d("Arena", false, true) == AMP.Network.Data.LevelChangeDecision.Proceed);
        T("level change: a joiner with map changes off is pulled back", d("Arena", false, false) == AMP.Network.Data.LevelChangeDecision.PullBack);
        T("level change: map changes on -> anyone", d("Arena", true, false) == AMP.Network.Data.LevelChangeDecision.Proceed);
    }

    // Audit 10: the item cap removed held items (the starting weapon on the hip)
    private static void ItemCapTests() {
        Func<int[], int[], int, string> pick = (free, held, limit) => string.Join(",", AMP.Network.Data.ItemCapPolicy.PickEvictions(
            free.Select(i => new KeyValuePair<int, bool>(i, false)).Concat(held.Select(i => new KeyValuePair<int, bool>(i, true))), limit));
        T("item cap: under the limit -> nothing", pick(new[] { 1, 2 }, new[] { 3 }, 3) == "");
        T("item cap: oldest free items go first", pick(new[] { 5, 2, 9 }, new int[0], 1) == "2,5");
        T("item cap: held items are never picked", pick(new[] { 4, 6 }, new[] { 1, 2 }, 3) == "4");
        T("item cap: all held -> nothing", pick(new int[0], new[] { 1, 2, 3 }, 1) == "");
        T("item cap: limit 0 means off", pick(new[] { 1, 2 }, new int[0], 0) == "");
    }

    // Quest research: PC is on 1.3.1.x, Nomad's build is unknown; AMP never compared game versions, a joiner is now told (never blocked)
    private static void GameVersionTests() {
        T("game version: same build -> nothing", HostModSet.GameVersionNote("1.3.1.260730005", "1.3.1.260730005") == null);
        T("game version: unknown on either side -> nothing", HostModSet.GameVersionNote("", "1.3.1.2") == null && HostModSet.GameVersionNote("1.3.1.2", null) == null);
        string hotfix = HostModSet.GameVersionNote("1.3.1.260730005", "1.3.1.2");
        T("game version: another hotfix of the same release -> mild note", hotfix != null && hotfix.Contains("Usually fine"));
        string other = HostModSet.GameVersionNote("1.3.1.2", "1.3.0.5");
        T("game version: another release -> says both should be on the same version", other != null && other.Contains("same game version") && other.Contains("1.3.1.2") && other.Contains("1.3.0.5"));
        HostModSet old = Newtonsoft.Json.JsonConvert.DeserializeObject<HostModSet>("{\"protocol\":1,\"platform\":\"windows\",\"mods\":[]}");
        T("game version: a 1.2.0 host's list (no gameVersion) reads as unknown", old.gameVersion == "" && HostModSet.GameVersionNote(old.gameVersion, "1.3.1.2") == null);
    }

    // A joiner's NaN/infinite position was relayed to every player (fuzz test: thousands of Unity errors per second on the host)
    private static void PacketNumberTests() {
        T("numbers: normal and huge-but-finite values pass", AMP.Network.Packets.PacketNumbers.Finite(1, 2, 3) && AMP.Network.Packets.PacketNumbers.Finite(float.MaxValue, -float.MaxValue, 1e30f, 0));
        T("numbers: NaN in any component is caught", !AMP.Network.Packets.PacketNumbers.Finite(float.NaN, 0, 0) && !AMP.Network.Packets.PacketNumbers.Finite(0, 0, 0, float.NaN));
        T("numbers: infinity is caught", !AMP.Network.Packets.PacketNumbers.Finite(0, float.PositiveInfinity, 0) && !AMP.Network.Packets.PacketNumbers.IsFinite(float.NegativeInfinity));
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
