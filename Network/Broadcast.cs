using AMP.Network.Data;
using Netamite.Network.Packet;
using Netamite.Server.Data;
using Netamite.Server.Definition;
using System;

namespace AMP.Network {
    // Gameplay broadcasts only go to players. A joiner that is still in mod sync (checking, setting up, downloading mods) isn't one yet:
    // the traffic would compete with its download and fill its sync data with things it can't use. When it is greeted it gets
    // the whole state anyway (players, items, creatures, level), so leaving it out loses nothing.
    internal static class Broadcast {
        internal static void SendToPlaying(this NetamiteServer server, NetPacket packet) {
            SendToPlayingExcept(server, packet);
        }

        internal static void SendToPlayingExcept(this NetamiteServer server, NetPacket packet, params int[] except) {
            foreach(ClientInformation client in server.Clients) {
                if(except != null && Array.IndexOf(except, client.ClientId) >= 0) continue;
                ClientData data = client as ClientData;
                if(data != null && !ModSync.ModSyncHost.IsPlaying(data)) continue;
                server.SendTo(client, packet);
            }
        }
    }
}
