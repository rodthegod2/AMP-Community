using AMP.ModSync;
using AMP.Network.Data;
using Netamite.Client.Definition;
using Netamite.Network.Packet;
using Netamite.Network.Packet.Attributes;
using Netamite.Server.Definition;
using System.Collections.Generic;
using System.Threading;

namespace AMP.Network.Packets.Implementation {
    // Carries mod sync JSON messages (possibly split into parts) in both directions.
    [PacketDefinition((byte) PacketType.MOD_SYNC)]
    public class ModSyncPacket : AMPPacket {
        public const byte HOST_MOD_SET  = 1;
        public const byte JOINER_REPORT = 2;

        [SyncedVar] public byte   kind;
        [SyncedVar] public int    transferId;
        [SyncedVar] public int    part;
        [SyncedVar] public int    parts;
        [SyncedVar] public string data;

        private static int nextTransferId;

        public ModSyncPacket() { }

        public ModSyncPacket(byte kind, int transferId, int part, int parts, string data) {
            this.kind       = kind;
            this.transferId = transferId;
            this.part       = part;
            this.parts      = parts;
            this.data       = data;
        }

        internal static List<ModSyncPacket> Create(byte kind, string json) {
            int id = Interlocked.Increment(ref nextTransferId);
            List<string> chunks = MessageChunks.Split(json);
            var packets = new List<ModSyncPacket>(chunks.Count);
            for(int i = 0; i < chunks.Count; i++) packets.Add(new ModSyncPacket(kind, id, i, chunks.Count, chunks[i]));
            return packets;
        }

        public override bool ProcessClient(NetamiteClient client) {
            ModSyncClient.OnPacket(this);
            return true;
        }

        public override bool ProcessServer(NetamiteServer server, ClientData client) {
            ModSyncHost.OnPacket(client, this);
            return true;
        }
    }
}
