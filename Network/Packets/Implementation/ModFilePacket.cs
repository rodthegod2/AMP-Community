using AMP.ModSync;
using System;
using AMP.Network.Data;
using Netamite.Client.Definition;
using Netamite.Network.Packet;
using Netamite.Network.Packet.Attributes;
using Netamite.Server.Definition;

namespace AMP.Network.Packets.Implementation {
    // One 32 KB piece of a mod file, host -> joiner
    [PacketDefinition((byte) PacketType.MOD_FILE)]
    public class ModFilePacket : AMPPacket {
        // Netamite packets are limited to 65,535 bytes: base64 of one chunk (~4/3 of 32 KB) must stay well below that
        public const int MAX_DATA_CHARS = 60000;
        [SyncedVar] public int  transferId;
        [SyncedVar] public int  fileIndex;
        [SyncedVar] public long offset;
        [SyncedVar] public string data; // the chunk, base64: a string is the one payload type every Netamite build handles

        public ModFilePacket() { }

        public ModFilePacket(int transferId, int fileIndex, long offset, byte[] bytes) {
            this.transferId = transferId;
            this.fileIndex  = fileIndex;
            this.offset     = offset;
            this.data       = Convert.ToBase64String(bytes);
            if(data.Length > MAX_DATA_CHARS) throw new ArgumentException("Chunk too large for one packet.");
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
