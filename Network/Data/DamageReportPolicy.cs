namespace AMP.Network.Data {
    // Who reports a hit to the server (audit finding 3). Every client that saw a hit used to report it, so one NPC swing at a player was
    // applied once by the victim's own game plus once per observer (PvP on, the default), and a player's hit on an NPC was reported by the
    // attacker and by the NPC's owner. Game independent, so it is unit tested.
    public static class DamageReportPolicy {
        // A remote player's avatar (puppet) was hurt here: only what the local player did counts. NPC and environment damage to that player
        // is applied by the player's own game.
        public static bool ReportPlayerPuppetDamage(bool doneByLocalPlayer) {
            return doneByLocalPlayer;
        }

        // An NPC was hurt here: a remote player's hit is reported by that player's client; the local player's hit by us; anything else
        // (other NPCs, falls, the environment) by the NPC's owner only
        public static bool ReportCreatureDamage(bool doneByLocalPlayer, bool doneByRemotePlayer, bool ownsCreature) {
            if(doneByRemotePlayer && !doneByLocalPlayer) return false;
            return doneByLocalPlayer || ownsCreature;
        }
    }
}
