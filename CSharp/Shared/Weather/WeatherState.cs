using System;
using ScpItdMod.Core;

namespace ScpItdMod.Weather {
    public enum WeatherRegime : byte { Clear, Snowfall, Drifting, Blizzard, Storm, Recovery }
    public enum PolarSeason : byte { PolarDay, PolarNight, Equinox }
    public enum WeatherControlKind : byte { Regime, Wind, Temperature, Time, Season, TimeScale }

    public struct WeatherState {
        public float Temperature, WindSpeed, WindDirection, Gustiness, Snowfall, Drifting, Visibility;
        public float Cloud, SkyIlluminance, Humidity, Frost, Pressure, Warning;
        public Vec2 Wind;
        public WeatherRegime Regime, Next;
        public double SegmentEnd;
        public float Extinction => 3.912023f / MathF.Max(.25f, Visibility);
    }

    public readonly struct WeatherControl {
        public readonly uint Revision;
        public readonly WeatherControlKind Kind;
        public readonly double At;
        public readonly float A, B, Duration;
        public readonly WeatherState From;
        public WeatherControl(uint revision, WeatherControlKind kind, double at, float a, float b, float duration, in WeatherState from) {
            Revision = revision; Kind = kind; At = at; A = a; B = b; Duration = duration; From = from;
        }
    }

    public static class WeatherNoise {
        public static uint Hash(uint x) { x ^= x >> 16; x *= 0x7feb352du; x ^= x >> 15; x *= 0x846ca68bu; return x ^ (x >> 16); }
        public static uint Seed(string value) { uint h = 2166136261; if (value != null) for (int i = 0; i < value.Length; i++) h = (h ^ value[i]) * 16777619; return h; }
        public static float Unit(uint x) => (Hash(x) >> 8) * (1f / 16777216f);
        public static float Smooth(float x) { x = Math.Clamp(x, 0, 1); return x * x * x * (x * (x * 6 - 15) + 10); }
        public static float Mix(float a, float b, float t) => a + (b - a) * t;
        public static float Angle(float a, float b, float t) => a + MathF.IEEERemainder(b - a, MathF.Tau) * t;
        public static float Value(uint seed, double time) {
            long n = (long)Math.Floor(time);
            return Mix(Unit(seed ^ unchecked((uint)n)), Unit(seed ^ unchecked((uint)(n + 1))), Smooth((float)(time - n))) * 2 - 1;
        }
        public static Vec2 Curl(uint seed, float x, float y, double time) {
            x += (float)(time * .11);
            y -= (float)(time * .07);
            int ix = (int)MathF.Floor(x), iy = (int)MathF.Floor(y);
            float u = x - ix, v = y - iy, su = Smooth(u), sv = Smooth(v);
            float du = 30 * u * u * (u - 1) * (u - 1), dv = 30 * v * v * (v - 1) * (v - 1);
            float a = Lattice(seed, ix, iy), b = Lattice(seed, ix + 1, iy), c = Lattice(seed, ix, iy + 1), d = Lattice(seed, ix + 1, iy + 1);
            return new Vec2(dv * Mix(c - a, d - b, su), -du * Mix(b - a, d - c, sv));
        }
        private static float Lattice(uint seed, int x, int y) => Unit(seed ^ unchecked((uint)x * 0x9e3779b9u) ^ unchecked((uint)y * 0x85ebca6bu));
        public static WeatherState Blend(in WeatherState a, in WeatherState b, float t) => new WeatherState {
            Temperature = Mix(a.Temperature, b.Temperature, t), WindSpeed = Mix(a.WindSpeed, b.WindSpeed, t),
            WindDirection = Angle(a.WindDirection, b.WindDirection, t), Gustiness = Mix(a.Gustiness, b.Gustiness, t),
            Snowfall = Mix(a.Snowfall, b.Snowfall, t), Drifting = Mix(a.Drifting, b.Drifting, t),
            Visibility = 3.912023f / Mix(a.Extinction, b.Extinction, t), Cloud = Mix(a.Cloud, b.Cloud, t),
            Humidity = Mix(a.Humidity, b.Humidity, t), Frost = Mix(a.Frost, b.Frost, t), Pressure = Mix(a.Pressure, b.Pressure, t),
            Regime = b.Regime, Next = b.Next, SegmentEnd = b.SegmentEnd
        };
    }
}
