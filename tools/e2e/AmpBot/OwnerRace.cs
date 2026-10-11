using AMP.ModSync;
using Netamite.Client.Implementation;
using Netamite.Network.Packet;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace AmpBot {
    // --join ... --owner-race: two players (two connections) grab the same item at the same moment, many times (audit finding 6).
    // The server answers every grab with "you own it" to the grabber and "you don't" to everyone else; when two grabs are handled at once
    // those answers could interleave, leaving both or neither believing they own it. After each round exactly one may believe it.
    public sealed class OwnerRace {
        private sealed class Player {
            public string name;
            public IPClient client;
            public volatile bool greeted;
            public readonly ConcurrentDictionary<int, bool> owns = new ConcurrentDictionary<int, bool>(); // item -> last ItemOwnerPacket
            public volatile int spawnedItem;
            public volatile bool dropped;
        }

        private readonly string address;
        private readonly int port;

        public OwnerRace(string address, int port) { this.address = address; this.port = port; }

        public bool Run(Checklist list, int rounds = 8, int grabsPerRound = 300) {
            Player a = Join("RaceA"), b = Join("RaceB");
            if(a == null || b == null) { list.Fail("couldn't get two players into the game"); return false; }
            a.client.SendReliable(new ItemSpawnPacket { itemId = 0, type = "SwordShortCommon", category = 1, clientsideId = 1,
                                                        position = new Netamite.Datatype.Vector3(40, 3, -46), rotation = new Netamite.Datatype.Vector3(0, 0, 0) });
            DateTime until = DateTime.UtcNow.AddSeconds(15);
            while(a.spawnedItem == 0 && DateTime.UtcNow < until) Thread.Sleep(100);
            int item = a.spawnedItem;
            if(item == 0) { list.Fail("the host never gave our item an id"); return false; }
            Out.Info("racing for item " + item);

            int bad = 0;
            for(int round = 1; round <= rounds; round++) {
                var start = new ManualResetEvent(false);
                Thread ta = Grabber(a, item, grabsPerRound, start), tb = Grabber(b, item, grabsPerRound, start);
                start.Set();
                ta.Join(); tb.Join();
                Thread.Sleep(3000); // every answer has arrived
                bool ownsA, ownsB;
                a.owns.TryGetValue(item, out ownsA);
                b.owns.TryGetValue(item, out ownsB);
                string result = "round " + round + ": A thinks " + (ownsA ? "it owns" : "it doesn't own") + ", B thinks " + (ownsB ? "it owns" : "it doesn't own");
                if(ownsA == ownsB) { bad++; Out.Info(result + "  <-- inconsistent"); } else Out.Info(result);
                if(a.dropped || b.dropped) { list.Fail("the host dropped a racer in round " + round); return false; }
            }
            a.client.Disconnect(); b.client.Disconnect();
            if(bad > 0) { list.Fail(bad + " of " + rounds + " rounds left both or neither player owning the item"); return false; }
            Out.Line("OK", "owner race: " + rounds + " rounds of " + grabsPerRound + " simultaneous grabs each, exactly one owner every time");
            return true;
        }

        private static Thread Grabber(Player p, int item, int count, ManualResetEvent start) {
            var t = new Thread(() => {
                start.WaitOne();
                for(int i = 0; i < count; i++) p.client.SendReliable(new ItemOwnerPacket { itemId = item, owning = true });
            }) { IsBackground = true };
            t.Start();
            return t;
        }

        // Connects, reports the same mods (ready), answers the host's level as loaded: greeted and playing
        private Player Join(string name) {
            var p = new Player { name = name };
            DateTime giveUp = DateTime.UtcNow.AddMinutes(4); // the game may not be hosting yet
            while(true) {
                var c = new IPClient(address, port) { ClientName = name };
                var connected = new ManualResetEvent(false);
                c.OnConnect += () => connected.Set();
                c.OnConnectionError += e => connected.Set();
                c.OnDisconnect += reason => { p.dropped = true; Out.Info(name + " disconnected: " + reason); };
                c.OnDataReceived += packet => OnPacket(p, packet);
                p.client = c;
                c.Connect("");
                connected.WaitOne(TimeSpan.FromSeconds(15));
                if(c.IsConnected) break;
                try { c.Disconnect(); } catch(Exception) { }
                if(DateTime.UtcNow > giveUp) return null;
                Thread.Sleep(2000);
            }
            p.dropped = false;
            DateTime until = DateTime.UtcNow.AddSeconds(60);
            while(!p.greeted && DateTime.UtcNow < until) Thread.Sleep(100);
            Out.Info(name + (p.greeted ? " is playing" : " was never greeted"));
            return p.greeted ? p : null;
        }

        private static void OnPacket(Player p, NetPacket packet) {
            try {
                ModSyncPacket sync = packet as ModSyncPacket;
                if(sync != null && sync.kind == ModSyncPacket.HOST_MOD_SET && sync.part == 0) {
                    string json = JsonConvert.SerializeObject(new JoinerReport { platform = "windows", status = JoinerStatus.Ready, detail = "" });
                    foreach(ModSyncPacket r in ModSyncPacket.Create(ModSyncPacket.JOINER_REPORT, json)) p.client.SendReliable(r);
                } else if(packet is LevelChangePacket) {
                    LevelChangePacket level = (LevelChangePacket) packet;
                    p.client.SendReliable(new LevelChangePacket(level.level, level.mode, level.options, EventTime.OnEnd));
                } else if(packet is AllowTransmissionPacket && ((AllowTransmissionPacket) packet).allow) {
                    p.greeted = true;
                } else if(packet is ItemSpawnPacket) {
                    ItemSpawnPacket item = (ItemSpawnPacket) packet;
                    if(item.clientsideId == 1 && item.itemId > 0) p.spawnedItem = item.itemId;
                } else if(packet is ItemOwnerPacket) {
                    ItemOwnerPacket owner = (ItemOwnerPacket) packet;
                    p.owns[owner.itemId] = owner.owning;
                }
            } catch(Exception e) {
                Out.Info(p.name + ": handling " + packet.GetType().Name + " threw " + e.Message);
            }
        }
    }
}
