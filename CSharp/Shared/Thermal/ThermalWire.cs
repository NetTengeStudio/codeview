using System;
using ScpItdMod.Net;

namespace ScpItdMod.Thermal {
    public struct ThermalReading {
        public ushort Character;
        public uint Sequence;
        public float Core, Skin, Wetness, Air, Wind, Reserve, Frostbite, Dexterity, Gait, Pain, ColdShock, WetCold;
    }
    public static class ThermalWire {
        public const string Channel = "scpitd.thermal.v3";
        public const int PacketSize = 39;
        public static bool Newer(uint candidate, uint current) => unchecked((int)(candidate - current)) > 0;
        private static ushort Pack(float n, float offset, float scale) => (ushort)Math.Clamp((int)MathF.Round((n - offset) * scale), 0, ushort.MaxValue);
        public static ulong ContentKey(in ThermalReading r) {
            ulong hash = 14695981039346656037;
            hash = (hash ^ Pack(r.Core, 0, 100)) * 1099511628211; hash = (hash ^ Pack(r.Skin, -90, 100)) * 1099511628211;
            hash = (hash ^ Pack(r.Wetness, 0, 10000)) * 1099511628211; hash = (hash ^ Pack(r.Air, -90, 10)) * 1099511628211;
            hash = (hash ^ Pack(r.Wind, 0, 100)) * 1099511628211; hash = (hash ^ Pack(r.Reserve, 0, 10000)) * 1099511628211;
            hash = (hash ^ Pack(r.Frostbite, 0, 10000)) * 1099511628211;
            hash = (hash ^ Pack(r.Dexterity, 0, 10000)) * 1099511628211; hash = (hash ^ Pack(r.Gait, 0, 10000)) * 1099511628211;
            hash = (hash ^ Pack(r.Pain, 0, 10000)) * 1099511628211; hash = (hash ^ Pack(r.ColdShock, 0, 10000)) * 1099511628211;
            return (hash ^ Pack(r.WetCold, 0, 10000)) * 1099511628211;
        }
        public static void Write(WireWriter w, uint epoch, uint token, in ThermalReading r) {
            w.Clear(); w.U8(3); w.U32(epoch); w.U32(token); w.U16(r.Character); w.U32(r.Sequence);
            w.U16(Pack(r.Core, 0, 100)); w.U16(Pack(r.Skin, -90, 100)); w.U16(Pack(r.Wetness, 0, 10000));
            w.U16(Pack(r.Air, -90, 10)); w.U16(Pack(r.Wind, 0, 100)); w.U16(Pack(r.Reserve, 0, 10000)); w.U16(Pack(r.Frostbite, 0, 10000));
            w.U16(Pack(r.Dexterity, 0, 10000)); w.U16(Pack(r.Gait, 0, 10000)); w.U16(Pack(r.Pain, 0, 10000)); w.U16(Pack(r.ColdShock, 0, 10000)); w.U16(Pack(r.WetCold, 0, 10000));
        }
        public static bool Read(ReadOnlySpan<byte> bytes, uint epoch, uint token, ushort character, uint sequence, bool have, out ThermalReading result) {
            result = default;
            if (bytes.Length != PacketSize) return false;
            var r = new WireReader(bytes);
            if (r.U8() != 3 || r.U32() != epoch || r.U32() != token) return false;
            ThermalReading value = new ThermalReading { Character = r.U16(), Sequence = r.U32(), Core = r.U16() / 100f, Skin = r.U16() / 100f - 90,
                Wetness = r.U16() / 10000f, Air = r.U16() / 10f - 90, Wind = r.U16() / 100f, Reserve = r.U16() / 10000f, Frostbite = r.U16() / 10000f, Dexterity = r.U16() / 10000f, Gait = r.U16() / 10000f, Pain = r.U16() / 10000f, ColdShock = r.U16() / 10000f, WetCold = r.U16() / 10000f };
            if (r.Failed || !r.End || value.Character == 0 || value.Character != character || have && !Newer(value.Sequence, sequence) ||
                value.Core < 10 || value.Core > 45 || value.Skin < -90 || value.Skin > 60 || value.Air < ThermalLimits.MinimumAmbient || value.Air > ThermalLimits.MaximumAmbient || value.Wind > 200 || value.Wetness > 1 || value.Reserve > 1 || value.Frostbite > 1 || value.Dexterity > 1 || value.Gait > 1 || value.Pain > 1 || value.ColdShock > 1 || value.WetCold > 1) return false;
            result = value; return true;
        }
    }
}
