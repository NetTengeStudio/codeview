using System;
using ScpItdMod.Net;

namespace ScpItdMod.Bath {
    public readonly struct BathSnapshot {
        public readonly ushort Item;
        public readonly float Litres, Celsius, Snow;
        public readonly bool Plugged, Heating;
        public BathSnapshot(ushort item, float litres, float celsius, float snow, bool plugged, bool heating) {
            Item = item; Litres = litres; Celsius = celsius; Snow = snow; Plugged = plugged; Heating = heating;
        }
    }

    // Снимок воды купели от сервера: частота 5 Гц, без надёжной доставки — следующий снимок заменит потерянный.
    public static class BathWire {
        public const string Channel = "scpitd.bath";
        public const int Bytes = 1 + 2 + 4 + 4 + 4 + 1;
        private const byte Version = 2;

        public static void Write(WireWriter w, in BathSnapshot s) {
            w.Clear(); w.U8(Version); w.U16(s.Item); w.F32(s.Litres); w.F32(s.Celsius); w.F32(s.Snow);
            w.U8((byte)((s.Plugged ? 1 : 0) | (s.Heating ? 2 : 0)));
        }

        public static bool Read(ReadOnlySpan<byte> bytes, out BathSnapshot s) {
            s = default;
            if (bytes.Length != Bytes) { return false; }
            var r = new WireReader(bytes);
            if (r.U8() != Version) { return false; }
            ushort item = r.U16(); float litres = r.F32(), celsius = r.F32(), snow = r.F32(); byte flags = r.U8();
            if (r.Failed || !r.End || item == 0 || flags > 3) { return false; }
            if (!float.IsFinite(litres) || !float.IsFinite(celsius) || !float.IsFinite(snow) || litres < 0 || snow < 0 || celsius < -50 || celsius > 150) { return false; }
            s = new BathSnapshot(item, litres, celsius, snow, (flags & 1) != 0, (flags & 2) != 0);
            return true;
        }
    }
}
