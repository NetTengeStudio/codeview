using System;
using System.Collections.Generic;
using ScpItdMod.Core;
using ScpItdMod.Weather.Material;

namespace ScpItdMod.Weather {
    // What a shovel does to the particles. A stroke cuts a slice of the width of the blade out of the top of the snow, layer after layer from the surface down, so the pit has a flat floor and the cut
    // walls then settle to the angle the snow can stand at (SnowPack.Slump). Pouring lays whole particles on the surface at rest, and each rolls off a crest before it stays. Mass is only moved
    // between the pack and a payload (what finds no place stays in the payload); nothing is woken, so a stroke costs a few hundred particles once and the solver never sees it.
    internal static class SnowShovel {
        internal const float Reach = .9f, Wall = .6f;
        private const int SlumpBudget = 160;
        private static readonly List<int> found = new List<int>();
        private static float centre; private static SnowMpm sorting;
        private static int Order(int a, int b) {
            ref readonly SnowPoint p = ref sorting.Point(a); ref readonly SnowPoint q = ref sorting.Point(b);
            int byHeight = q.Position.PreciseY.CompareTo(p.Position.PreciseY);
            return byHeight != 0 ? byHeight : Math.Abs(p.Position.PreciseX - centre).CompareTo(Math.Abs(q.Position.PreciseX - centre));
        }
        private static readonly Comparison<int> order = Order;

        // The point of the snow surface a cursor means: the snow under it within reach above and below, so a stroke aimed at a drift needs no pixel-exact aim.
        internal static bool Aim(SnowPack pack, Vec2 cursor, out Vec2 surface, out float density) {
            surface = cursor; density = SnowPhysics.FreshDensity;
            if (!SnowTools.Probe(pack, cursor, Reach, out float ground, out float top, out density) || top - ground < SnowSkin.MinimumDepth) return false;
            surface = new Vec2(cursor.X, top); return true;
        }

        // Cuts a slice of the given width and depth out of the surface under point.X, at most maximumMass per metre of span, and returns what was cut. The cut goes from the top down level by level and
        // from the middle of the blade outwards, so a stroke that cannot take everything leaves a flat floor with a ragged edge rather than a hole.
        internal static SnowPayload Dig(SnowPack pack, Vec2 point, float bite, float depth, double maximumMass) {
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(bite) || bite <= 0 || !float.IsFinite(depth) || depth <= 0 || !double.IsFinite(maximumMass) || maximumMass <= 0) throw new ArgumentException("Invalid excavation");
            SnowPayload result = default; if (pack == null) return result;
            int slot = pack.FindSurface(point, Reach); if (slot < 0 || !pack.Band(slot, out float floor, out float ceiling)) return result;
            SnowMpm material = pack.Material; float half = bite * .5f;
            if (!material.Column(point.X, SnowSkin.Half, floor, ceiling, out float low, out float top)) return result;
            float level = MathF.Max(low, top - depth);
            material.Collect(point.X - half, level, point.X + half, top + .01f, found);
            centre = point.X; sorting = material; found.Sort(order); sorting = null;
            float density = material.TopDensity(point.X, half, floor, ceiling, top, depth);
            for (int i = 0; i < found.Count; i++) {
                ref readonly SnowPoint p = ref material.Point(found[i]);
                if (result.Mass + p.Mass > maximumMass) break;
                result.Mass += p.Mass; result.Volume += p.Mass / p.ReferenceDensity; material.Remove(found[i]);
            }
            if (result.Mass > 0) pack.Slump(point.X - half - Wall, point.X + half + Wall, top, SnowConstitutive.Friction * Math.Clamp(density / 150, 1, 1.8f), SlumpBudget);
            return result;
        }

        // Lays the payload on the surface around point.X as whole resting particles, spread over radius on either side; what is less than one particle is dropped. False when nothing could be placed.
        internal static bool Pour(SnowPack pack, float temperature, Vec2 point, float radius, ref SnowPayload payload) {
            if (payload.Mass <= 0) { payload = default; return true; }
            if (!float.IsFinite(radius) || radius <= 0 || !double.IsFinite(payload.Mass) || !double.IsFinite(payload.Volume) || payload.Volume <= 0) throw new ArgumentException("Invalid snow payload");
            if (pack == null) return false;
            float density = Math.Clamp(payload.Density, SnowPhysics.FreshDensity, SnowConstitutive.MaximumDensity), mass = density * SnowConstitutive.Area;
            int count = (int)Math.Max(payload.Mass < mass * .5 ? 0 : 1, Math.Floor(payload.Mass / mass)), placed = 0;
            SnowMpm material = pack.Material;
            // A grain that finds no free place (a wall, a closed door, a ceiling) is tried again further out, up to three times the radius; what still finds none stays in the load.
            for (int k = 0; k < count; k++) {
                bool done = false;
                for (int widen = 1; widen <= 3 && !done; widen++) {
                    float x = point.X + radius * widen * (2 * Corput(k + 1 + widen * 7919) - 1);
                    if (!pack.Landing(ref x, point.Y, radius * widen + .5f, 1, out float top)) continue;
                    Vec2 at = new Vec2(x, top + SnowConstitutive.Spacing * .5f);
                    if (pack.Sample(at).Distance < SnowConstitutive.Clearance || !pack.Grid.Bounds.Contains(at.X, at.Y)) continue;
                    SnowMatrix elastic = SnowPlasticity.Overburden(density * SnowConstitutive.Gravity * SnowConstitutive.Spacing);
                    int id = material.Seed(new SnowPoint { Position = at, Mass = mass, ReferenceVolume = SnowConstitutive.Area, Enthalpy = Math.Min(-1, temperature) * SnowConstitutive.IceHeat, Elastic = elastic, Plastic = 1, Bond = .05f }, true);
                    material.Sampled(id, pack.Sample(at).Distance); placed++; done = true;
                }
            }
            if (placed == 0) return false;
            double share = Math.Min(1, placed * (double)mass / payload.Mass);
            payload.Mass -= payload.Mass * share; payload.Volume -= payload.Volume * share;
            if (payload.Mass < mass * .5) payload = default;
            return true;
        }
        private static float Corput(int n) { float f = .5f, r = 0; while (n > 0) { if ((n & 1) != 0) r += f; n >>= 1; f *= .5f; } return r; }
        internal static void Export(ref SnowPayload payload) { payload = default; }
    }
}
