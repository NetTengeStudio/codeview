using System;

namespace ScpItdMod.Weather {
    public static class SnowFlake {
        public const float PixelCoverage = 1.6f, HumanMetres = 1.8f;
        public static float MinimumPixels(byte layer) => layer == 0 ? 2.2f : layer == 1 ? 1.6f : 1.1f;
        public const float MaximumAspect = 3f;
        public static float Streak(float motion, float drawn) => Math.Clamp(motion, drawn, drawn * MaximumAspect);
        public static void Footprint(float size, float pixelMetres, byte layer, float alpha, out float drawn, out float drawnAlpha) {
            float minimum = MinimumPixels(layer) * pixelMetres;
            drawn = MathF.Max(size, minimum);
            drawnAlpha = alpha * Math.Clamp(size / drawn, .35f, 1f);
        }
        public static float ThicknessMetres(float drawn) => drawn * PixelCoverage;
    }
}
