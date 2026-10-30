using System;
using ScpItdMod.Core;
using ScpItdMod.Weather.Material;

namespace ScpItdMod.Weather {
    public struct SnowBloodDrop { public Vec2 Position, Previous, Velocity; public float Age; }
    public enum SnowBloodContact : byte { Airborne, Hit, Ended }
    public static class SnowBlood {
        public const int Capacity = 256;
        public const float Lifetime = 5, Radius = .0125f, StepSeconds = 1f / 60;
        public static SnowBloodContact Advance(SnowPack snow, ref SnowBloodDrop drop, float dt) {
            if (!float.IsFinite(dt) || dt < 0) throw new ArgumentOutOfRangeException(nameof(dt));
            Vec2 from = drop.Position; drop.Previous = from;
            drop.Position += drop.Velocity * dt - new Vec2(0, .5f * SnowPhysics.Gravity * dt * dt);
            drop.Velocity -= new Vec2(0, SnowPhysics.Gravity * dt); drop.Age += dt;
            if (Intersect(snow, from, drop.Position, out Vec2 hit, out _)) { drop.Position = hit; return SnowBloodContact.Hit; }
            return drop.Age >= Lifetime || !snow.Grid.Geometry.SegmentClear(from, drop.Position) ? SnowBloodContact.Ended : SnowBloodContact.Airborne;
        }
        // A falling drop meets the snow where its path first enters the particles of a surface.
        public static bool Intersect(SnowPack snow, Vec2 from, Vec2 to, out Vec2 hit, out int slot) {
            hit = default; slot = -1;
            Vec2 delta = to - from;
            if (delta.Y >= 0 || !float.IsFinite(delta.X) || !float.IsFinite(delta.Y)) return false;
            int steps = Math.Max(1, (int)MathF.Ceiling(delta.Length() / (SnowConstitutive.Spacing * .5f)));
            float before = 0;
            for (int k = 1; k <= steps; k++) {
                float after = k / (float)steps; int inside = snow.FindSurface(from + delta * after, 0);
                if (inside < 0) { before = after; continue; }
                for (int iteration = 0; iteration < 10; iteration++) {
                    float middle = (before + after) * .5f;
                    if (snow.FindSurface(from + delta * middle, 0) >= 0) after = middle; else before = middle;
                }
                Vec2 at = from + delta * after;
                if (!snow.Grid.Geometry.SegmentClear(from, at)) return false;
                hit = at; slot = inside; return true;
            }
            return false;
        }
        // The stain is carried by the particles of the top layer under the drop.
        public static void Deposit(SnowPack snow, Vec2 hit, float quantity) {
            if (!float.IsFinite(quantity) || quantity <= 0) return;
            int centre = snow.FindSurface(hit, snow.Grid.Cell); if (centre < 0) return;
            snow.Stain(hit, .08f, quantity);
        }
    }
}
