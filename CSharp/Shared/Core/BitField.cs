using System;
using System.Runtime.CompilerServices;

namespace ScpItdMod.Core {
    public readonly struct BitField {
        public readonly byte Offset;
        public readonly byte Width;
        public readonly uint Mask;

        public BitField(int offset, int width) {
            if ((uint)(width - 1) >= 32u) { throw new ArgumentOutOfRangeException(nameof(width)); }
            if ((uint)offset >= 32u || offset > 32 - width) {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }
            Offset = (byte)offset;
            Width = (byte)width;
            Mask = width == 32 ? uint.MaxValue : (1u << width) - 1u;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Read(uint word) => (word >> Offset) & Mask;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Write(uint word, uint value) =>
            (word & ~(Mask << Offset)) | ((value & Mask) << Offset);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Quantize(float unit) {
            float scaled = unit * Mask + 0.5f;
            if (scaled <= 0.0f) { return 0u; }
            uint raw = (uint)scaled;
            return raw > Mask ? Mask : raw;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Dequantize(uint raw) => raw * (1.0f / Mask);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Next(uint sequence) => (sequence + 1u) & Mask;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Newer(uint sequence, uint previous) {
            uint delta = (sequence - previous) & Mask;
            return delta != 0u && delta <= (Mask >> 1);
        }
    }
}
