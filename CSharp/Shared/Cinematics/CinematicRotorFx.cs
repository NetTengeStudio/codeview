using System;
using ScpItdMod.Core;

namespace ScpItdMod.Cinematics {
    public static class CinematicRotorFx {
        public const float TipTrailsPerSecond = 46f, RollsPerSecond = 120f, MinimumCondensation = .3f, HelixHertz = 1.7f, HelixAmplitude = .3f, RollRise = .55f, RollBack = .35f, FrontShare = .2f;

        public static float Visibility(float condensation) => Math.Clamp(MinimumCondensation + (1f - MinimumCondensation) * condensation, 0f, 1f);

        public static float TipRate(float rotorShare, float intensity, float visibility) => Math.Clamp(rotorShare, 0f, 1f) * MathF.Max(0f, intensity) * visibility * TipTrailsPerSecond;

        public static Vec2 TipLaunch(float induced, float seconds, float side, float rotorShare) {
            float sink = -MathF.Max(.5f, induced * 1.2f) * (.6f + .4f * Math.Clamp(rotorShare, 0f, 1f));
            float phase = 2f * MathF.PI * HelixHertz * seconds;
            return new Vec2(side * .35f * MathF.Abs(sink) + HelixAmplitude * 2f * MathF.PI * HelixHertz * MathF.Cos(phase + (side < 0 ? MathF.PI : 0)), sink);
        }

        public static int FrontIndex(ReadOnlySpan<float> strengths, bool right) {
            float peak = 0;
            for (int i = 0; i < strengths.Length; i++) peak = MathF.Max(peak, strengths[i]);
            if (peak <= 0) return -1;
            if (right) { for (int i = strengths.Length - 1; i >= 0; i--) if (strengths[i] >= peak * FrontShare) return i; }
            else { for (int i = 0; i < strengths.Length; i++) if (strengths[i] >= peak * FrontShare) return i; }
            return -1;
        }

        public static Vec2 RollLaunch(in CinematicOutwash flow, float scatter) {
            float speed = MathF.Abs(flow.Speed);
            return new Vec2(-MathF.Sign(flow.Speed) * speed * RollBack * (.4f + .6f * scatter), speed * RollRise * (.5f + .5f * scatter));
        }
    }
}
