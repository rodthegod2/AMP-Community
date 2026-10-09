using AMP.Data;
using AMP.Logging;
using AMP.Network.Data;
using AMP.Threading;
using Netamite.Client.Definition;
using Netamite.Network.Packet;
using Netamite.Network.Packet.Attributes;
using Netamite.Server.Definition;
using UnityEngine;

namespace AMP.Network.Packets.Implementation {
    [PacketDefinition((byte) PacketType.MODERATION_VOTE_KICK)]
    public class ModerationVoteKickPacket : AMPPacket {
        [SyncedVar] public int ClientId;
        
        public ModerationVoteKickPacket() { }
        
        public ModerationVoteKickPacket(int ClientId) {
            this.ClientId = ClientId;
        }
        
        public override bool ProcessClient(NetamiteClient client) {
            return true;
        }

        public override bool ProcessServer(NetamiteServer server, ClientData client) {
            if (client.ClientId == ClientId) {
                client.ShowText("votekick", "You can't votekick yourself.", 0, Color.red, 5);
                return true;
            }
            if (server.ConnectedClients <= 2) {
                client.ShowText("votekick", "Not enough players for a votekick.", 0, Color.red, 5);
                return true;
            }
            
            
            ClientData target = (ClientData) server.GetClientById(ClientId);
            
            // Dont allow a vote kick on a lobby admin
            if (target.permissionLevel >= Datatypes.PermissionLevel.LOBBY_ADMIN) {
                client.ShowText("votekick", "You can't vote to kick a admin.", 0, Color.red, 5);
                return true;
            }

            // Only add vote if not already there
            if (!target.votersForKick.Contains(client))
                target.votersForKick.Add(client);
            
            // Remove all votes from disconnected clients
            foreach(ClientData voter in target.votersForKick.ToArray()) {
                if(server.GetClientById(voter.ClientId) == null) {
                    target.votersForKick.Remove(voter);
                }
            }

            // Check vote threshold
            int requiredVotes = Mathf.CeilToInt(server.ConnectedClients * 0.6f);
            if (target.votersForKick.Count >= requiredVotes) {
                ModManager.serverInstance.netamiteServer.SendToAll(
                    new DisplayTextPacket("votekick", $"{client.ClientName} has been voted out.", Color.yellow, new Vector3(0, 0, 2), true, true, 5)
                );
                target.TempBan("You have been votekicked!");
            } else {
                ModManager.serverInstance.netamiteServer.SendToAll(
                    new DisplayTextPacket("votekick", $"Votekick for {client.ClientName} started.\n{target.votersForKick.Count} / {requiredVotes} Votes", Color.yellow, new Vector3(0, 0, 2), true, true, 5)
                );
            }

            return true;
        }
    }
}
