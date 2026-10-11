using Netamite.Datatype;
using Netamite.Network.Packet;
using Netamite.Network.Packet.Attributes;

// Copies of the AMP gameplay packets the bot sends or reads (same ids, reliability, field types and order as
// AMP's Network/Packets/Implementation/*.cs; --selftest compares them). Netamite's own Vector3/Color/Quaternion have the same
// wire format as Unity's (Netamite.Unity's converters wrap them).
namespace AmpBot {
    [PacketDefinition((byte) 10)] // PLAYER_DATA
    public class PlayerDataPacket : NetPacket {
        [SyncedVar]       public int     clientId;
        [SyncedVar]       public string  name;
        [SyncedVar]       public string  creatureId;
        [SyncedVar(true)] public float   height;
        [SyncedVar]       public Vector3 playerPos;
        [SyncedVar(true)] public float   playerRotY;
        [SyncedVar]       public string  uniqueId;
    }

    [PacketDefinition(false, (byte) 11)] // PLAYER_POSITION
    public class PlayerPositionPacket : NetPacket {
        [SyncedVar]       public int     playerId;
        [SyncedVar(true)] public Vector3 handLeftPos;
        [SyncedVar(true)] public Vector3 handLeftRot;
        [SyncedVar(true)] public Vector3 handRightPos;
        [SyncedVar(true)] public Vector3 handRightRot;
        [SyncedVar(true)] public Vector3 headPos;
        [SyncedVar(true)] public Vector3 headRot;
        [SyncedVar]       public Vector3 position;
        [SyncedVar(true)] public float   rotationY;
    }

    [PacketDefinition((byte) 12)] // PLAYER_EQUIPMENT
    public class PlayerEquipmentPacket : NetPacket {
        [SyncedVar] public int      clientId;
        [SyncedVar] public Color[]  colors;
        [SyncedVar] public string[] equipment;
        [SyncedVar] public string   ethnicGroup;
    }

    [PacketDefinition(false, (byte) 13)] // PLAYER_RAGDOLL: what real AMP clients send instead of PLAYER_POSITION (full body sync)
    public class PlayerRagdollPacket : NetPacket {
        [SyncedVar]       public long         timestamp;
        [SyncedVar]       public int          playerId;
        [SyncedVar]       public Vector3      position;
        [SyncedVar(true)] public float        rotationY;
        [SyncedVar(true)] public Vector3      velocity;
        [SyncedVar(true)] public float        rotationYVel;
        [SyncedVar(true)] public Vector3[]    ragdollPositions;
        [SyncedVar(true)] public Quaternion[] ragdollRotations;
        [SyncedVar(true)] public Vector3[]    velocities;
        [SyncedVar(true)] public Vector3[]    angularVelocities;
    }

    [PacketDefinition((byte) 20)] // ITEM_SPAWN
    public class ItemSpawnPacket : NetPacket {
        [SyncedVar]       public int     itemId;
        [SyncedVar]       public string  type;
        [SyncedVar]       public byte    category;
        [SyncedVar]       public int     clientsideId;
        [SyncedVar]       public Vector3 position;
        [SyncedVar(true)] public Vector3 rotation;
        [SyncedVar]       public bool    isMagicProjectile;
    }

    [PacketDefinition(true, (byte) 22)] // ITEM_POSITION
    public class ItemPositionPacket : NetPacket {
        [SyncedVar]       public long    timestamp;
        [SyncedVar]       public int     itemId;
        [SyncedVar]       public Vector3 position;
        [SyncedVar(true)] public Vector3 rotation;
        [SyncedVar(true)] public Vector3 velocity;
        [SyncedVar(true)] public Vector3 angularVelocity;
    }

    [PacketDefinition((byte) 23)] // ITEM_OWNER
    public class ItemOwnerPacket : NetPacket {
        [SyncedVar] public int  itemId;
        [SyncedVar] public bool owning;
    }

    public enum DrawSlot { None, BackRight, BackLeft, HipsRight, HipsLeft } // ThunderRoad Holder.DrawSlot (int)
    public enum Side { Left, Right }                                        // ThunderRoad Side (int)
    public enum ItemHolderType : byte { NONE, ITEM, PLAYER, CREATURE }      // AMP Datatypes.ItemHolderType

    // AMP Network.Data.ItemHoldingState
    public class ItemHoldingState {
        [SyncedVar] public int            holderNetworkId;
        [SyncedVar] public DrawSlot       equipmentSlot;
        [SyncedVar] public byte           holdingIndex;
        [SyncedVar] public byte           orientationIndex;
        [SyncedVar] public Side           holdingSide;
        [SyncedVar] public ItemHolderType holderType;
    }

    [PacketDefinition((byte) 24)] // ITEM_SNAPPING_SNAP
    public class ItemSnapPacket : NetPacket {
        [SyncedVar] public int                itemId;
        [SyncedVar] public ItemHoldingState[] itemHoldingStates;
    }

    [PacketDefinition((byte) 16)] // PLAYER_TELEPORT
    public class PlayerTeleportPacket : NetPacket {
        [SyncedVar] public Vector3 targetPosition;
        [SyncedVar] public float   targetRotation;
    }

    [PacketDefinition((byte) 26)] // ITEM_IMBUE
    public class ItemImbuePacket : NetPacket {
        [SyncedVar]       public int    itemId;
        [SyncedVar]       public string type;
        [SyncedVar]       public byte   index;
        [SyncedVar(true)] public float  amount;
    }

    [PacketDefinition((byte) 50)] // CREATURE_SPAWN
    public class CreatureSpawnPacket : NetPacket {
        [SyncedVar]       public int      creatureId;
        [SyncedVar]       public int      clientsideId;
        [SyncedVar]       public string   type;
        [SyncedVar]       public string   container;
        [SyncedVar]       public byte     factionId;
        [SyncedVar]       public Vector3  position;
        [SyncedVar(true)] public float    rotationY;
        [SyncedVar]       public float    health;
        [SyncedVar]       public float    maxHealth;
        [SyncedVar(true)] public float    height;
        [SyncedVar]       public string[] equipment = new string[0];
        [SyncedVar]       public Color[]  colors    = new Color[0];
        [SyncedVar]       public string   ethnicGroup = "";
    }

    [PacketDefinition((byte) 110)] // MOD_LIST
    public class ModListPacket : NetPacket {
        [SyncedVar] public string[] modList;
    }

    [PacketDefinition((byte) 251)] // ALLOW_TRANSMISSION
    public class AllowTransmissionPacket : NetPacket {
        [SyncedVar] public bool allow;
    }
}
