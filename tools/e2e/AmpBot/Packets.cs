using Netamite.Network.Packet;
using Netamite.Network.Packet.Attributes;
using System.Collections.Generic;
using System.Threading;
using AMP.ModSync;

// Copies of the AMP packets the bot speaks. Netamite serializes the [SyncedVar] fields in declaration order,
// so ids, field types and order must match AMP's Network/Packets/Implementation/*.cs exactly (AMP.dll is never loaded here).
namespace AmpBot {
    // AMP ModSyncPacket (PacketType.MOD_SYNC = 111)
    [PacketDefinition((byte) 111)]
    public class ModSyncPacket : NetPacket {
        public const byte HOST_MOD_SET      = 1;
        public const byte JOINER_REPORT     = 2;
        public const byte SETTINGS_SNAPSHOT = 3;
        public const byte SETTING_CHANGED   = 4;
        public const byte MODS_REQUEST      = 5;
        public const byte TRANSFER_START    = 6;
        public const byte CHUNK_ACK         = 7;
        public const byte TRANSFER_END      = 8;
        public const byte TRANSFER_CANCEL   = 9;
        public const byte READY_NEXT        = 10;

        [SyncedVar] public byte   kind;
        [SyncedVar] public int    transferId;
        [SyncedVar] public int    part;
        [SyncedVar] public int    parts;
        [SyncedVar] public string data;

        private static int nextTransferId;

        public ModSyncPacket() { }

        public ModSyncPacket(byte kind, int transferId, int part, int parts, string data) {
            this.kind = kind;
            this.transferId = transferId;
            this.part = part;
            this.parts = parts;
            this.data = data;
        }

        // Same splitting as AMP's ModSyncPacket.Create
        public static List<ModSyncPacket> Create(byte kind, string json) {
            int id = Interlocked.Increment(ref nextTransferId);
            List<string> chunks = MessageChunks.Split(json);
            var packets = new List<ModSyncPacket>(chunks.Count);
            for(int i = 0; i < chunks.Count; i++) packets.Add(new ModSyncPacket(kind, id, i, chunks.Count, chunks[i]));
            return packets;
        }
    }

    // AMP ModFilePacket (PacketType.MOD_FILE = 112)
    [PacketDefinition((byte) 112)]
    public class ModFilePacket : NetPacket {
        [SyncedVar] public int    transferId;
        [SyncedVar] public int    fileIndex;
        [SyncedVar] public long   offset;
        [SyncedVar] public string data; // base64

        public ModFilePacket() { }

        public ModFilePacket(int transferId, int fileIndex, long offset, string data) {
            this.transferId = transferId;
            this.fileIndex = fileIndex;
            this.offset = offset;
            this.data = data;
        }
    }

    // AMP ServerInfoPacket (PacketType.SERVER_INFO = 253): the first packet of the host's greeting
    [PacketDefinition((byte) 253)]
    public class ServerInfoPacket : NetPacket {
        [SyncedVar] public string version = "";
        [SyncedVar] public int    max_players = 99;
        [SyncedVar] public bool   allow_voicechat = false;
        [SyncedVar] public byte   base_tickrate = 10;
        [SyncedVar] public byte   player_tickrate = 10;

        public ServerInfoPacket() { }

        public ServerInfoPacket(string version, int maxPlayers) {
            this.version = version;
            this.max_players = maxPlayers;
        }
    }

    // ThunderRoad.EventTime (int enum): OnStart = 0, OnEnd = 1
    public enum EventTime { OnStart, OnEnd }

    // AMP LevelChangePacket (PacketType.DO_LEVEL_CHANGE = 41). The host sends it while greeting; a joiner that is in that level
    // already echoes it back (eventTime OnEnd) and only then gets the rest of the greeting (players, items, settings snapshot).
    [PacketDefinition((byte) 41)]
    public class LevelChangePacket : NetPacket {
        [SyncedVar] public string    level = "";
        [SyncedVar] public string    mode = "";
        [SyncedVar] public string[]  options = new string[0];
        [SyncedVar] public EventTime eventTime = EventTime.OnEnd;

        public LevelChangePacket() { }

        public LevelChangePacket(string level, string mode, string[] options, EventTime eventTime) {
            this.level = level;
            this.mode = mode;
            this.options = options ?? new string[0];
            this.eventTime = eventTime;
        }
    }
}
