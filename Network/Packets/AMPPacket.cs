using AMP.Network.Data;
using Netamite.Network.Packet;
using Netamite.Server.Data;
using Netamite.Server.Definition;

namespace AMP.Network.Packets {
    public class AMPPacket : NetPacket {

        /// <summary>
        /// This will be called when the packet is received on the Server.
        /// </summary>
        /// <param name="server">Server Instance that is processing the  packet</param>
        /// <param name="client">Client Information that sent the Packet</param>
        /// <returns></returns>
        public virtual bool ProcessServer(NetamiteServer server, ClientData client) { return false; }

        public override bool ProcessServer(NetamiteServer server, ClientInformation client) {
            if(!PacketNumbers.AllFinite(this)) {
                // Handled (dropped): never relayed to the other players
                if(warned.TryAdd(GetType().Name + ":" + client.ClientId, true)) AMP.Logging.Log.Warn(AMP.Data.Defines.SERVER, client.ClientName + " sent a " + GetType().Name + " with invalid numbers (NaN/infinite), ignoring those.");
                return true;
            }
            return ProcessServer(server, (ClientData) client);
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> warned = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();
    }
}
