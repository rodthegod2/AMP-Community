using AMP.Data;
using AMP.Logging;
using AMP.Network.Data;
using AMP.Network.Data.Sync;
using AMP.Threading;
using Netamite.Client.Definition;
using Netamite.Network.Packet;
using Netamite.Network.Packet.Attributes;
using Netamite.Server.Definition;
using ThunderRoad;
using UnityEngine;

namespace AMP.Network.Packets.Implementation {
    [PacketDefinition((byte) PacketType.ITEM_BREAK)]
    public class ItemBreakPacket : AMPPacket {
        [SyncedVar] public int itemId;
        [SyncedVar(true)] public Vector3[] velocities;
        [SyncedVar(true)] public Vector3[] angularVelocities;

        public ItemBreakPacket() { }

        public ItemBreakPacket(int itemId, Vector3[] velocities, Vector3[] angularVelocities) {
            this.itemId = itemId;
            this.velocities = velocities;
            this.angularVelocities = angularVelocities;
        }

        public override bool ProcessClient(NetamiteClient client) {
            if(ModManager.clientSync.syncData.items.ContainsKey(itemId)) {
                ItemNetworkData itemNetworkData = ModManager.clientSync.syncData.items[itemId];

                Dispatcher.Enqueue(() => {
                Breakable breakable = itemNetworkData?.clientsideItem?.GetComponent<Breakable>();
                    if(breakable != null) {
                        // Breaking raises the game's break event; without this every receiver claimed the item and sent the break again (audit 9)
                        Client.NetworkComponents.NetworkItem ni = itemNetworkData.networkItem;
                        if(ni != null) ni.breakingFromNetwork = true;
                        try {
                            breakable.Break();
                        } finally {
                            if(ni != null) ni.breakingFromNetwork = false;
                        }

                        for(int i = 0; i < breakable.subBrokenBodies.Count; i++) {
                            if(velocities == null || angularVelocities == null || i >= velocities.Length || i >= angularVelocities.Length) break;
                            PhysicBody pb = breakable.subBrokenBodies[i];
                            pb.velocity = velocities[i];
                            pb.angularVelocity = angularVelocities[i];
                        }

                        Log.Debug(Defines.SERVER, $"Broke item {itemNetworkData.dataId}.");
                    }
                });
            }
            return true;
        }

        public override bool ProcessServer(NetamiteServer server, ClientData client) {
            if(ModManager.serverInstance.items.ContainsKey(itemId)) {
                ItemNetworkData ind = ModManager.serverInstance.items[itemId];

                Log.Debug(Defines.SERVER, $"Broke item {ind.dataId} by {client.ClientName}.");

                server.SendToPlayingExcept(this, client.ClientId);
            }
            return true;
        }
    }
}
