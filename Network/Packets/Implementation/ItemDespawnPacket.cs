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
using UnityEngine;

namespace AMP.Network.Packets.Implementation {
    [PacketDefinition((byte) PacketType.ITEM_DESPAWN)]
    public class ItemDespawnPacket : AMPPacket {
        [SyncedVar] public int itemId;

        public ItemDespawnPacket() { }

        public ItemDespawnPacket(int itemId) {
            this.itemId = itemId;
        }

        public ItemDespawnPacket(ItemNetworkData ind) 
            : this(itemId: ind.networkedId){

        }

        public override bool ProcessClient(NetamiteClient client) {
            // Look the item up on the main thread too: spawns register there, so a despawn right behind its spawn would otherwise miss it
            Dispatcher.Enqueue(() => {
                if(ModManager.clientSync == null) return;
                if(!ModManager.clientSync.syncData.items.TryRemove(itemId, out ItemNetworkData ind)) return;

                if(ind.clientsideItem != null) {
                    if(ind.networkItem != null) ind.networkItem.lastTime = (int) Time.time;
                    ind.clientsideItem.Despawn();
                    ClientSync.PrintAreaStuff("Item 3");
                }
            });
            return true;
        }

        public override bool ProcessServer(NetamiteServer server, ClientData client) {
            if(ModManager.serverInstance.items.ContainsKey(itemId)) {
                ItemNetworkData ind = ModManager.serverInstance.items[itemId];

                Log.Debug(Defines.SERVER, $"{client.ClientName} has despawned item {ind.dataId} ({ind.networkedId})");

                server.SendToPlayingExcept(this, client.ClientId);

                ModManager.serverInstance.items.TryRemove(itemId, out _);
                ModManager.serverInstance.item_owner.TryRemove(itemId, out _);

                ServerEvents.InvokeOnItemDespawned(ind, client);
            }
            return true;
        }
    }
}
