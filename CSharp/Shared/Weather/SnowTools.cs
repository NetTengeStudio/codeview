using System;
using System.Collections.Generic;
using ScpItdMod.Core;
using ScpItdMod.Weather.Material;

namespace ScpItdMod.Weather {
    // Snow carried on a blade or in the air: mass and volume per metre of span.
    public struct SnowPayload {
        public double Mass, Volume;
        public float Density => Volume > 0 ? (float)(Mass / Volume) : SnowPhysics.FreshDensity;
        public void Add(in SnowPayload other) { Mass += other.Mass; Volume += other.Volume; }
    }

    // Vehicles work the particles directly: a wheel or a skid presses a print into the pack. The shovel has its own file (SnowShovel).
    internal static class SnowTools {
        // Surface at a point: ground, top of the snow and the density of its top layer.
        internal static bool Probe(SnowPack pack, Vec2 point, float reach, out float ground, out float top, out float density) {
            ground = top = density = 0; if (pack == null) return false;
            int slot = pack.FindSurface(point, reach); if (slot < 0) return false;
            ground = pack.Ground(slot); top = pack.Top(slot, point.X);
            density = pack.Material.TopDensity(point.X, SnowSkin.Step * 2, ground - pack.Grid.Cell, top + .01f, top, .1f);
            if (density <= 0) density = SnowPhysics.FreshDensity;
            return true;
        }

        // A wheel or a skid of the given radius that the vehicle sinks by depth below the untouched snow beside it: the snow under the chord of contact is pressed like under a boot, with the
        // pressure that compacts the column under it down to that level, and nothing is woken (a rigid body driven through the solver would push the whole layer ahead of it as a plane-strain
        // wave, as the legs once did). The level is measured against the higher of the two sides, so a wheel that asks again on the same spot, every frame or standing still, presses no deeper.
        internal const float WheelRate = 30, WheelSide = .3f;
        internal static void PressInto(SnowPack pack, Vec2 point, float radius, float depth) {
            if (!float.IsFinite(radius) || radius <= 0 || !float.IsFinite(depth) || depth <= 0 || pack == null) return;
            int slot = pack.FindSurface(point, radius); if (slot < 0 || !pack.Band(slot, out float floor, out float ceiling)) return;
            SnowMpm material = pack.Material; depth = Math.Min(depth, radius);
            if (!material.Column(point.X, SnowSkin.Half, floor, ceiling, out float low, out float high) || high - low < SnowSkin.MinimumDepth) return;
            float half = MathF.Max(SnowSkin.Step, MathF.Sqrt(depth * (2 * radius - depth))), reference = high;
            for (int side = -1; side <= 1; side += 2)
                if (material.Column(point.X + side * (half + WheelSide), SnowSkin.Half, floor, ceiling, out _, out float beside)) reference = MathF.Max(reference, beside);
            float sink = high - (reference - depth); if (sink <= .005f) return;
            float density = material.TopDensity(point.X, SnowSkin.Step * 2, floor, ceiling, high, high - low);
            if (density <= 0) return;
            float compacted = MathF.Min(SnowConstitutive.MaximumDensity, density * (high - low) / MathF.Max(SnowConstitutive.Spacing, high - low - sink));
            SnowPrint.Press(material, point.X, half, SnowConstitutive.CrushStrength(compacted), 1 / 60f, floor, ceiling, WheelRate);
        }
    }
}
