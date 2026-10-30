using System;
using ScpItdMod.Net;

namespace ScpItdMod.Meteo {
    [Flags]
    public enum MeteoFlags : byte {
        None = 0,
        Iced = 1,
        WindStalled = 2,
        HumidityInvalid = 4,
        Manual = 8,
        Indoor = 16
    }

    public readonly struct MeteoSample {
        public readonly double Time;
        public readonly ushort Station;
        public readonly float Temperature, Pressure, Wind, Direction, Humidity, Visibility, Snow, Ice;
        public readonly MeteoFlags Flags;

        public MeteoSample(double time, ushort station, float temperature, float pressure, float wind, float direction, float humidity, float visibility, float snow, float ice, MeteoFlags flags) {
            Time = time; Station = station; Temperature = temperature; Pressure = pressure; Wind = wind; Direction = direction;
            Humidity = humidity; Visibility = visibility; Snow = snow; Ice = ice; Flags = flags;
        }

        public bool Manual => (Flags & MeteoFlags.Manual) != 0;
        public bool WindValid => (Flags & (MeteoFlags.WindStalled | MeteoFlags.Manual)) == 0;
        public bool HumidityValid => (Flags & MeteoFlags.HumidityInvalid) == 0;
        public float DewPoint => HumidityValid ? MeteoPhysics.FrostPoint(Temperature, Humidity) : float.NaN;
        public float WindChill => MeteoPhysics.WindChill(Temperature, Wind);
    }

    public static class MeteoCodec {
        public const int Bytes = 4 + 2 + 2 + 2 + 2 + 2 + 1 + 2 + 2 + 1 + 1;
        public const double TimeStep = 0.1;

        public static void Write(WireWriter w, in MeteoSample s) {
            w.U32((uint)Math.Clamp(Math.Round(s.Time / TimeStep), 0, uint.MaxValue));
            w.U16(s.Station);
            w.U16((ushort)Math.Clamp((int)MathF.Round((s.Temperature + 100) * 100), 0, 65535));
            w.U16((ushort)Math.Clamp((int)MathF.Round(s.Pressure * 50), 0, 65535));
            w.U16((ushort)Math.Clamp((int)MathF.Round(s.Wind * 100), 0, 65535));
            w.U16((ushort)Math.Clamp((int)MathF.Round(Wrap(s.Direction) / MathF.Tau * 65535), 0, 65535));
            w.U8((byte)Math.Clamp((int)MathF.Round(s.Humidity * 200), 0, 200));
            w.U16(VisibilityCode(s.Visibility));
            w.U16((ushort)Math.Clamp((int)MathF.Round(s.Snow * 1000), 0, 65535));
            w.U8((byte)Math.Clamp((int)MathF.Round(s.Ice * 200), 0, 200));
            w.U8((byte)s.Flags);
        }

        public static bool Read(ref WireReader r, out MeteoSample s) {
            double time = r.U32() * TimeStep; ushort station = r.U16();
            float t = r.U16() / 100f - 100, p = r.U16() / 50f, wind = r.U16() / 100f, dir = r.U16() / 65535f * MathF.Tau;
            float humidity = r.U8() / 200f, visibility = VisibilityValue(r.U16()), snow = r.U16() / 1000f, ice = r.U8() / 200f; var flags = (MeteoFlags)r.U8();
            s = new MeteoSample(time, station, t, p, wind, dir, humidity, visibility, snow, ice, flags);
            return !r.Failed && humidity <= 1 && ice <= 1 && ((byte)flags & ~31) == 0;
        }

        private static float Wrap(float a) { a %= MathF.Tau; return a < 0 ? a + MathF.Tau : a; }

        public static ushort VisibilityCode(float metres) => (ushort)Math.Clamp((int)MathF.Round(MathF.Log2(MathF.Max(.25f, metres) / .25f) * 2048), 0, 65535);
        public static float VisibilityValue(ushort code) => .25f * MathF.Pow(2, code / 2048f);
    }
}
