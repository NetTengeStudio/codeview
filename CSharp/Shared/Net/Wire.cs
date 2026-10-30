using System;
using System.Buffers.Binary;
using System.Text;

namespace ScpItdMod.Net {
    public sealed class WireWriter {
        private byte[] buffer;
        private int length;

        public WireWriter(int capacity = 256) {
            if (capacity < 0) { throw new ArgumentOutOfRangeException(nameof(capacity)); }
            buffer = capacity == 0 ? Array.Empty<byte>() : new byte[capacity];
        }

        public int Length => length;

        public ReadOnlySpan<byte> Span => new ReadOnlySpan<byte>(buffer, 0, length);

        public byte[] Buffer => buffer;

        public void Clear() => length = 0;

        public void U8(byte value) {
            Ensure(1);
            buffer[length++] = value;
        }

        public void Bool(bool value) => U8(value ? (byte)1 : (byte)0);

        public void U16(ushort value) {
            Ensure(2); BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(length), value); length += 2;
        }

        public void F32(float value) => U32(BitConverter.SingleToUInt32Bits(value));

        public void U32(uint value) {
            Ensure(4);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(length), value);
            length += 4;
        }

        public void U64(ulong value) {
            Ensure(8);
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(length), value);
            length += 8;
        }

        public void Var(ulong value) {
            Ensure(10);
            while (value >= 0x80) {
                buffer[length++] = (byte)(value | 0x80);
                value >>= 7;
            }
            buffer[length++] = (byte)value;
        }

        public void Text(string value) {
            value ??= "";
            int bytes = Encoding.UTF8.GetByteCount(value);
            Var((ulong)bytes);
            Ensure(bytes);
            length += Encoding.UTF8.GetBytes(value, 0, value.Length, buffer, length);
        }

        public void Bytes(ReadOnlySpan<byte> value) {
            Var((ulong)value.Length);
            Ensure(value.Length);
            value.CopyTo(buffer.AsSpan(length));
            length += value.Length;
        }

        public void Raw(ReadOnlySpan<byte> value) {
            Ensure(value.Length);
            value.CopyTo(buffer.AsSpan(length));
            length += value.Length;
        }

        private void Ensure(int extra) {
            if (extra < 0 || length > Array.MaxLength - extra) {
                throw new ArgumentOutOfRangeException(nameof(extra), "Wire buffer size exceeds array bounds");
            }
            int needed = length + extra;
            if (needed <= buffer.Length) { return; }
            int size = Math.Max(256, buffer.Length);
            while (size < needed) {
                if (size > Array.MaxLength / 2) { size = needed; break; }
                size *= 2;
            }
            Array.Resize(ref buffer, size);
        }
    }

    public ref struct WireReader {
        private readonly ReadOnlySpan<byte> data;
        private int position;

        public WireReader(ReadOnlySpan<byte> data) {
            this.data = data;
            position = 0;
            Failed = false;
        }

        public bool Failed { get; private set; }

        public bool End => position == data.Length;

        public int Remaining => data.Length - position;

        public byte U8() {
            if (!Take(1)) { return 0; }
            return data[position - 1];
        }

        public bool Bool() {
            byte value = U8();
            if (value > 1) { Failed = true; }
            return value == 1;
        }

        public uint U32() {
            if (!Take(4)) { return 0; }
            return BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(position - 4));
        }

        public ushort U16() {
            if (!Take(2)) { return 0; }
            return BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(position - 2));
        }

        public float F32() => BitConverter.UInt32BitsToSingle(U32());

        public ulong U64() {
            if (!Take(8)) { return 0; }
            return BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(position - 8));
        }

        public ulong Var() {
            ulong value = 0;
            for (int shift = 0; shift < 64; shift += 7) {
                if (!Take(1)) { return 0; }
                byte b = data[position - 1];
                if (shift == 63 && b > 1) { break; }
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) { return value; }
            }
            Failed = true;
            return 0;
        }

        public int Count(int max) {
            ulong value = Var();
            if (value > (ulong)max) {
                Failed = true;
                return 0;
            }
            return (int)value;
        }

        public string Text(int maxBytes) {
            int bytes = Count(maxBytes);
            if (Failed || !Take(bytes)) { return ""; }
            if (bytes == 0) { return ""; }
            try {
                return Strict.GetString(data.Slice(position - bytes, bytes));
            } catch (DecoderFallbackException) {
                Failed = true;
                return "";
            }
        }

        public void SkipText(int maxBytes) {
            ReadOnlySpan<byte> bytes = Bytes(maxBytes);
            if (Failed) { return; }
            try {
                Strict.GetCharCount(bytes);
            } catch (DecoderFallbackException) {
                Failed = true;
            }
        }

        public ReadOnlySpan<byte> Bytes(int maxBytes) {
            int bytes = Count(maxBytes);
            if (Failed || !Take(bytes)) { return ReadOnlySpan<byte>.Empty; }
            return data.Slice(position - bytes, bytes);
        }

        private bool Take(int bytes) {
            if (Failed || bytes < 0 || bytes > data.Length - position) {
                Failed = true;
                return false;
            }
            position += bytes;
            return true;
        }

        private static readonly UTF8Encoding Strict = new UTF8Encoding(false, true);
    }
}
