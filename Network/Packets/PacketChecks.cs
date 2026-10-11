using UnityEngine;

namespace AMP.Network.Packets {
    // PacketNumbers for Unity's vector types, used by the packets' HasValidNumbers
    internal static class PacketChecks {
        internal static bool Ok(float f)      { return PacketNumbers.IsFinite(f); }
        internal static bool Ok(Vector3 v)    { return PacketNumbers.Finite(v.x, v.y, v.z); }
        internal static bool Ok(Quaternion q) { return PacketNumbers.Finite(q.x, q.y, q.z, q.w); }

        internal static bool Ok(Vector3[] a) {
            if(a == null) return true;
            for(int i = 0; i < a.Length; i++) if(!Ok(a[i])) return false;
            return true;
        }

        internal static bool Ok(Quaternion[] a) {
            if(a == null) return true;
            for(int i = 0; i < a.Length; i++) if(!Ok(a[i])) return false;
            return true;
        }
    }
}
