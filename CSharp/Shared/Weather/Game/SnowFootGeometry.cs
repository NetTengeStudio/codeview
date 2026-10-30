using System;
using FarseerPhysics;
using FarseerPhysics.Collision;
using FarseerPhysics.Dynamics;
using ScpItdMod.Core;
using Vector2 = Microsoft.Xna.Framework.Vector2;

namespace ScpItdMod.Weather {
    internal static class SnowFootGeometry {
        internal static bool Read(Body body, Vector2 offset, float units, out Vec2 sole, out float length, out float height) {
            sole = default; length = height = 0;
            if (body == null || !(units > 0)) return false;
            body.GetTransform(out var transform);
            AABB box = default; bool any = false;
            for (int i = 0; i < body.FixtureList.Count; i++) {
                Fixture fixture = body.FixtureList[i];
                if (fixture.IsSensor) continue;
                for (int child = 0; child < fixture.Shape.ChildCount; child++) {
                    fixture.Shape.ComputeAABB(out AABB one, ref transform, child);
                    if (!any) { box = one; any = true; } else box.Combine(ref one);
                }
            }
            if (!any) return false;
            length = ConvertUnits.ToDisplayUnits(box.UpperBound.X - box.LowerBound.X) / units;
            height = ConvertUnits.ToDisplayUnits(box.UpperBound.Y - box.LowerBound.Y) / units;
            Vector2 world = ConvertUnits.ToDisplayUnits(new Vector2((box.LowerBound.X + box.UpperBound.X) * .5f, box.LowerBound.Y)) + offset;
            sole = new Vec2(world.X / units, world.Y / units);
            return float.IsFinite(length + height + sole.X + sole.Y) && length > .02f && height > .02f;
        }
    }
}
