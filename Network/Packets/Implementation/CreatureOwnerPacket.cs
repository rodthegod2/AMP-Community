using AMP.Network.Data;
using AMP.Threading;
using Netamite.Client.Definition;
using Netamite.Network.Packet;
using Netamite.Network.Packet.Attributes;
using Netamite.Server.Definition;

namespace AMP.Network.Packets.Implementation {
    [PacketDefinition((byte) PacketType.CREATURE_OWNER)]
    public class CreatureOwnerPacket : AMPPacket {
        [SyncedVar] public int  creatureId;
        [SyncedVar] public bool owning;
        [SyncedVar] public bool staticOwner;

        public CreatureOwnerPacket() { }

        public CreatureOwnerPacket(int creatureId, bool owning, bool staticOwner = false) {
            this.creatureId  = creatureId;
            this.owning      = owning;
            this.staticOwner = staticOwner;
        }

        public override bool ProcessClient(NetamiteClient client) {
            // owningCreatures is a plain List read every frame by NetworkCreature, so it may only change on the main thread
            Dispatcher.Enqueue(() => {
                if(ModManager.clientSync == null) return;
                var syncData = ModManager.clientSync.syncData;

                if(owning && !syncData.owningCreatures.Contains(creatureId)) syncData.owningCreatures.Add(creatureId);
                if(!owning) syncData.owningCreatures.Remove(creatureId);

                if(syncData.creatures.TryGetValue(creatureId, out var cnd)) {
                    cnd.SetOwnership(owning, staticOwner);
                }
            });
            return true;
        }

        public override bool ProcessServer(NetamiteServer server, ClientData client) {
            if(creatureId > 0 && ModManager.serverInstance.creatures.ContainsKey(creatureId)) {
                ModManager.serverInstance.UpdateCreatureOwner(ModManager.serverInstance.creatures[creatureId], client, staticOwner);
            }
            
            return true;
        }
    }
}
