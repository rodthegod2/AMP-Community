using AMP.Network.Data;
using AMP.Threading;
using Netamite.Client.Definition;
using Netamite.Network.Packet;
using Netamite.Network.Packet.Attributes;
using Netamite.Server.Definition;

namespace AMP.Network.Packets.Implementation {
    [PacketDefinition((byte) PacketType.ITEM_OWNER)]
    public class ItemOwnerPacket : AMPPacket {
        [SyncedVar] public int itemId;
        [SyncedVar] public bool owning;

        public ItemOwnerPacket() { }

        public ItemOwnerPacket(int itemId, bool owning) {
            this.itemId = itemId;
            this.owning = owning;
        }

        public override bool ProcessClient(NetamiteClient client) {
            // owningItems is a plain List read every frame by NetworkItem, so it may only change on the main thread
            Dispatcher.Enqueue(() => {
                if(ModManager.clientSync == null) return;
                var syncData = ModManager.clientSync.syncData;

                if(owning && !syncData.owningItems.Contains(itemId)) syncData.owningItems.Add(itemId);
                if(!owning) syncData.owningItems.Remove(itemId);

                if(syncData.items.TryGetValue(itemId, out var ind)) {
                    ind.SetOwnership(owning);
                    if(owning) ind.networkItem?.OnHoldStateChanged();
                }
            });
            return true;
        }

        public override bool ProcessServer(NetamiteServer server, ClientData client) {
            if(itemId > 0 && ModManager.serverInstance.items.ContainsKey(itemId)) {
                ModManager.serverInstance.UpdateItemOwner(ModManager.serverInstance.items[itemId], client);
            }
            return true;
        }
    }
}
