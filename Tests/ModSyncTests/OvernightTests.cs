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
        DamagePolicyTests();
        ItemCapTests();
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

    // Audit finding 3: every client that saw a hit reported it
    private static void DamagePolicyTests() {
        T("damage: a remote player's avatar hit by me is reported", AMP.Network.Data.DamageReportPolicy.ReportPlayerPuppetDamage(true));
        T("damage: a remote player's avatar hit by an NPC is not (their own game applies it)", !AMP.Network.Data.DamageReportPolicy.ReportPlayerPuppetDamage(false));
        Func<bool, bool, bool, bool> npc = AMP.Network.Data.DamageReportPolicy.ReportCreatureDamage;
        T("damage: my hit on an NPC is reported, owned or not", npc(true, false, false) && npc(true, false, true));
        T("damage: another player's hit on an NPC is not reported by me, owned or not", !npc(false, true, false) && !npc(false, true, true));
        T("damage: other causes are reported by the NPC's owner only", npc(false, false, true) && !npc(false, false, false));
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

    private struct Vec { public float x, y, z; public Vec(float x, float y, float z) { this.x = x; this.y = y; this.z = z; } }
    private class FakePositionPacket { public int id; public string name; public Vec position; public float height; public Vec[] path; public bool flag; }

    // A joiner's NaN/infinite position was relayed to every player (fuzz test: thousands of Unity errors per second on the host)
    private static void PacketNumberTests() {
        Func<FakePositionPacket> ok = () => new FakePositionPacket { id = 1, name = "x", position = new Vec(1, 2, 3), height = 1.8f, path = new[] { new Vec(0, 0, 0) } };
        T("numbers: a normal packet passes", AMP.Network.Packets.PacketNumbers.AllFinite(ok()));
        T("numbers: null arrays and strings pass", AMP.Network.Packets.PacketNumbers.AllFinite(new FakePositionPacket()));
        var p = ok(); p.position = new Vec(float.NaN, 0, 0);
        T("numbers: NaN in a vector is caught", !AMP.Network.Packets.PacketNumbers.AllFinite(p));
        p = ok(); p.position = new Vec(0, 0, float.PositiveInfinity);
        T("numbers: infinity in a vector is caught", !AMP.Network.Packets.PacketNumbers.AllFinite(p));
        p = ok(); p.height = float.PositiveInfinity;
        T("numbers: plain float fields are left alone (health may be infinite)", AMP.Network.Packets.PacketNumbers.AllFinite(p));
        p = ok(); p.path = new[] { new Vec(0, 0, 0), new Vec(0, float.NaN, 0) };
        T("numbers: NaN inside an array of vectors is caught", !AMP.Network.Packets.PacketNumbers.AllFinite(p));
        p = ok(); p.position = new Vec(float.MaxValue, -float.MaxValue, 1e30f);
        T("numbers: huge but finite values pass (the game decides about those)", AMP.Network.Packets.PacketNumbers.AllFinite(p));
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
