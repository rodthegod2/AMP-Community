namespace AMP.Network.Server {
    /// <summary>
    /// Decides whether a creature may change owner. Kept free of game and network types so it can be unit tested.
    /// </summary>
    internal static class OwnershipPolicy {

        /// <param name="oldOwnerId">Current owner, 0 or less if nobody owns the creature yet</param>
        /// <param name="newOwnerId">The client asking to become the owner</param>
        /// <param name="lockedByGrab">The current owner holds a static (grab) lock</param>
        /// <param name="oldOwnerConnected">The current owner is still connected</param>
        /// <param name="hostAuthority">Host decides all NPC behaviour unless somebody explicitly grabs one</param>
        /// <param name="hostId">Client id of the host, 0 if unknown (e.g. dedicated server)</param>
        /// <param name="grabRequest">The request is an explicit grab, not just damage or proximity</param>
        /// <param name="secondsSinceLastTransfer">Time since the last owner change, infinity if there was none</param>
        /// <param name="holdSeconds">Minimum time between two non-grab transfers, 0 disables it</param>
        internal static bool IsTransferAllowed(int oldOwnerId, int newOwnerId, bool lockedByGrab, bool oldOwnerConnected,
                                               bool hostAuthority, int hostId, bool grabRequest,
                                               double secondsSinceLastTransfer, float holdSeconds) {
            if(oldOwnerId <= 0 || oldOwnerId == newOwnerId) return true; // Nothing to take over, or no change

            if(lockedByGrab) return false;            // Somebody is physically holding it
            if(!oldOwnerConnected) return true;       // Owner left, anybody can take over
            if(grabRequest) return true;              // Grabbing needs authority, so it always wins

            // Damage alone must not move the NPC's brain away from the host
            if(hostAuthority && hostId > 0 && oldOwnerId == hostId) return false;

            // Stops two players fighting over the same NPC from flipping its owner on every hit
            if(holdSeconds > 0 && secondsSinceLastTransfer < holdSeconds) return false;

            return true;
        }
    }
}
