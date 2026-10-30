using System;

namespace ScpItdMod.Hud {
    public struct TypePresentation {
        private double start;

        public bool Started { get; private set; }

        public void Present(double now) {
            if (Started) { return; }
            start = now;
            Started = true;
        }

        public float Age(double now) => Started ? (float)Math.Max(0.0, now - start) : 0.0f;
    }

    public static class TypeMotion {
        public const float EnterSeconds = 0.18f;
        public const float LeaveSeconds = 0.22f;
        public const float RevealLead = 0.06f;
        public const float PendingSeconds = 9.0f;

        public static float Ease(float t) {
            if (!(t > 0.0f)) { return 0.0f; }
            if (t >= 1.0f) { return 1.0f; }
            float v = t <= 0.5f ? t : 1.0f - t;
            float eased = v * v * v * (v * (v * 6.0f - 15.0f) + 10.0f);
            return t <= 0.5f ? eased : 1.0f - eased;
        }

        public static float ReadSeconds(TypeText text) => MathF.Min(6.6f, 2.2f + text.Count * 0.045f);
    }
}
