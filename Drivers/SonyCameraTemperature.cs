using System;
using System.Buffers.Binary;

namespace NINA.RetroKiwi.Plugin.SonyCamera.Drivers {
    // Sony's CameraTemperature is distinct from battery/ambient temperature. It is
    // recorded in tag 0x9403 after a photo, not exposed as a live MTP property.
    internal static class SonyCameraTemperature {
        internal static double Read(byte[] rawBytes) {
            if (rawBytes == null || rawBytes.Length < 8) return double.NaN;
            ReadOnlySpan<byte> data = rawBytes;
            bool littleEndian = data[0] == 'I' && data[1] == 'I';
            if (!littleEndian && !(data[0] == 'M' && data[1] == 'M')) return double.NaN;
            if (Read16(data, 2, littleEndian) != 42) return double.NaN;

            uint firstIfd = Read32(data, 4, littleEndian);
            if (!FindEntry(data, firstIfd, data.Length, 0x010F, littleEndian, out int make) ||
                Read16(data, make + 2, littleEndian) != 2 ||
                Read32(data, make + 4, littleEndian) != 5) return double.NaN;
            uint makeOffset = Read32(data, make + 8, littleEndian);
            if (!InRange(makeOffset, 5, data.Length) ||
                !data.Slice((int)makeOffset, 5).SequenceEqual("SONY\0"u8)) return double.NaN;

            if (!FindEntry(data, firstIfd, data.Length, 0x8769, littleEndian, out int exif) ||
                Read16(data, exif + 2, littleEndian) != 4 ||
                Read32(data, exif + 4, littleEndian) != 1) return double.NaN;
            uint exifIfd = Read32(data, exif + 8, littleEndian);
            if (!FindEntry(data, exifIfd, data.Length, 0x927C, littleEndian, out int maker) ||
                Read16(data, maker + 2, littleEndian) != 7) return double.NaN;
            uint makerLength = Read32(data, maker + 4, littleEndian);
            uint makerOffset = Read32(data, maker + 8, littleEndian);
            if (makerLength < 6 || !InRange(makerOffset, makerLength, data.Length)) return double.NaN;
            int makerEnd = (int)((long)makerOffset + makerLength);

            // ARW maker notes may be a bare IFD (A7 III) or have a 12-byte Sony
            // header. In both layouts, value offsets are relative to the TIFF.
            uint makerIfd = makerOffset;
            if (makerLength >= 12 && data.Slice((int)makerOffset, 12).SequenceEqual("SONY DSC \0\0\0"u8)) {
                makerIfd += 12;
            }
            if (!FindEntry(data, makerIfd, makerEnd, 0x9403, littleEndian, out int temperature) ||
                Read16(data, temperature + 2, littleEndian) != 7) return double.NaN;
            uint length = Read32(data, temperature + 4, littleEndian);
            uint offset = Read32(data, temperature + 8, littleEndian);
            if (length < 6 || offset < makerOffset || !InRange(offset, length, makerEnd)) return double.NaN;

            // Sony enciphers bytes 0..248 as (value^3) mod 249; 249..255 are
            // unchanged. The validity flag and signed Celsius byte follow the
            // documented ExifTool Tag9403 layout (bytes 4 and 5).
            byte valid = Decipher(data[(int)offset + 4]);
            if (valid == 0 || valid >= 100) return double.NaN;
            return unchecked((sbyte)Decipher(data[(int)offset + 5]));
        }

        private static bool FindEntry(ReadOnlySpan<byte> data, uint offset, int end, ushort tag,
            bool littleEndian, out int entry) {
            entry = 0;
            if (!InRange(offset, 2, end)) return false;
            int count = Read16(data, (int)offset, littleEndian);
            if (!InRange(offset, 2L + 12L * count + 4, end)) return false;
            for (int i = 0; i < count; i++) {
                int current = (int)offset + 2 + 12 * i;
                if (Read16(data, current, littleEndian) == tag) {
                    entry = current;
                    return true;
                }
            }
            return false;
        }

        private static bool InRange(uint offset, long length, int end) => offset <= end && length <= end - (long)offset;
        private static ushort Read16(ReadOnlySpan<byte> data, int offset, bool littleEndian) => littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2))
            : BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
        private static uint Read32(ReadOnlySpan<byte> data, int offset, bool littleEndian) => littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4))
            : BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
        private static byte Decipher(byte encoded) {
            if (encoded >= 249) return encoded;
            for (int value = 0; value < 249; value++) {
                if (value * value * value % 249 == encoded) return (byte)value;
            }
            return encoded;
        }
    }
}
