using Netamite.Converter;
using System;

namespace AMP.ModSync {
    // Byte payload for packets. A plain byte[] field is serialized one boxed element at a time (~1.5 MB/s);
    // a class field with its own converter is copied in one go. Netamite finds the converter by scanning loaded assemblies.
    public class Blob {
        public byte[] bytes = new byte[0];
    }

    public class BlobConverter : DataConverterInterface<Blob> {
        public static new byte[] GetBytes(Blob value, bool lowPrecision = false) {
            byte[] source = value == null || value.bytes == null ? new byte[0] : value.bytes;
            if(source.Length > ushort.MaxValue) throw new ArgumentException("Blob too large for one packet.");
            byte[] output = new byte[2 + source.Length];
            BitConverter.GetBytes((ushort) source.Length).CopyTo(output, 0);
            Buffer.BlockCopy(source, 0, output, 2, source.Length);
            return output;
        }

        public static new Blob FromBytes(byte[] value, out int bytesRead, int startIndex = 0, bool lowPrecision = false) {
            int length = BitConverter.ToUInt16(value, startIndex);
            byte[] bytes = new byte[length];
            Buffer.BlockCopy(value, startIndex + 2, bytes, 0, length);
            bytesRead = 2 + length;
            return new Blob { bytes = bytes };
        }
    }
}
