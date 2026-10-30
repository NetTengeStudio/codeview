using System;
using ScpItdMod.Core;

namespace ScpItdMod.Weather {
    public readonly struct SolarState {
        public readonly float Altitude, Azimuth, MoonAltitude, MoonAzimuth, MoonPhase, Illuminance, Twilight;
        public readonly Vec2 Sun;
        public SolarState(float altitude, float azimuth, float moonAltitude, float moonAzimuth, float phase, float illuminance, float twilight) {
            Altitude = altitude; Azimuth = azimuth; MoonAltitude = moonAltitude; MoonAzimuth = moonAzimuth; MoonPhase = phase; Illuminance = illuminance; Twilight = twilight;
            Sun = new Vec2(MathF.Sin(azimuth) * MathF.Cos(altitude), MathF.Sin(altitude));
        }
    }
    public static class PolarSky {
        public const float ClearRange = 7f, WarmestHour = 14f, CloudDamping = .7f, MixingWind = 15f;
        public static float DiurnalSwing(double day, float cloud, float wind) {
            float range = ClearRange * (1 - CloudDamping * Math.Clamp(cloud, 0, 1)) * (1 - .5f * Math.Clamp(wind / MixingWind, 0, 1));
            return .5f * range * (float)Math.Cos(Math.Tau * (day - Math.Floor(day) - WarmestHour / 24));
        }
        public static float AuroraActivity(uint seed, double seconds) => .35f + .65f * (.5f + .5f * WeatherNoise.Value(seed, seconds / 900));
        public static float DiamondDust(in WeatherState state) {
            float clear = 1 - Math.Clamp(state.Cloud, 0, 1), cold = Math.Clamp((-state.Temperature - 25) / 20, 0, 1);
            return clear * clear * cold * Math.Clamp(state.Humidity / .3f, 0, 1) * (1 - Math.Clamp(state.Snowfall * 2, 0, 1)) * (1 - Math.Clamp(state.WindSpeed / 20, 0, 1));
        }
        public static SolarState Evaluate(float latitude, double day, float dayOfYear, float cloud) {
            const double radians = Math.PI / 180;
            double g = Math.Tau * (dayOfYear - 1) / 365;
            double declination = .006918 - .399912 * Math.Cos(g) + .070257 * Math.Sin(g) - .006758 * Math.Cos(2 * g) + .000907 * Math.Sin(2 * g) - .002697 * Math.Cos(3 * g) + .00148 * Math.Sin(3 * g);
            double hour = Math.Tau * (day - Math.Floor(day) - .5);
            Position(latitude * radians, declination, hour, out float altitude, out float azimuth);
            double lunarAngle = Math.Tau * (dayOfYear + day) / 27.321661;
            double lunarDeclination = Math.Asin(Math.Sin(23.4393 * radians) * Math.Sin(lunarAngle)) + 5.145 * radians * Math.Sin(lunarAngle * .944);
            double phaseAngle = Math.Tau * (dayOfYear + day) / 29.53059;
            Position(latitude * radians, lunarDeclination, hour - phaseAngle, out float moonAltitude, out float moonAzimuth);
            float phase = (float)((1 - Math.Cos(phaseAngle)) * .5);
            float twilight = WeatherNoise.Smooth((altitude * (180 / MathF.PI) + 18) / 18);
            float daylight = MathF.Max(0, MathF.Sin(altitude));
            float lux = (110000 * daylight + 450 * twilight * twilight + .25f * MathF.Max(0, MathF.Sin(moonAltitude)) * phase + .001f) * (1 - .8f * cloud);
            return new SolarState(altitude, azimuth, moonAltitude, moonAzimuth, phase, lux, twilight);
        }
        private static void Position(double latitude, double declination, double hour, out float altitude, out float azimuth) {
            altitude = (float)Math.Asin(Math.Clamp(Math.Sin(latitude) * Math.Sin(declination) + Math.Cos(latitude) * Math.Cos(declination) * Math.Cos(hour), -1, 1));
            azimuth = (float)(Math.Atan2(Math.Sin(hour), Math.Cos(hour) * Math.Sin(latitude) - Math.Tan(declination) * Math.Cos(latitude)) + Math.PI);
        }
    }
}
