using System;
using ScpItdMod.Core;

namespace ScpItdMod.Weather {
    public readonly struct LightColor {
        public readonly float R, G, B;
        public LightColor(float r, float g, float b) { R = r; G = g; B = b; }
        public float Luma => .2126f * R + .7152f * G + .0722f * B;
        public static LightColor Lerp(LightColor a, LightColor b, float t) => new LightColor(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);
        public static LightColor operator *(LightColor c, float k) => new LightColor(c.R * k, c.G * k, c.B * k);
        public static LightColor operator +(LightColor a, LightColor b) => new LightColor(a.R + b.R, a.G + b.G, a.B + b.B);
    }

    public readonly struct SkyLightEnvironment {
        public readonly LightColor Ambient, Sunlight, Moonlight;
        public readonly float SunSample, MoonSample;
        public readonly Vec2 SunDirection, MoonDirection;
        public readonly Vec2 SunIncidence, MoonIncidence;

        public SkyLightEnvironment(LightColor ambient, LightColor sunlight, Vec2 sunDirection, LightColor moonlight, Vec2 moonDirection, float sunDepth = 0, float moonDepth = 0) {
            Ambient = ambient; Sunlight = sunlight; Moonlight = moonlight;
            SunDirection = Vec2.Normalize(sunDirection); MoonDirection = Vec2.Normalize(moonDirection);
            SunSample = Coordinate(sunDirection); MoonSample = Coordinate(moonDirection);
            SunIncidence = Incidence(sunDirection, sunDepth); MoonIncidence = Incidence(moonDirection, moonDepth);
        }
        private static Vec2 Incidence(Vec2 projected, float depth) {
            float planar = projected.Length(), length = MathF.Sqrt(planar * planar + depth * depth);
            return length > 1e-8f ? new Vec2(planar / length, depth / length) : default;
        }

        public static SkyLightEnvironment Diffuse(LightColor sky) => new SkyLightEnvironment(sky, default, Vec2.UnitY, default, Vec2.UnitY);
        private static float Coordinate(Vec2 direction) => Math.Clamp((MathF.Atan2(direction.X, direction.Y) / MathF.PI + .5f) * SkyVisibility.Directions - .5f, 0, SkyVisibility.Directions - 1);

        public static float DirectionalVisibility(uint mask, float coordinate) {
            int first = (int)coordinate, second = Math.Min(first + 1, SkyVisibility.Directions - 1);
            float a = (mask >> first) & 1, b = (mask >> second) & 1;
            return a + (b - a) * (coordinate - first);
        }

        public LightColor Evaluate(uint mask) => Ambient * (System.Numerics.BitOperations.PopCount(mask) * (1f / SkyVisibility.Directions))
            + Sunlight * DirectionalVisibility(mask, SunSample) + Moonlight * DirectionalVisibility(mask, MoonSample);
    }

    public static class WeatherLighting {
        public const float AdaptLux = 50f, FullLux = 110000f, MoonlessShare = .6f, SnowReflection = .08f, CloudBreathing = .08f;
        public const float NoonLightMap = .58f;
        public const float AdaptUpSeconds = 1.2f, AdaptDownSeconds = 3.5f;
        private static readonly LightColor Overcast = new LightColor(.78f, .84f, .93f), SkyBlue = new LightColor(.62f, .76f, 1f), Moon = new LightColor(.52f, .64f, .92f);
        private static readonly LightColor LowSun = new LightColor(1f, .68f, .46f), HighSun = new LightColor(1f, .96f, .9f);

        public static float Brightness(float lux) => lux <= 0 ? 0 : Math.Clamp(MathF.Log(1 + lux / AdaptLux) / MathF.Log(1 + FullLux / AdaptLux), 0, 1);

