using System;

namespace ScpItdMod.Core {
    public static class IkBlend {
        public static Vec2 Local(Vec2 world, float rotation, float direction) {
            float c = MathF.Cos(rotation), s = MathF.Sin(rotation);
            return new Vec2((world.X * c + world.Y * s) * direction, world.Y * c - world.X * s);
        }

        public static Vec2 Target(Vec2 natural, Vec2 local, Vec2 shoulder, float rotation, float direction, float reach, float weight) {
            float lengthSquared = local.LengthSquared();
            if (lengthSquared > reach * reach && lengthSquared > 0) { local *= reach / MathF.Sqrt(lengthSquared); }
            float c = MathF.Cos(rotation), s = MathF.Sin(rotation);
            local = new Vec2(local.X * direction, local.Y);
            Vec2 target = shoulder + new Vec2(local.X * c - local.Y * s, local.X * s + local.Y * c);
            return Vec2.Lerp(natural, target, Math.Clamp(weight, 0, 1));
        }

        public static float Ease(float t) {
            if (t <= 0.0f) { return 0.0f; }
            if (t >= 1.0f) { return 1.0f; }
            return t * t * t * (t * (t * 6.0f - 15.0f) + 10.0f);
        }

        public static float Weight(float t, float fadeIn, float fadeAt, float fadeOut) {
            float rise = fadeIn <= 0.0f ? 1.0f : Ease(t / fadeIn);
            float fall = fadeOut <= 0.0f ? (t < fadeAt ? 1.0f : 0.0f) : 1.0f - Ease((t - fadeAt) / fadeOut);
            return MathF.Min(rise, fall);
        }

        public static Vec2 Sway(float t, uint seed, float arm) {
            float p = (seed & 1023u) * 0.0061359f;
            float q = (seed >> 10 & 1023u) * 0.0061359f;
            float x = MathF.Sin(t * 1.9f + p) + 0.45f * MathF.Sin(t * 3.7f + q);
            float y = MathF.Sin(t * 1.3f + q) + 0.4f * MathF.Sin(t * 2.9f + p);
            return new Vec2(x, y) * (arm * 0.011f);
        }
    }
}
