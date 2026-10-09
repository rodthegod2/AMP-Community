using AMP.Data;
using AMP.Events;
using AMP.Logging;
using AMP.Network.Client;
using AMP.Network.Data;
using AMP.Threading;
using Netamite.Client.Definition;
using Netamite.Network.Packet;
using Netamite.Network.Packet.Attributes;
using Netamite.Server.Definition;
using System;
using ThunderRoad;
using UnityEngine;

namespace AMP.Network.Packets.Implementation {
    [PacketDefinition((byte) PacketType.PLAYER_HEALTH_CHANGE)]
    public class PlayerHealthChangePacket : AMPPacket {
        [SyncedVar]       public int   ClientId;
        [SyncedVar(true)] public float change;
        [SyncedVar]       public bool  doneByPlayer;
        [SyncedVar(true)] public Vector3 pushbackForce;

        public PlayerHealthChangePacket() { }

        public PlayerHealthChangePacket(int ClientId, float change, Vector3 pushbackForce, bool doneByPlayer = false) {
            this.ClientId      = ClientId;
            this.change        = change;
            this.doneByPlayer  = doneByPlayer;
            this.pushbackForce = pushbackForce;
        }

        public override bool ProcessClient(NetamiteClient client) {
            if(ClientId == client.ClientId) {
                if(Player.currentCreature == null) return true;
                if(!Player.currentCreature.initialized) return true;
                if(Player.currentCreature.isKilled) return true;

                Dispatcher.Enqueue(() => {
                    if(change > 0) {
                        Player.currentCreature.Heal(change);
                    } else {
                        Log.Debug(Defines.AMP, $"Received {change} damage.");
                        if(Player.invincibility) {
                            Player.currentCreature.currentHealth -= Math.Abs(change);

                            try {
                                if(Player.currentCreature.currentHealth <= 0) {
                                    Player.currentCreature.Kill();
                                }
                            } catch(NullReferenceException) { }

                            NetworkLocalPlayer.Instance.SendHealthPacket();
                        } else {
                            Player.currentCreature.Damage(Math.Abs(change));
                        }

                        Player.currentCreature.AddForce(pushbackForce, ForceMode.Impulse);
                    }
                });
            }
            return true;
        }

        private const float MAX_CHANGE   = 1000f; // far above anything a weapon does, but finite
        private const float MAX_PUSHBACK = 200f;

        private static bool IsValid(float value) {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static float intensity = 1f;
        public override bool ProcessServer(NetamiteServer server, ClientData client) {
            if(!ModManager.safeFile.hostingSettings.pvpEnable) return true;

            // A client must not be able to forward NaN, infinite or absurd values to another player
            if(!IsValid(change) || !IsValid(pushbackForce.x) || !IsValid(pushbackForce.y) || !IsValid(pushbackForce.z)) return true;
            change        = Mathf.Clamp(change, -MAX_CHANGE, MAX_CHANGE);
            pushbackForce = Vector3.ClampMagnitude(pushbackForce, MAX_PUSHBACK);

            if(client.GetDamageMultiplicator() <= 0) return true;

            if(change < 0) { // Its damage
                change *= client.GetDamageMultiplicator();

                if(change >= 0) return true; // Damage is zero after the damage multiplication

                pushbackForce = client.ApplyPushbackMultiplicator(pushbackForce);
                
                ClientData damaged = ModManager.serverInstance.GetClientById(ClientId);
                if(damaged != null) {

                    if(doneByPlayer) {
                        damaged.player.lastDamager = client;
                    } else {
                        damaged.player.lastDamager = null;
                    }
                    ServerEvents.InvokeOnPlayerDamaged(damaged, change, damaged.player.lastDamager);
                }
            }
            
            server.SendTo(ClientId, this);
            return true;
        }
    }
}
