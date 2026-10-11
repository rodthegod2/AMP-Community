using Netamite.Datatype;
using Netamite.Network.Packet;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace AmpBot {
    // The bot's own avatar: walks in a small circle next to the other player and carries one item that circles at chest height.
    // The joiner and the host both use it; they differ in how the item gets its network id and how packets leave.
    public class BotPlayer {
        public const string CreatureId = "HumanMale"; // what a real AMP client reports for a male player
        private const float Radius = 1.5f, ItemRadius = 0.5f, Period = 8f;

        private readonly Expectations x;
        private readonly Action<NetPacket> sendReliable, sendUnreliable;
        private readonly Func<long> clock;
        private readonly int tickRate;
        private volatile bool stopped;
        private int itemNetworkId;              // 0 until the item has its network id
        private Thread thread;

        public int PlayerId { get; private set; }
        public Vector3 Center { get; private set; }

        public BotPlayer(Expectations x, int tickRate, Func<long> clock, Action<NetPacket> sendReliable, Action<NetPacket> sendUnreliable) {
            this.x = x;
            this.tickRate = Math.Max(1, Math.Min(60, tickRate));
            this.clock = clock;
            this.sendReliable = sendReliable;
            this.sendUnreliable = sendUnreliable;
        }

        // What an AMP client sends once it may transmit: its player, its look, its mod list; then positions every tick
        public void Start(int playerId, Vector3 near, string name) {
            if(thread != null) return;
            PlayerId = playerId;
            Center = new Vector3(near.x + 2f, near.y, near.z); // next to the other player, not inside them
            Out.Info("playing as " + playerId + " around " + Center);
            sendReliable(new PlayerDataPacket { clientId = playerId, name = name, creatureId = CreatureId, height = 1.8f, playerPos = At(0), playerRotY = 0f, uniqueId = "ampbot-" + playerId });
            sendReliable(new PlayerEquipmentPacket {
                clientId = playerId,
                colors = Enumerable.Range(0, 6).Select(i => new Color(0.4f, 0.3f, 0.2f, 1f)).ToArray(), // hair x3, iris, sclera, skin
                equipment = x.equipment.ToArray(),
                ethnicGroup = "Eradian",
            });
            thread = new Thread(Loop) { IsBackground = true, Name = "bot player" };
            thread.Start();
        }

        public void Stop() { stopped = true; }

        // The item's network id (joiner: the host's answer to our spawn; host: chosen by us)
        public void ItemHasId(int id) {
            if(Interlocked.CompareExchange(ref itemNetworkId, id, 0) == 0) Out.Info("our " + x.playItem + " has network id " + id);
        }

        public ItemSpawnPacket ItemSpawn(int itemId, int clientsideId) {
            return new ItemSpawnPacket { itemId = itemId, type = x.playItem, category = 1 /* ItemData.Type.Weapon */, clientsideId = clientsideId,
                                         position = ItemAt(0), rotation = new Vector3(0, 0, 0), isMagicProjectile = false };
        }

        public event Action SpawnItemNow; // raised once, a few seconds after the avatar appeared

        private void Loop() {
            var start = DateTime.UtcNow;
            bool itemRequested = false;
            while(!stopped) {
                float t = (float) (DateTime.UtcNow - start).TotalSeconds;
                try {
                    Vector3 pos = At(t);
                    float facing = (float) (t / Period * 360f + 90f) % 360f; // walking direction along the circle
                    sendUnreliable(new PlayerPositionPacket {
                        playerId = PlayerId, position = pos, rotationY = facing,
                        headPos = new Vector3(pos.x, pos.y + 1.7f, pos.z), headRot = new Vector3(0, facing, 0),
                        handLeftPos = new Vector3(-0.3f, 1.1f, 0.2f), handLeftRot = new Vector3(0, 0, 0),
                        handRightPos = new Vector3(0.3f, 1.1f, 0.2f), handRightRot = new Vector3(0, 0, 0),
                    });
                    if(!string.IsNullOrEmpty(x.playItem) && !itemRequested && t > 3f) {
                        itemRequested = true;
                        SpawnItemNow?.Invoke();
                    }
                    int id = itemNetworkId;
                    if(id > 0) {
                        sendReliable(new ItemPositionPacket { timestamp = clock(), itemId = id, position = ItemAt(t), rotation = new Vector3(0, facing, 0),
                                                              velocity = new Vector3(0, 0, 0), angularVelocity = new Vector3(0, 0, 0) });
                    }
                } catch(Exception e) {
                    if(!stopped) Out.Info("sending failed: " + e.Message);
                }
                Thread.Sleep(1000 / tickRate);
            }
        }

        private Vector3 At(float t) {
            double a = 2 * Math.PI * t / Period;
            return new Vector3(Center.x + Radius * (float) Math.Cos(a), Center.y, Center.z + Radius * (float) Math.Sin(a));
        }

        private Vector3 ItemAt(float t) {
            double a = -2 * Math.PI * t / Period;
            return new Vector3(Center.x + ItemRadius * (float) Math.Cos(a), Center.y + 1.2f, Center.z + ItemRadius * (float) Math.Sin(a));
        }
    }

    // What the bot saw of the other side's gameplay, checked against --expect (seePlayers, seePlayerMoves, seeItems, seeItemMoves)
    public class GameplayWatch {
        private readonly Expectations x;
        private readonly Checklist list;
        private readonly ConcurrentDictionary<int, string> players = new ConcurrentDictionary<int, string>();   // other players: id -> name
        private readonly ConcurrentDictionary<int, int> playerMoves = new ConcurrentDictionary<int, int>();
        private readonly ConcurrentDictionary<int, string> itemTypes = new ConcurrentDictionary<int, string>(); // item id -> data id
        private readonly ConcurrentDictionary<int, int> itemMoves = new ConcurrentDictionary<int, int>();
        private readonly HashSet<int> ignore = new HashSet<int>();
        public Vector3 LastOtherPosition { get; private set; }

        public GameplayWatch(Expectations x, Checklist list) {
            this.x = x;
            this.list = list;
            if(x.seePlayers > 0) list.Expect("seePlayers " + x.seePlayers);
            if(x.seePlayerMoves > 0) list.Expect("seePlayerMoves " + x.seePlayerMoves);
            foreach(string item in x.seeItems) {
                list.Expect("seeItem " + item);
                if(x.seeItemMoves > 0) list.Expect("seeItemMoves " + item + " " + x.seeItemMoves);
            }
        }

        public void IgnorePlayer(int id) { lock(ignore) ignore.Add(id); }

        public string Summary() {
            return players.Count + " other player(s), " + playerMoves.Values.Sum() + " player position updates, " + itemTypes.Count + " items ("
                 + string.Join(", ", itemTypes.Values.GroupBy(t => t).Select(g => g.Count() + "x " + g.Key)) + "), " + itemMoves.Values.Sum() + " item position updates";
        }
        private bool Ignored(int id) { lock(ignore) return ignore.Contains(id); }

        public void OnPlayerData(PlayerDataPacket p) {
            if(Ignored(p.clientId)) return;
            if((object) p.playerPos != null) LastOtherPosition = p.playerPos;
            if(players.TryAdd(p.clientId, p.name)) {
                Out.Info("player " + p.clientId + " " + p.name + " (" + p.creatureId + ", height " + p.height.ToString("0.00") + ") at " + p.playerPos);
                if(x.seePlayers > 0 && players.Count >= x.seePlayers) list.Mark("seePlayers " + x.seePlayers, string.Join(", ", players.Values));
            }
        }

        public void OnEquipment(PlayerEquipmentPacket p) {
            if(Ignored(p.clientId)) return;
            Out.Info("equipment of " + p.clientId + ": " + (p.equipment == null ? "none" : string.Join(", ", p.equipment)) + ", " + (p.colors == null ? 0 : p.colors.Length) + " colors, ethnic group " + p.ethnicGroup);
        }

        public void OnPlayerMoved(int playerId, Vector3 position) {
            if(Ignored(playerId) || (object) position == null) return; // Netamite's Vector3 == compares fields
            LastOtherPosition = position;
            int n = playerMoves.AddOrUpdate(playerId, 1, (_, c) => c + 1);
            if(n == 1) Out.Info("first position of player " + playerId + ": " + position);
            if(x.seePlayerMoves > 0 && n == x.seePlayerMoves) list.Mark("seePlayerMoves " + x.seePlayerMoves, "player " + playerId + " now at " + position);
        }

        public void OnItemSpawn(ItemSpawnPacket p, bool ours) {
            if(p.itemId <= 0) return;
            itemTypes[p.itemId] = p.type;
            if(ours) return;
            if(x.seeItems.Contains(p.type)) list.Mark("seeItem " + p.type, "id " + p.itemId + " at " + p.position);
        }

        public void OnItemMoved(ItemPositionPacket p) {
            string type;
            if(!itemTypes.TryGetValue(p.itemId, out type) || !x.seeItems.Contains(type)) return;
            int n = itemMoves.AddOrUpdate(p.itemId, 1, (_, c) => c + 1);
            if(x.seeItemMoves > 0 && n == x.seeItemMoves) list.Mark("seeItemMoves " + type + " " + x.seeItemMoves, "now at " + p.position);
        }
    }
}
