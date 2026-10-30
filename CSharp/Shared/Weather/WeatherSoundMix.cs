using System;

namespace ScpItdMod.Weather {
    // Loudness of the weather layers as a function of the weather. Perceived loudness grows slowly with wind speed, so a linear or
    // squared gain makes ordinary wind inaudible; a quiet bed keeps the world from going dead even in calm air.
    public static class WeatherSoundMix {
        public const float StormWind = 27f, WindBed = .14f, WindExponent = .6f, GustExponent = .8f;

        public static float Wind(float windSpeed, float gain, float transmission) =>
            gain * transmission * (WindBed + (1 - WindBed) * MathF.Pow(Math.Clamp(windSpeed / StormWind, 0, 1), WindExponent));

        public static float Gust(float windSpeed, float gustiness, float gustPhase, float gain, float transmission) =>
            gain * transmission * MathF.Pow(Math.Clamp(windSpeed / StormWind, 0, 1), GustExponent) * Math.Clamp(gustiness * 2.2f, 0, 1) * (.3f + .7f * Math.Clamp(gustPhase, 0, 1));
    }
}
