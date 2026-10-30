using System;

namespace ScpItdMod.Core {
    public static class Scalar {
        private const int Steps = 256;
        private const float StepsF = Steps;
        private const float DecayRange = 6.0f;

        private static readonly float[] decay = new float[Steps + 2];

        static Scalar() {
            for (int i = 0; i <= Steps + 1; i++) {
                double t = i / (double)Steps;
                if (t > 1.0) { t = 1.0; }
                decay[i] = (float)(1.0 - Math.Exp(-t * DecayRange));
            }
        }

        public static float Decay(float rate, float dt) {
            float x = rate * dt;
            if (x <= 0.0f) { return 0.0f; }
            if (x >= DecayRange) { return 1.0f; }
            float scaled = x * (1.0f / DecayRange) * StepsF;
            int index = (int)scaled;
            float frac = scaled - index;
            return decay[index] + (decay[index + 1] - decay[index]) * frac;
        }

        public static float Approach(float current, float target, float rate, float dt) =>
            current + (target - current) * Decay(rate, dt);

        public static float Smooth(float t) =>
            t <= 0.0f ? 0.0f : t >= 1.0f ? 1.0f : t * t * (3.0f - 2.0f * t);

        public static float Lerp(float from, float to, float t) => from + (to - from) * t;

        public static float Clamp01(float value) => value < 0.0f ? 0.0f : value > 1.0f ? 1.0f : value;

        public static int Round(float value) => (int)(value + (value < 0.0f ? -0.5f : 0.5f));

        public static byte ToByte(float unit) => (byte)(Clamp01(unit) * 255.0f + 0.5f);
    }
}
