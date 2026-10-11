using AMP.Data;
using AMP.Events;
using AMP.Logging;
using AMP.Network.Data;
using AMP.Network.Data.Sync;
using AMP.Threading;
using Netamite.Client.Definition;
using Netamite.Network.Packet;
using Netamite.Network.Packet.Attributes;
using Netamite.Server.Definition;
using System;

namespace AMP.Network.Packets.Implementation {
    [PacketDefinition((byte) PacketType.CREATURE_HEALTH_CHANGE)]
    public class CreatureHealthChangePacket : AMPPacket {
        [SyncedVar]       public int   creatureId;
        [SyncedVar(true)] public float change;

        public CreatureHealthChangePacket() { }

        public CreatureHealthChangePacket(int creatureId, float change) {
            this.creatureId = creatureId;
            this.change     = change;
        }

        public override bool ProcessClient(NetamiteClient client) {
            if(ModManager.clientSync.syncData.creatures.ContainsKey(creatureId)) {
                CreatureNetworkData cnd = ModManager.clientSync.syncData.creatures[creatureId];
                cnd.Apply(this);
                Dispatcher.Enqueue(() => {
                    cnd.ApplyHealthToCreature();
                });
            }
            return true;
        }

        public override bool ProcessServer(NetamiteServer server, ClientData client) {
            if(ModManager.serverInstance.creatures.ContainsKey(creatureId)) {
                CreatureNetworkData cnd = ModManager.serverInstance.creatures[creatureId];

                // One NaN made the NPC's health NaN everywhere: never "killed", never healed (audit 11). A change can't exceed the NPC's health.
                if(float.IsNaN(change) || float.IsInfinity(change)) return true;
                if(cnd.maxHealth > 0 && !float.IsInfinity(cnd.maxHealth)) change = UnityEngine.Mathf.Clamp(change, -cnd.maxHealth, cnd.maxHealth);

                if(change < 0) {
                    cnd.lastDamager = client;
                    ServerEvents.InvokeOnCreatureDamaged(cnd, change, client);
                }

                if(cnd.Apply(this)) {
                    ServerEvents.InvokeOnCreatureKilled(cnd, client);
                }

                // If the damage the player did is more than 5% (REQUIRED_DAMAGE_FOR_CREATURE_TRANSFER) of the max health,
                // then change the npc to that players authority
                //Log.Warn(Math.Abs(change) + " > " + (cnd.maxHealth * Config.REQUIRED_DAMAGE_FOR_CREATURE_TRANSFER));
                if(Math.Abs(change) > cnd.maxHealth * Config.REQUIRED_DAMAGE_FOR_CREATURE_TRANSFER) {
                    if(!ModManager.serverInstance.creature_owner.TryGetValue(creatureId, out int creatureOwnerId) || creatureOwnerId != client.ClientId) {
                        ModManager.serverInstance.UpdateCreatureOwner(cnd, client);
                    }
                }

                if(cnd.health < 0) cnd.health = -1;

                server.SendToPlayingExcept(this, client.ClientId);
            }
            return true;
        }
    }
}
