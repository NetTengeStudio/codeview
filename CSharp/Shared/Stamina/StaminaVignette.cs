using System;
using ScpItdMod.Core;
using Microsoft.Xna.Framework;

namespace ScpItdMod.Stamina {
    public static class StaminaVignette {
        public static float Falloff(float dx, float dy) {
            float radius = (float)Math.Sqrt(dx * dx + dy * dy);
            float span = StaminaConfig.VignetteOuter - StaminaConfig.VignetteInner;
            if (span <= 0.0f) { return radius >= StaminaConfig.VignetteOuter ? 1.0f : 0.0f; }
            return Scalar.Smooth((radius - StaminaConfig.VignetteInner) / span);
        }

        public static void Bake(Span<Color> pixels, int size) {
            float step = 2.0f / size;
            for (int y = 0; y < size; y++) {
                float dy = (y + 0.5f) * step - 1.0f;
                int row = y * size;
                for (int x = 0; x < size; x++) {
                    float dx = (x + 0.5f) * step - 1.0f;
                    pixels[row + x] = new Color((byte)0, (byte)0, (byte)0, Scalar.ToByte(Falloff(dx, dy)));
                }
            }
        }
    }
}
