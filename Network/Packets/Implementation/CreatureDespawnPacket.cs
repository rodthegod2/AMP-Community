using AMP.Data;
using AMP.Events;
using AMP.Logging;
using AMP.Network.Client;
using AMP.Network.Data;
using AMP.Network.Data.Sync;
using AMP.Threading;
using Netamite.Client.Definition;
using Netamite.Network.Packet;
using Netamite.Network.Packet.Attributes;
using Netamite.Server.Definition;

namespace AMP.Network.Packets.Implementation {
    [PacketDefinition((byte) PacketType.CREATURE_DESPAWN)]
    public class CreatureDepawnPacket : AMPPacket {
        [SyncedVar] public int creatureId;

        public CreatureDepawnPacket() { }

        public CreatureDepawnPacket(int creatureId) {
            this.creatureId = creatureId;
        }

        public CreatureDepawnPacket(CreatureNetworkData cnd) 
            : this(creatureId: cnd.networkedId
                  ) {
            
        }

        public override bool ProcessClient(NetamiteClient client) {
            // Look the creature up on the main thread too: spawns register there, so a despawn right behind its spawn would otherwise miss it
            Dispatcher.Enqueue(() => {
                if(ModManager.clientSync == null) return;
                var creatures = ModManager.clientSync.syncData.creatures;
                if(!creatures.TryRemove(creatureId, out CreatureNetworkData cnd) && !creatures.TryRemove(-creatureId, out cnd)) return;

                if(cnd.creature != null) {
                    cnd.creature.Despawn();
                    ClientSync.PrintAreaStuff("Creature 1");
                }
            });
            return true;
        }

        public override bool ProcessServer(NetamiteServer server, ClientData client) {
            if(ModManager.serverInstance.creatures.ContainsKey(creatureId)) {
                CreatureNetworkData cnd = ModManager.serverInstance.creatures[creatureId];

                Log.Debug(Defines.SERVER, $"{client.ClientName} has despawned creature {cnd.creatureType} ({cnd.networkedId})");
                server.SendToAllExcept(this, client.ClientId);

                ModManager.serverInstance.creatures.TryRemove(creatureId, out _);
                ModManager.serverInstance.creature_owner.TryRemove(creatureId, out _);

                ServerEvents.InvokeOnCreatureDespawned(cnd, client);
            }
            return true;
        }
    }
}
