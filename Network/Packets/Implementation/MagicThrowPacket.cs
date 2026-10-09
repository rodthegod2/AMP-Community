using AMP.Datatypes;
using AMP.Network.Data;
using AMP.Network.Helper;
using AMP.Threading;
using Netamite.Client.Definition;
using Netamite.Network.Packet;
using Netamite.Network.Packet.Attributes;
using Netamite.Server.Definition;
using ThunderRoad;
using ThunderRoad.Skill.Spell;
using UnityEngine;

namespace AMP.Network.Packets.Implementation {
    // The game only computes a spell throw for the local player (SpellCastCharge.Fire(false) reads the controller velocity),
    // so remote casters never "release" their spell on other screens. This replays the throw on the caster's puppet.
    [PacketDefinition((byte) PacketType.MAGIC_THROW)]
    internal class MagicThrowPacket : AMPPacket {
        [SyncedVar]       public byte    handIndex;
        [SyncedVar]       public int     casterNetworkId;
        [SyncedVar]       public byte    casterType;
        [SyncedVar(true)] public float   currentCharge;
        [SyncedVar]       public Vector3 velocity;

        private const float MAX_VELOCITY = 50f; // a hand never moves this fast, but keeps the value finite

        public MagicThrowPacket() { }

        public MagicThrowPacket(byte handIndex, int casterNetworkId, ItemHolderType casterType, float currentCharge, Vector3 velocity) {
            this.handIndex       = handIndex;
            this.casterNetworkId = casterNetworkId;
            this.casterType      = (byte) casterType;
            this.currentCharge   = currentCharge;
            this.velocity        = velocity;
        }

        public override bool ProcessClient(NetamiteClient client) {
            Creature c = SyncFunc.GetCreature((ItemHolderType) casterType, casterNetworkId);
            if(c == null || handIndex > (byte) Side.Left) return true;

            SpellCaster caster = c.GetHand((Side) handIndex)?.caster;
            if(caster == null) return true;

            Dispatcher.Enqueue(() => {
                // Projectile spells (fireball and mods built on it) already arrive as synced items, replaying them would double the projectile
                if(!(caster.spellInstance is SpellCastCharge charge) || charge is SpellCastProjectile) return;

                charge.currentCharge = currentCharge;
                charge.Throw(velocity);
            });
            return true;
        }

        public override bool ProcessServer(NetamiteServer server, ClientData client) {
            if(!IsValid(currentCharge) || !IsValid(velocity.x) || !IsValid(velocity.y) || !IsValid(velocity.z)) return true;
            currentCharge = Mathf.Clamp01(currentCharge);
            velocity      = Vector3.ClampMagnitude(velocity, MAX_VELOCITY);

            server.SendToAllExcept(this, client.ClientId);
            return true;
        }

        private static bool IsValid(float value) {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
