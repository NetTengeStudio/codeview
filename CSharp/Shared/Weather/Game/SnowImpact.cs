using System;
using Barotrauma;
using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;
using ScpItdMod.Core;

namespace ScpItdMod.Weather {
    internal static class SnowImpact {
        internal static bool Apply(Ragdoll __instance, Fixture f1, Fixture f2, Vector2 worldNormal, Vector2 impactPos, ref Vector2 velocity) {
            if (!WeatherService.Active || !SnowRuntime.Ready || !SnowSupportBodies.Owns(f2?.Body)) return true;
            if (__instance.IgnorePlatforms || (__instance as AnimController)?.IsClimbing == true) return false;
            // Only a landing is cushioned, and only its fall: a boot that meets the side of a mound is not stopped by snow, and the speed along the ground is never taken.
            if (MathF.Abs(worldNormal.Y) < .6f) return true;
            float speed = Vector2.Dot(velocity, -worldNormal);
            if (speed <= 0) return true;
            Vector2 offset = __instance.Character.Submarine?.Position ?? Vector2.Zero;
            // A body that comes up through the snow is not stopped by it (SnowContactChain.Beneath), so it does not land on it either.
            if (f1?.Body != null) {
                Vector2 centre = FarseerPhysics.ConvertUnits.ToDisplayUnits(f1.Body.WorldCenter) + offset;
                var at = new Vec2(centre.X, centre.Y) / WeatherService.UnitsPerMetre;
                if (SnowRuntime.Surface(at, SnowContactModel.FootAbove, SnowContactModel.FootBelow, out _, out float high) && at.Y < high) return false;
            }
            Vector2 world = FarseerPhysics.ConvertUnits.ToDisplayUnits(impactPos) + offset;
            var point = new Vec2(world.X, world.Y) / WeatherService.UnitsPerMetre;
            // The landing is absorbed by what the particles under the impact are: their depth, density and softness at this moment.
            SnowFooting footing = SnowRuntime.Footing(point, SnowRuntime.Pack.Grid.Cell + SnowRuntime.Pack.MaximumDepth);
            if (footing.Contact == SnowContact.None) return true;
            float residual = SnowPhysics.LandingSpeed(speed, footing.Depth, footing.Compaction, footing.Fresh);
            velocity.Y += worldNormal.Y * (speed - residual);
            return true;
        }
    }
}
