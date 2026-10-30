using System;
using System.Collections.Generic;
using Barotrauma;
using FarseerPhysics.Dynamics;
using FarseerPhysics.Dynamics.Contacts;
using FarseerPhysics.Collision;
using ScpItdMod.Core;
using ScpItdMod.Weather.Material;
using Vector2 = Microsoft.Xna.Framework.Vector2;

namespace ScpItdMod.Weather {
    internal static class SnowSupportBodies {
        internal static readonly object Marker = new object();
        private static SnowSupportField field;
        private static World contactWorld;
        private static readonly Predicate<Body> ignoresSupport = IgnoresSupport;
        private static bool IgnoresSupport(Body body) {
            Limb limb = body.UserData as Limb; Character character = limb != null ? limb.character : body.UserData as Character;
            AnimController anim = character?.AnimController;
            return anim != null && (anim.IgnorePlatforms || anim.IsClimbing);
        }
        private static void BeforeSolve(Contact contact, ref Manifold oldManifold) => SnowContactChain.FilterContact(contact, Marker, ignoresSupport);
        internal static bool Owns(Body body) => body?.UserData == Marker;

        internal static void Update() {
            SnowPack snow = SnowRuntime.Pack;
            if (!WeatherService.Config.SnowSupport || snow == null || GameMain.World == null || GameMain.World.IsLocked) return;
            if (!ReferenceEquals(contactWorld, GameMain.World)) {
                Release(); contactWorld = GameMain.World;
                contactWorld.ContactManager.PreSolve += BeforeSolve;
                field = new SnowSupportField(contactWorld, Marker, Physics.CollisionWall, Physics.CollisionCharacter, WeatherService.UnitsPerMetre);
            }
            double now = Timing.TotalTime; field.Begin(); guard.Begin();
            IReadOnlyList<Character> people = Humans.All;
            float reach = FarseerPhysics.ConvertUnits.ToSimUnits(MaximumSweepMetres * WeatherService.UnitsPerMetre);
            for (int i = 0; i < people.Count; i++) {
                Character c = people[i];
                if (c.Removed || !c.Enabled) continue;
                float x = c.WorldPosition.X / WeatherService.UnitsPerMetre; Vector2 offset = c.Submarine?.Position ?? Vector2.Zero;
                field.Include(c.Submarine, offset, x, now, snow);
                Hold(c, offset, reach);
            }
            guard.End(); field.End(now);
        }

        // Every body of a character that the snow carries is followed from one call to the next; one that went from above the snow line to under it between two calls is put back on the line.
        private static void Hold(Character c, Vector2 offset, float reach) {
            AnimController anim = c.AnimController; if (anim == null) return;
            bool exempt = anim.IgnorePlatforms || anim.IsClimbing;
            Limb[] limbs = anim.Limbs;
            Follow(c, anim.Collider, exempt, offset, reach);
            for (int i = 0; i < limbs.Length; i++) Follow(c, limbs[i]?.body, exempt, offset, reach);
        }
        private static void Follow(Character c, PhysicsBody body, bool exempt, Vector2 offset, float reach) {
            Body farseer = body?.FarseerBody; if (farseer == null || !farseer.Enabled || farseer.BodyType != FarseerPhysics.BodyType.Dynamic) return;
            Vector2 position = body.SimPosition;
            bool carried = !exempt && (body.CollidesWith & Physics.CollisionWall) != 0 && (body.CollisionCategories & Physics.CollisionCharacter) != 0;
            if (guard.Previous(farseer, out Vector2 from) && carried && (position - from).LengthSquared() <= reach * reach && field.Sweep(c.Submarine, offset, from, position, out Vector2 hit, out Vector2 up)) {
                position = SnowTunnelGuard.Surface(hit, up, body.Radius);
                body.SetTransformIgnoreContacts(position, body.Rotation);
                body.LinearVelocity = SnowTunnelGuard.Slide(body.LinearVelocity, up);
                guard.Rescue();
            }
            guard.Remember(farseer, position);
        }
        internal const float MaximumSweepMetres = 1f;
        internal static int Rescued => guard.Rescued;
        private static readonly SnowTunnelGuard guard = new SnowTunnelGuard();

        internal static void Release() {
            if (contactWorld != null) { contactWorld.ContactManager.PreSolve -= BeforeSolve; contactWorld = null; }
            field?.Dispose(); field = null; guard.Clear();
        }
    }
}
