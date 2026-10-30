using System;

namespace ScpItdMod.Weather {
    public static class PolarExposure {
        public static float WindChill(float temperature, float windMetresPerSecond) {
            float kph = MathF.Max(0, windMetresPerSecond) * 3.6f;
            if (temperature > 0 || kph < 4.8f) return temperature;
            float v = MathF.Pow(kph, .16f);
            return 13.12f + .6215f * temperature - 11.37f * v + .3965f * temperature * v;
        }
        public const float LegLengthPerDeepSnow = 1.5f;
        public static float SnowWading(float sinkage, float deepSnow) {
            if (!float.IsFinite(sinkage) || sinkage <= 0) return 1;
            float immersion = Math.Min(1.4f, sinkage / (deepSnow * LegLengthPerDeepSnow));
            return 1 / (1 + immersion * (1 + immersion));
        }
        public static bool FlightAllowed(in WeatherState state, WeatherConfig config) => state.WindSpeed < config.StormWind && state.Visibility >= config.FlightVisibility && state.Regime != WeatherRegime.Storm;
    }
}
