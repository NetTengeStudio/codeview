using System;
using ScpItdMod.Core;

namespace ScpItdMod.Cinematics {
    public static class CinematicWashMath {
        public const int Samples = 9;
        public const float PeakParticlesPerSecond = 1600f, FrictionVelocityRatio = .06f, SaltationRise = 4f, BaselineShare = .4f;
        public static float Haze(in CinematicOutwash flow) => Math.Clamp(flow.Ratio, 0f, 1f);
        public static float Lift(in CinematicOutwash flow) => Math.Clamp(flow.Ratio - 1f, 0f, 1f);
        public static float Strength(in CinematicOutwash flow) => Haze(flow) * (BaselineShare + (1f - BaselineShare) * Lift(flow));
        public static float Emission(float meanStrength, float intensity) => Math.Clamp(meanStrength, 0f, 1f) * MathF.Max(0f, intensity) * PeakParticlesPerSecond;
        public static Vec2 Launch(in CinematicOutwash flow, float along, float scatter) {
            float strength = Strength(flow);
            return new Vec2(flow.Speed * (.55f + .9f * along), MathF.Abs(flow.Speed) * FrictionVelocityRatio * SaltationRise * (.35f + 1.3f * scatter) * (.5f + .5f * strength));
        }
        public static int Pick(ReadOnlySpan<float> strengths, float total, float u) {
            float target = u * total, sum = 0;
            for (int i = 0; i < strengths.Length; i++) { sum += strengths[i]; if (target <= sum) return i; }
            return strengths.Length - 1;
        }
    }
}
