using AMP.ModSync;
using AMP.Network.Data;
using Netamite.Client.Definition;
using Netamite.Network.Packet;
using Netamite.Network.Packet.Attributes;
using Netamite.Server.Definition;

namespace AMP.Network.Packets.Implementation {
    // One 32 KB piece of a mod file, host -> joiner
    [PacketDefinition((byte) PacketType.MOD_FILE)]
    public class ModFilePacket : AMPPacket {
        [SyncedVar] public int  transferId;
        [SyncedVar] public int  fileIndex;
        [SyncedVar] public long offset;
        [SyncedVar] public Blob data;

        public ModFilePacket() { }

        public ModFilePacket(int transferId, int fileIndex, long offset, byte[] bytes) {
            this.transferId = transferId;
            this.fileIndex  = fileIndex;
            this.offset     = offset;
            this.data       = new Blob { bytes = bytes };
        }

        public override bool ProcessClient(NetamiteClient client) {
            ModTransferClient.OnChunk(this);
            return true;
        }

        public override bool ProcessServer(NetamiteServer server, ClientData client) {
            return true; // chunks only go host -> joiner
        }
    }
}
