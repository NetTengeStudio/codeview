using System;
using ScpItdMod.Net;

namespace ScpItdMod.Weather {
    public static class WeatherWire {
        public const string Channel = "scpitd.weather.v3";
        public const int MaximumPacket = 49152;
        public static void Write(WireWriter w, in WeatherControl c) {
            w.U32(c.Revision); w.U8((byte)c.Kind); Double(w, c.At); w.F32(c.A); w.F32(c.B); w.F32(c.Duration);
            WeatherState s = c.From;
            w.F32(s.Temperature); w.F32(s.WindSpeed); w.F32(s.WindDirection); w.F32(s.Gustiness); w.F32(s.Snowfall); w.F32(s.Drifting);
            w.F32(s.Visibility); w.F32(s.Cloud); w.F32(s.SkyIlluminance); w.F32(s.Humidity); w.F32(s.Frost); w.F32(s.Pressure);
            w.U8((byte)s.Regime); w.U8((byte)s.Next); Double(w, s.SegmentEnd);
        }
        public static bool Read(ref WireReader r, out WeatherControl c) {
            uint revision = r.U32(); var kind = (WeatherControlKind)r.U8(); double at = Double(ref r); float a = r.F32(), b = r.F32(), duration = r.F32();
            WeatherState s = new WeatherState { Temperature = r.F32(), WindSpeed = r.F32(), WindDirection = r.F32(), Gustiness = r.F32(), Snowfall = r.F32(), Drifting = r.F32(),
                Visibility = r.F32(), Cloud = r.F32(), SkyIlluminance = r.F32(), Humidity = r.F32(), Frost = r.F32(), Pressure = r.F32(), Regime = (WeatherRegime)r.U8(), Next = (WeatherRegime)r.U8(), SegmentEnd = Double(ref r) };
            c = new WeatherControl(revision, kind, at, a, b, duration, s);
            return !r.Failed && WeatherDirector.Valid(c);
        }
        public static void Double(WireWriter w, double value) => w.U64(BitConverter.DoubleToUInt64Bits(value));
        public static double Double(ref WireReader r) => BitConverter.UInt64BitsToDouble(r.U64());
    }
}
