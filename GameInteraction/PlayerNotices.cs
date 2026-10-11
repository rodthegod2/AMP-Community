using AMP.GameInteraction.Components;
using AMP.Network.Packets.Implementation;
using AMP.Threading;
using UnityEngine;

namespace AMP.GameInteraction {
    // Short local lines when another player joins, leaves or dies. Client side only, no protocol change.
    internal static class PlayerNotices {
        private const float QuietAfterConnect = 15f; // the greeting lists everyone already there: not "joined" news
        private static float quietUntil;

        // Main thread, when a connection starts
        internal static void ConnectionStarted() {
            quietUntil = Time.realtimeSinceStartup + QuietAfterConnect;
        }

        internal static void Joined(string name) { Show(Name(name) + " joined", true); }
        internal static void Left(string name)   { Show(Name(name) + " left", false); }
        internal static void Died(string name)   { Show(Name(name) + " died", false); }

        private static string Name(string name) { return string.IsNullOrEmpty(name) ? "A player" : name; }

        // Any thread
        private static void Show(string text, bool quietAfterConnect) {
            Dispatcher.Enqueue(() => {
                if((quietAfterConnect && Time.realtimeSinceStartup < quietUntil) || ModManager.clientInstance == null) return;
                Logging.Log.Info(Data.Defines.CLIENT, "Player notice: " + text);
                TextDisplay.ShowTextDisplay(new DisplayTextPacket("player_notice", text, Color.white, Vector3.forward * 2 + Vector3.down * 0.4f, true, true, 4));
            });
        }
    }
}
