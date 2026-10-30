using System;

namespace ScpItdMod.Weather {
    public readonly struct Turbulence {
        public readonly float Wind, Direction, Snowfall, Visibility, Cloud;
        public Turbulence(float wind, float direction, float snowfall, float visibility, float cloud) { Wind = wind; Direction = direction; Snowfall = snowfall; Visibility = visibility; Cloud = cloud; }
    }

    // The fast part of the weather. Real wind is not a slow wave with noise on top: its energy falls off with frequency roughly as
    // f^-5/3 (the von Karman spectrum), so slow swells carry most of it and short gusts ride on them, and strong gusts come in squalls
    // that bring snow and cut the visibility together. Deterministic in (seed, time) so every peer sees the same air.
    public static class WeatherTurbulence {
        private static readonly float[] Periods = { 150f, 52f, 17f, 5.6f, 1.9f };
        private static readonly uint[] Salts = { 0xa7264f31u, 0x31f264a7u, 0x5c1d3b97u, 0x9e3779b9u, 0x2545f491u };
        private static readonly float[] Weights = Weigh();
        private static float[] Weigh() {
            var w = new float[Periods.Length]; float sum = 0;
            for (int i = 0; i < w.Length; i++) { w[i] = MathF.Pow(Periods[i], 1f / 3f); sum += w[i]; }
            for (int i = 0; i < w.Length; i++) w[i] /= sum;
            return w;
        }

        public static float Swell(uint seed, double seconds) {
            float sum = 0;
            for (int i = 0; i < Periods.Length; i++) sum += Weights[i] * WeatherNoise.Value(seed ^ Salts[i], seconds / Periods[i]);
            return sum;
        }

        public static float Squall(uint seed, double seconds) {
            float envelope = WeatherNoise.Smooth(.5f + .5f * WeatherNoise.Value(seed ^ 0x6d2b79f5u, seconds / 95));
            return envelope * envelope;
        }

        public static Turbulence Sample(uint seed, double seconds, float gustiness) {
            float swell = Swell(seed, seconds), squall = Squall(seed, seconds), amplitude = Math.Clamp(gustiness, 0, 1);
            float wind = Math.Clamp(1 + amplitude * 2.2f * swell * (.55f + .9f * squall) + amplitude * .5f * squall, .15f, 2.4f);
            float direction = (WeatherNoise.Value(seed ^ 0x672419fbu, seconds / 23) * .2f + swell * .09f) * amplitude;
            float cells = WeatherNoise.Value(seed ^ 0x2f0b4a27u, seconds / 37);
            float snowfall = Math.Clamp(1 + .4f * cells + .55f * squall, .2f, 1.8f);
            float visibility = Math.Clamp(1 / (1 + .6f * (snowfall - 1)), .45f, 1.5f);
            float cloud = .06f * cells + .05f * squall;
            return new Turbulence(wind, direction, snowfall, visibility, cloud);
        }
    }
}
