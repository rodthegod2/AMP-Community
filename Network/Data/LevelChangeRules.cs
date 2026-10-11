using System;

namespace AMP.Network.Data {
    public enum LevelChangeDecision { Proceed, KickMainMenu, PullBack }

    // Server: what happens when a player is the first to load a level other than the session's. Game independent, unit tested.
    public static class LevelChangeRules {
        // MainMenu first: a player who quits to the main menu is let go, never dragged back into the level. Map changes off: everyone but
        // the host's own client (the host decides the map) is sent back to the session's level instead of being kicked.
        public static LevelChangeDecision Decide(string level, bool allowMapChange, bool fromHost) {
            if(string.Equals(level, "mainmenu", StringComparison.OrdinalIgnoreCase)) return LevelChangeDecision.KickMainMenu;
            if(!allowMapChange && !fromHost) return LevelChangeDecision.PullBack;
            return LevelChangeDecision.Proceed;
        }
    }
}
