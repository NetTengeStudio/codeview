using System;
using ScpItdMod.Weather;

namespace ScpItdMod.Meteo {
    public static class MeteoPhysics {
        private const float IceA = 22.46f, IceB = 272.62f;
        private static readonly float[] BeaufortLimits = { .3f, 1.6f, 3.4f, 5.5f, 8f, 10.8f, 13.9f, 17.2f, 20.8f, 24.5f, 28.5f, 32.7f };
        private static readonly string[] BeaufortNames = {
            "штиль", "тихий", "лёгкий", "слабый", "умеренный", "свежий", "сильный", "крепкий", "очень крепкий", "шторм", "сильный шторм", "жестокий шторм", "ураган"
        };
        private static readonly float[] FrostWindChill = { -28, -40, -48, -55, -65 };
        private static readonly float[] FrostMinutes = { 30, 10, 5, 2, 1 };

        public static float SaturationOverIce(float celsius) => 6.112f * MathF.Exp(IceA * celsius / (IceB + celsius));

        public static float FrostPoint(float celsius, float relativeHumidity) {
            float rh = Math.Clamp(relativeHumidity, .001f, 1);
            float gamma = MathF.Log(rh) + IceA * celsius / (IceB + celsius);
            return IceB * gamma / (IceA - gamma);
        }

        public static float VapourPressure(float celsius, float relativeHumidity) => SaturationOverIce(celsius) * Math.Clamp(relativeHumidity, 0, 1);

        public static float WindChill(float celsius, float metresPerSecond) => PolarExposure.WindChill(celsius, metresPerSecond);

        public static float Density(float hectopascal, float celsius) => hectopascal * 100 / (287.05f * (celsius + 273.15f));

        public static int Beaufort(float metresPerSecond) {
            for (int i = 0; i < BeaufortLimits.Length; i++) if (metresPerSecond < BeaufortLimits[i]) return i;
            return BeaufortLimits.Length;
        }

        public static string BeaufortName(float metresPerSecond) => BeaufortNames[Beaufort(metresPerSecond)];

        public static float FrostbiteMinutes(float apparentCelsius) {
            if (apparentCelsius >= FrostWindChill[0]) return float.PositiveInfinity;
            for (int i = 1; i < FrostWindChill.Length; i++) {
                if (apparentCelsius > FrostWindChill[i]) {
                    float t = (apparentCelsius - FrostWindChill[i - 1]) / (FrostWindChill[i] - FrostWindChill[i - 1]);
                    return MathF.Exp(MathF.Log(FrostMinutes[i - 1]) + (MathF.Log(FrostMinutes[i]) - MathF.Log(FrostMinutes[i - 1])) * t);
                }
            }
            return FrostMinutes[^1];
        }

        public static string Compass(float radians) {
            string[] names = { "С", "ССВ", "СВ", "ВСВ", "В", "ВЮВ", "ЮВ", "ЮЮВ", "Ю", "ЮЮЗ", "ЮЗ", "ЗЮЗ", "З", "ЗСЗ", "СЗ", "ССЗ" };
            float a = radians % MathF.Tau; if (a < 0) a += MathF.Tau;
            return names[(int)MathF.Round(a / (MathF.Tau / 16)) % 16];
        }

        public static float VisibilityStep(float metres) {
            if (metres < 1000) return metres < 50 ? MathF.Max(1, MathF.Round(metres)) : MathF.Round(metres / 50) * 50;
            if (metres < 10000) return MathF.Round(metres / 1000) * 1000;
            return MathF.Round(metres / 5000) * 5000;
        }

        public static float Quantize(float value, float step) => step <= 0 ? value : MathF.Round(value / step) * step;
    }
}
