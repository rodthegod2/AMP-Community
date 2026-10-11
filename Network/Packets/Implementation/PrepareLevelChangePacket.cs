using AMP.Network.Data;
using AMP.Network.Data.Sync;
using AMP.Threading;
using Netamite.Client.Definition;
using Netamite.Network.Packet;
using Netamite.Network.Packet.Attributes;
using Netamite.Server.Definition;
using System;
using System.Linq;
using ThunderRoad;
using UnityEngine;
using static ThunderRoad.TextData;

namespace AMP.Network.Packets.Implementation {
    [PacketDefinition((byte) PacketType.PREPARE_LEVEL_CHANGE)]
    public class PrepareLevelChangePacket : AMPPacket {
        [SyncedVar] public string username;
        [SyncedVar] public string level;
        [SyncedVar] public string mode;

        public PrepareLevelChangePacket() { }

        public PrepareLevelChangePacket(string username, string level, string mode) {
            this.username = username;
            this.level = level;
            this.mode = mode;
        }

        public override bool ProcessClient(NetamiteClient client) {
            ModManager.clientInstance.allowTransmission = false;

            Dispatcher.Enqueue(ResetWorldState);
            return true;
        }

        // Main thread. Forgets everything synced in the old level: other players' avatars are despawned (they come back after the load),
        // items, creatures and entities lose their network components and entries. Also run by the player who starts a level change
        // (EventHandler, level load OnStart): the server sends that player no prepare/clear, so it used to keep every old entry and
        // respawned other players' items and NPCs at old-level positions in the new level (audit finding 1).
        internal static void ResetWorldState() {
            if(ModManager.clientSync == null) return;
            foreach(PlayerNetworkData playerSync in ModManager.clientSync.syncData.players.Values.ToList()) { // Will despawn all player creatures and respawn them after level has changed
                if(playerSync.creature == null) continue;

                playerSync.Despawn();
            }

            foreach(ItemNetworkData item in ModManager.clientSync.syncData.items.Values.ToList()) {
                if(item.clientsideItem != null) item.clientsideItem.DisallowDespawn = false;

                item.networkItem?.UnregisterEvents();
                GameObject.Destroy(item.networkItem);
            }
            ModManager.clientSync.syncData.items.Clear();
            ModManager.clientSync.syncData.owningItems.Clear();


            foreach(CreatureNetworkData creature in ModManager.clientSync.syncData.creatures.Values.ToList()) {
                creature.networkCreature?.UnregisterEvents();
                GameObject.Destroy(creature.networkCreature);
            }
            ModManager.clientSync.syncData.creatures.Clear();
            ModManager.clientSync.syncData.owningCreatures.Clear();


            foreach(EntityNetworkData entity in ModManager.clientSync.syncData.entities.Values.ToList()) {
                //entity.networkEntity?.UnregisterEvents();
                GameObject.Destroy(entity.networkEntity);
            }
            ModManager.clientSync.syncData.entities.Clear();
        }

        public override bool ProcessServer(NetamiteServer server, ClientData client) {
            return base.ProcessServer(server, client);
        }
    }
}
