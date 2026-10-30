using Barotrauma;
using Microsoft.Xna.Framework;

namespace ScpItdMod.Rp {
    internal static class CharacterPicking {
        public static bool Contains(Character character, Vector2 world) {
            Limb[] limbs = character.AnimController?.Limbs;
            if (limbs == null) { return false; }
            for (int i = 0; i < limbs.Length; i++) {
                Limb limb = limbs[i];
                var body = limb?.body;
                if (body == null || limb.IsSevered) { continue; }
                Vector2 delta = world - body.DrawPosition;
                float angle = body.Rotation - body.DrawRotation;
                float c = System.MathF.Cos(angle), s = System.MathF.Sin(angle);
                Vector2 point = body.FarseerBody.Position + FarseerPhysics.ConvertUnits.ToSimUnits(new Vector2(c * delta.X - s * delta.Y, s * delta.X + c * delta.Y));
                foreach (var fixture in body.FarseerBody.FixtureList) {
                    if (fixture.TestPoint(ref point)) { return true; }
                }
            }
            return false;
        }
    }
}
