using System;
using ScpItdMod.Core;
using ScpItdMod.Net;

namespace ScpItdMod.Thermal {
    [Flags]
    public enum ThermalPixelFlags : byte { Inside = 1, Solid = 2, Water = 4, Source = 8, ClosedGate = 16 }
    public readonly struct ThermalPixel {
        public readonly ushort Air, Surface;
        public readonly ThermalPixelFlags Flags;
        public readonly byte Angle, Strength;
        public float AirTemperature => Air * .1f - 90;
        public float SurfaceTemperature => Surface * .1f - 90;
        public ThermalPixel(ushort air, ushort surface, ThermalPixelFlags flags, byte angle, byte strength) {
            Air = air; Surface = surface; Flags = flags; Angle = angle; Strength = strength;
        }
        public static ThermalPixel From(in ThermalSample sample, ThermalPixelFlags flags) {
            float length = sample.HeatFlow.Length();
            byte angle = length > 0 ? unchecked((byte)(int)MathF.Round(MathF.Atan2(sample.HeatFlow.Y, sample.HeatFlow.X) * (256 / (2 * MathF.PI)))) : (byte)0;
            byte strength = (byte)Math.Clamp((int)MathF.Round(MathF.Sqrt(length) * 4), 0, 255);
            return new ThermalPixel(Pack(sample.Air), Pack(sample.Surface), flags, angle, strength);
        }
        private static ushort Pack(float value) {
            if (!float.IsFinite(value) || value < -90 || value > 6463.4f) throw new ArgumentOutOfRangeException(nameof(value));
            return (ushort)Math.Clamp((int)MathF.Round((value + 90) * 10), 0, 65534);
        }
        public ThermalPixel With(ThermalPixelFlags flags) => new ThermalPixel(Air, Surface, Flags | flags, Angle, Strength);
    }
    public readonly struct ThermalTileHeader {
        public readonly uint Epoch, Token, Revision, Sequence;
        public readonly ushort Character, X, Y;
        public readonly Vec2 Origin;
        public readonly float Cell;
        public ThermalTileHeader(uint epoch, uint token, ushort character, uint revision, uint sequence, Vec2 origin, float cell, ushort x, ushort y) {
            Epoch = epoch; Token = token; Character = character; Revision = revision; Sequence = sequence; Origin = origin; Cell = cell; X = x; Y = y;
        }
    }
    public static class ThermalFieldWire {
        public const string Channel = "scpitd.thermal.field.v1";
        public const int Side = 8, Pixels = Side * Side, PacketSize = 35 + Pixels * 7;
        public static ulong Write(WireWriter writer, in ThermalTileHeader header, ReadOnlySpan<ThermalPixel> pixels) {
            if (pixels.Length != Pixels) throw new ArgumentException("Expected one thermal tile", nameof(pixels));
            writer.Clear(); writer.U8(1); writer.U32(header.Epoch); writer.U32(header.Token); writer.U16(header.Character);
            writer.U32(header.Revision); writer.U32(header.Sequence); writer.F32(header.Origin.X); writer.F32(header.Origin.Y); writer.F32(header.Cell); writer.U16(header.X); writer.U16(header.Y);
            ulong key = 14695981039346656037;
            for (int i = 0; i < pixels.Length; i++) {
                ThermalPixel pixel = pixels[i];
                writer.U16(pixel.Air); writer.U16(pixel.Surface); writer.U8((byte)pixel.Flags); writer.U8(pixel.Angle); writer.U8(pixel.Strength);
                key = (key ^ pixel.Air) * 1099511628211; key = (key ^ pixel.Surface) * 1099511628211;
                key = (key ^ (byte)pixel.Flags) * 1099511628211; key = (key ^ pixel.Angle) * 1099511628211; key = (key ^ pixel.Strength) * 1099511628211;
            }
            key = (key ^ header.Revision) * 1099511628211; key = (key ^ header.X) * 1099511628211; key = (key ^ header.Y) * 1099511628211;
            return key;
        }
        public static bool Read(ReadOnlySpan<byte> bytes, Span<ThermalPixel> pixels, out ThermalTileHeader header) {
            header = default;
            if (bytes.Length != PacketSize || pixels.Length != Pixels) return false;
            var reader = new WireReader(bytes);
            if (reader.U8() != 1) return false;
            uint epoch = reader.U32(), token = reader.U32(); ushort character = reader.U16();
            uint revision = reader.U32(), sequence = reader.U32();
            Vec2 origin = new Vec2(reader.F32(), reader.F32()); float cell = reader.F32(); ushort x = reader.U16(), y = reader.U16();
            if (character == 0 || revision == 0 || !float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || MathF.Abs(origin.X) > 1e7f || MathF.Abs(origin.Y) > 1e7f || !float.IsFinite(cell) || cell < .01f || cell > 100) return false;
            for (int i = 0; i < Pixels; i++) {
                ushort air = reader.U16(), surface = reader.U16(); byte flags = reader.U8(), angle = reader.U8(), strength = reader.U8();
                if (air == ushort.MaxValue || surface == ushort.MaxValue || (flags & ~31) != 0) return false;
                pixels[i] = new ThermalPixel(air, surface, (ThermalPixelFlags)flags, angle, strength);
            }
            if (reader.Failed || !reader.End) return false;
            header = new ThermalTileHeader(epoch, token, character, revision, sequence, origin, cell, x, y);
            return true;
        }
    }
    public sealed class ThermalFieldReplica {
        public const int Slots = 16;
        public const double FreshSeconds = 3;
        private readonly ThermalPixel[] pixels = new ThermalPixel[Slots * ThermalFieldWire.Pixels];
        private readonly ThermalTileHeader[] headers = new ThermalTileHeader[Slots];
        private readonly double[] received = new double[Slots];
        private readonly bool[] valid = new bool[Slots];
        private uint revision;
        private Vec2 origin;
        private float cell;
        public void Reset() { Array.Clear(valid); revision = 0; cell = 0; }
        public bool Apply(ReadOnlySpan<byte> bytes, uint epoch, uint token, ushort character, double time) {
            if (!double.IsFinite(time)) return false;
            Span<ThermalPixel> decoded = stackalloc ThermalPixel[ThermalFieldWire.Pixels];
            if (!ThermalFieldWire.Read(bytes, decoded, out ThermalTileHeader header) || header.Epoch != epoch || header.Token != token || header.Character != character) return false;
            if (revision != 0 && header.Revision != revision && !ThermalWire.Newer(header.Revision, revision)) return false;
            int slot = (header.X & 3) | ((header.Y & 3) << 2);
            if (header.Revision == revision) {
                if (header.Origin.X != origin.X || header.Origin.Y != origin.Y || header.Cell != cell || valid[slot] && !ThermalWire.Newer(header.Sequence, headers[slot].Sequence)) return false;
            } else { Array.Clear(valid); revision = header.Revision; origin = header.Origin; cell = header.Cell; }
            decoded.CopyTo(pixels.AsSpan(slot * ThermalFieldWire.Pixels, ThermalFieldWire.Pixels));
            headers[slot] = header; received[slot] = time; valid[slot] = true;
            return true;
        }
        public bool TryTile(int slot, double time, out ThermalTileHeader header, out ReadOnlySpan<ThermalPixel> tile) {
            header = default; tile = default;
            if ((uint)slot >= Slots || !valid[slot] || !double.IsFinite(time) || time < received[slot] || time - received[slot] >= FreshSeconds) return false;
            header = headers[slot]; tile = pixels.AsSpan(slot * ThermalFieldWire.Pixels, ThermalFieldWire.Pixels); return true;
        }
        public bool Sample(Vec2 point, double time, out ThermalPixel pixel) {
            pixel = default; if (cell <= 0) return false;
            int x = (int)MathF.Floor((point.X - origin.X) / cell), y = (int)MathF.Floor((point.Y - origin.Y) / cell);
            if (x < 0 || y < 0 || x / 8 > ushort.MaxValue || y / 8 > ushort.MaxValue) return false;
            int tileX = x / 8, tileY = y / 8, slot = (tileX & 3) | ((tileY & 3) << 2);
            if (!TryTile(slot, time, out ThermalTileHeader header, out ReadOnlySpan<ThermalPixel> tile) || header.X != tileX || header.Y != tileY) return false;
            pixel = tile[(y & 7) * 8 + (x & 7)]; return true;
        }
    }
}
