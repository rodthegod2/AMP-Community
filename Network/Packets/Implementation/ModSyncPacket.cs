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
        public const byte SETTINGS_SNAPSHOT = 3; // host -> joiner, all server-side values, sent when the joiner is greeted
        public const byte SETTING_CHANGED   = 4; // host -> joiners, one value the host just changed
        public const byte MODS_REQUEST     = 5; // joiner -> host: ModsRequest JSON
        public const byte TRANSFER_START   = 6; // host -> joiner: TransferManifest JSON
        public const byte CHUNK_ACK        = 7; // joiner -> host: transferId, no data
        public const byte TRANSFER_END     = 8; // host -> joiner: TransferEnd JSON
        public const byte TRANSFER_CANCEL  = 9; // joiner -> host: stop sending

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
            if(kind == SETTINGS_SNAPSHOT || kind == SETTING_CHANGED) SettingsSyncClient.OnPacket(this);
            else if(kind == TRANSFER_START || kind == TRANSFER_END) ModTransferClient.OnPacket(this);
            else ModSyncClient.OnPacket(this);
            return true;
        }

        public override bool ProcessServer(NetamiteServer server, ClientData client) {
            if(kind == MODS_REQUEST || kind == CHUNK_ACK || kind == TRANSFER_CANCEL) ModTransferHost.OnPacket(client, this);
            else ModSyncHost.OnPacket(client, this);
            return true;
        }
    }
}
