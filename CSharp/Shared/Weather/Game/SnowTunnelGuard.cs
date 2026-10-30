using System;
using System.Collections.Generic;
using FarseerPhysics.Dynamics;
using Vector2 = Microsoft.Xna.Framework.Vector2;

namespace ScpItdMod.Weather {
    // The contact solver stops a body on the snow line only if the two meet inside one step. A limb that arrives deep enough in a step to have its centre already under the line is let through by
    // SnowContactChain.FilterContact (that is how a body stands up inside a drift), so the contact alone cannot tell a fall through the snow from a climb out of it. The motion can: a body that was above the
    // line when last seen and is under it now has crossed it downward, which no legitimate path does (a climb or an animation that passes through the snow is excluded by the caller before it asks).
    internal sealed class SnowTunnelGuard {
        internal const float Lift = 1.02f;
        private readonly Dictionary<Body, Vector2> seen = new Dictionary<Body, Vector2>();
        private readonly List<Body> stale = new List<Body>();
        private readonly HashSet<Body> touched = new HashSet<Body>();
        internal int Rescued { get; private set; }
        internal int Tracked => seen.Count;

        // The first crossing of the segment from -> to through the edge sequence, where above is the left side of each edge (runs are written left to right). The parameter along the segment is returned
        // so that the caller can compare crossings of several chains.
        internal static bool Cross(List<Vector2> vertices, Vector2 from, Vector2 to, ref float best, out Vector2 point, out Vector2 normal) {
            point = default; normal = default; bool found = false;
            float minX = MathF.Min(from.X, to.X), maxX = MathF.Max(from.X, to.X), minY = MathF.Min(from.Y, to.Y), maxY = MathF.Max(from.Y, to.Y);
            for (int i = 0; i + 1 < vertices.Count; i++) {
                Vector2 v1 = vertices[i], v2 = vertices[i + 1];
                if (MathF.Max(v1.X, v2.X) < minX || MathF.Min(v1.X, v2.X) > maxX || MathF.Max(v1.Y, v2.Y) < minY || MathF.Min(v1.Y, v2.Y) > maxY) continue;
                Vector2 edge = v2 - v1; float length = edge.LengthSquared(); if (length <= 1e-12f) continue;
                float above = edge.X * (from.Y - v1.Y) - edge.Y * (from.X - v1.X), below = edge.X * (to.Y - v1.Y) - edge.Y * (to.X - v1.X);
                if (above < 0 || below >= 0) continue;
                float t = above / (above - below);
                if (t >= best) continue;
                Vector2 hit = from + (to - from) * t; float along = Vector2.Dot(hit - v1, edge) / length;
                if (along < 0 || along > 1) continue;
                best = t; point = hit; float inverse = 1f / MathF.Sqrt(length); normal = new Vector2(-edge.Y * inverse, edge.X * inverse); found = true;
            }
            return found;
        }

        // Where a body that crossed the line is put back: on the line it crossed, lifted by its own radius, and the part of its velocity that drives it into the snow is removed.
        internal static Vector2 Surface(Vector2 point, Vector2 normal, float radius) => point + normal * (MathF.Max(radius, 0.005f) * Lift);
        internal static Vector2 Slide(Vector2 velocity, Vector2 normal) => velocity - normal * MathF.Min(0, Vector2.Dot(velocity, normal));

        internal void Begin() => touched.Clear();
        internal bool Previous(Body body, out Vector2 from) { touched.Add(body); return seen.TryGetValue(body, out from); }
        internal void Remember(Body body, Vector2 position) { touched.Add(body); seen[body] = position; }
        internal void Rescue() => Rescued++;
        internal void End() {
            stale.Clear();
            foreach (var pair in seen) if (!touched.Contains(pair.Key)) stale.Add(pair.Key);
            for (int i = 0; i < stale.Count; i++) seen.Remove(stale[i]);
        }
        internal void Clear() { seen.Clear(); touched.Clear(); stale.Clear(); }
    }
}
