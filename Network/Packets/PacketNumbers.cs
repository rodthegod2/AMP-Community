namespace AMP.Network.Packets {
    // A joiner's NaN or infinite positions, rotations or velocities are dropped by the host: relayed, they put items and players of everyone
    // on the server at NaN (fuzz test: thousands of Unity errors per second). Plain checks, no reflection: the movement packets this guards
    // arrive many times per second per player. Game independent, so it is unit tested (PacketChecks applies it to Unity's types).
    public static class PacketNumbers {
        public static bool IsFinite(float f) { return !float.IsNaN(f) && !float.IsInfinity(f); }

        public static bool Finite(float x, float y, float z) { return IsFinite(x) && IsFinite(y) && IsFinite(z); }

        public static bool Finite(float x, float y, float z, float w) { return IsFinite(x) && IsFinite(y) && IsFinite(z) && IsFinite(w); }
    }
}