        public static float NightFloor(float moonlight) => moonlight * MoonlessShare * Moon.Luma;
        public static LightColor Outdoor(in WeatherState state, in SolarState sun, double seconds, uint seed, float moonlight) {
            float day = Brightness(state.SkyIlluminance);
            float altitude = Math.Clamp(sun.Altitude * (180f / MathF.PI), -6f, 30f), warmth = WeatherNoise.Smooth((altitude + 2f) / 22f);
            LightColor sunlight = LightColor.Lerp(LowSun, HighSun, warmth), clear = LightColor.Lerp(SkyBlue, sunlight, .55f * WeatherNoise.Smooth(altitude / 12f));
            LightColor tint = LightColor.Lerp(Overcast, clear, (1 - Math.Clamp(state.Cloud, 0, 1)) * WeatherNoise.Smooth((altitude + 6f) / 10f));
            float dusk = 1 - WeatherNoise.Smooth((altitude + 6f) / 10f);
            tint = LightColor.Lerp(tint, Moon, dusk);
            float breathing = 1 - CloudBreathing * Math.Clamp(state.Cloud, 0, 1) * (.5f + .5f * WeatherNoise.Value(seed ^ 0x51ed270bu, seconds / 7));
            float snow = 1 + SnowReflection * Math.Clamp(state.Snowfall, 0, 1.2f);
            float moon = MathF.Max(0, MathF.Sin(sun.MoonAltitude)) * sun.MoonPhase * (1 - .6f * Math.Clamp(state.Cloud, 0, 1));
            float night = moonlight * (MoonlessShare + (1 - MoonlessShare) * MathF.Min(1, 2 * moon));
            float level = night + (1 - night) * day * breathing * snow * NoonLightMap;
            LightColor lit = tint * MathF.Min(1, level);
            float aurora = AuroraLight * PolarSky.AuroraActivity(seed, seconds) * (1 - sun.Twilight) * (1 - .9f * Math.Clamp(state.Cloud, 0, 1));
            return new LightColor(lit.R + Aurora.R * aurora, lit.G + Aurora.G * aurora, lit.B + Aurora.B * aurora);
        }
        public const float AuroraLight = .12f;
        private static readonly LightColor Aurora = new LightColor(.25f, 1f, .55f);

        public static LightColor Skylight(LightColor outdoor, float exposure) => outdoor * Math.Clamp(exposure, 0, 1);

        public const float ClearAirOpticalDepth = .12f;
        public static SkyLightEnvironment Environment(in WeatherState state, in SolarState sun, LightColor outdoor) {
            float clear = 1 - Math.Clamp(state.Cloud, 0, 1), day = Brightness(state.SkyIlluminance);
            float elevation = MathF.Max(0, MathF.Sin(sun.Altitude));
            float solar = elevation > 0 ? day * clear * clear * MathF.Exp(-ClearAirOpticalDepth / elevation) : 0;
            float lunar = (1 - day) * (1 - MoonlessShare) * MathF.Min(1, 2 * MathF.Max(0, MathF.Sin(sun.MoonAltitude)) * sun.MoonPhase) * clear * clear;
            Vec2 moon = new Vec2(-MathF.Sin(sun.MoonAzimuth) * MathF.Cos(sun.MoonAltitude), MathF.Sin(sun.MoonAltitude));
            return new SkyLightEnvironment(outdoor * MathF.Max(0, 1 - solar - lunar), outdoor * solar, new Vec2(-sun.Sun.X, sun.Sun.Y), outdoor * lunar, moon,
                MathF.Cos(sun.Azimuth) * MathF.Cos(sun.Altitude), MathF.Cos(sun.MoonAzimuth) * MathF.Cos(sun.MoonAltitude));
        }

        public static LightColor Adapt(LightColor current, LightColor target, float dt) {
            float up = 1 - MathF.Exp(-dt / AdaptUpSeconds), down = 1 - MathF.Exp(-dt / AdaptDownSeconds);
            return new LightColor(Step(current.R, target.R, up, down), Step(current.G, target.G, up, down), Step(current.B, target.B, up, down));
        }
        private static float Step(float from, float to, float up, float down) => from + (to - from) * (to > from ? up : down);
    }
}
