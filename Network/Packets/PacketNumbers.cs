using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;

namespace AMP.Network.Packets {
    // A joiner's packet with NaN or infinite vectors (positions, rotations, velocities: Vector3, Quaternion, any struct of floats) is dropped
    // by the host: relayed, it put items and players of everyone on the server at NaN (Unity logs every frame, physics breaks).
    // Plain float fields are left alone: health can legitimately be infinite. Game independent, so it is unit tested.
    public static class PacketNumbers {
        private static readonly ConcurrentDictionary<Type, FieldInfo[]> numberFields = new ConcurrentDictionary<Type, FieldInfo[]>();

        // True when every float of every struct field (Vector3, Quaternion, ...) and of every element of arrays of those is a finite number
        public static bool AllFinite(object packet) {
            if(packet == null) return true;
            foreach(FieldInfo field in FieldsOf(packet.GetType())) {
                object value = field.GetValue(packet);
                if(value == null) continue;
                Array array = value as Array;
                if(array != null) {
                    foreach(object element in array) if(!Finite(element)) return false;
                } else if(!Finite(value)) return false;
            }
            return true;
        }

        private static bool Finite(object value) {
            if(value == null) return true;
            Type type = value.GetType();
            if(!type.IsValueType || type.IsPrimitive || type.IsEnum) return true;
            foreach(FieldInfo f in floatFields.GetOrAdd(type, t => t.GetFields(BindingFlags.Public | BindingFlags.Instance).Where(x => x.FieldType == typeof(float)).ToArray())) {
                if(!IsFinite((float) f.GetValue(value))) return false;
            }
            return true;
        }

        private static readonly ConcurrentDictionary<Type, FieldInfo[]> floatFields = new ConcurrentDictionary<Type, FieldInfo[]>();

        public static bool IsFinite(float f) { return !float.IsNaN(f) && !float.IsInfinity(f); }

        // Public instance fields that are structs (Vector3...) or arrays of them
        private static FieldInfo[] FieldsOf(Type type) {
            return numberFields.GetOrAdd(type, t => t.GetFields(BindingFlags.Public | BindingFlags.Instance).Where(f => CanHoldNumber(f.FieldType)).ToArray());
        }

        private static bool CanHoldNumber(Type t) {
            if(t.IsArray) return CanHoldNumber(t.GetElementType());
            return t.IsValueType && !t.IsPrimitive && !t.IsEnum;
        }
    }
}
