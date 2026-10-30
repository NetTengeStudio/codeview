using System;

namespace ScpItdMod.Cinematics {
    public static class CinematicTitleMath {
        public static bool Visible(double age, double duration) => age >= 0 && age < duration;
        public static float Ease(double age, double seconds) {
            float t = seconds > 0 ? Math.Clamp((float)(age / seconds), 0, 1) : 1;
            return t * t * t * (t * (t * 6 - 15) + 10);
        }
        public static float Opacity(double age, double duration, double fade) => Visible(age, duration) ? MathF.Min(Ease(age, fade), Ease(duration - age, fade)) : 0;
    }
}
