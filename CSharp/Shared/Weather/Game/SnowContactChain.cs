using System;
using System.Collections.Generic;
using FarseerPhysics;
using FarseerPhysics.Common;
using FarseerPhysics.Collision.Shapes;
using FarseerPhysics.Dynamics;
using FarseerPhysics.Dynamics.Contacts;
using Microsoft.Xna.Framework;
using ScpItdMod.Core;

namespace ScpItdMod.Weather {
    internal sealed class SnowContactChain : IDisposable {
        private readonly List<Vertices> buffers = new List<Vertices>();
        private readonly List<Fixture> fixtures = new List<Fixture>();
        private readonly Category category, collidesWith;
        private bool disposed;
        internal Body Body { get; }
        internal SnowContactChain(World world, object marker, Category category, Category collidesWith) {
            this.category = category; this.collidesWith = collidesWith;
            Body = world.CreateBody(Vector2.Zero, 0, BodyType.Static); Body.UserData = marker;
        }
        // Snow holds a body up and never holds one down: a body whose centre lies under the line of the snow (climbing out of a hatch or off a ladder into a drift, standing up inside one)
        // passes through it upward, and is carried again once it is above. Runs are written left to right, so above is the left side of each edge.
        internal static void FilterContact(Contact contact, object marker, Predicate<Body> ignoresSupport) {
            Fixture a = contact.FixtureA, b = contact.FixtureB;
            bool snowA = ReferenceEquals(a.Body.UserData, marker);
            Fixture snow = snowA ? a : ReferenceEquals(b.Body.UserData, marker) ? b : null;
            if (snow == null) return;
            Body passenger = snowA ? b.Body : a.Body;
            if (ignoresSupport(passenger) || Beneath(snow, snowA ? contact.ChildIndexA : contact.ChildIndexB, passenger.WorldCenter)) contact.Enabled = false;
        }
        internal static bool Beneath(Fixture snow, int child, Vector2 centre) {
            if (snow.Shape is not ChainShape chain || (uint)(child + 1) >= (uint)chain.Vertices.Count) return false;
            snow.Body.GetTransform(out var transform);
            Vector2 v1 = Transform.Multiply(chain.Vertices[child], ref transform), v2 = Transform.Multiply(chain.Vertices[child + 1], ref transform), edge = v2 - v1, to = centre - v1;
            return edge.X * to.Y - edge.Y * to.X < 0;
        }
        internal bool Sweep(Vector2 from, Vector2 to, ref float best, out Vector2 point, out Vector2 normal) {
            point = default; normal = default; bool found = false;
            for (int i = 0; i < fixtures.Count; i++)
                if (fixtures[i].Shape is ChainShape chain && SnowTunnelGuard.Cross(chain.Vertices, from, to, ref best, out Vector2 hit, out Vector2 up)) { point = hit; normal = up; found = true; }
            return found;
        }
        internal void Update(ReadOnlySpan<Vec2> points, ReadOnlySpan<int> starts, Vector2 offset, float units) {
            if (disposed || Body.World == null) throw new ObjectDisposedException(nameof(SnowContactChain));
            if (Body.World.IsLocked) throw new InvalidOperationException("Cannot deform snow during physics integration");
            int runs = 0; float gap = Settings.LinearSlop * Settings.LinearSlop;
            for (int run = 0; run < starts.Length; run++) {
                if (runs == buffers.Count) buffers.Add(new Vertices());
                Vertices vertices = buffers[runs]; vertices.Clear();
                int end = run + 1 < starts.Length ? starts[run + 1] : points.Length;
                for (int k = starts[run]; k < end; k++) {
                    Vector2 local = ConvertUnits.ToSimUnits(new Vector2(points[k].X * units, points[k].Y * units) - offset);
                    if (vertices.Count == 0 || Vector2.DistanceSquared(vertices[vertices.Count - 1], local) > gap) vertices.Add(local);
                }
                if (vertices.Count >= 2) runs++;
            }
            bool same = fixtures.Count == runs;
            for (int run = 0; same && run < runs; run++) same = fixtures[run].Shape is ChainShape chain && chain.Vertices.Count == buffers[run].Count;
            if (!same) {
                for (int i = 0; i < fixtures.Count; i++) Body.Remove(fixtures[i]);
                fixtures.Clear();
                for (int run = 0; run < runs; run++) fixtures.Add(Body.CreateChainShape(buffers[run], category, collidesWith));
                return;
            }
            for (int run = 0; run < runs; run++) {
                var chain = (ChainShape)fixtures[run].Shape; Vertices vertices = buffers[run];
                for (int k = 0; k < vertices.Count; k++) chain.Vertices[k] = vertices[k];
            }
            Vector2 position = Body.Position; Body.SetTransformIgnoreContacts(ref position, Body.Rotation);
            for (var edge = Body.ContactList; edge != null; edge = edge.Next) if (edge.Other.BodyType == BodyType.Dynamic) edge.Other.Awake = true;
        }
        public void Dispose() { if (disposed) return; disposed = true; if (Body.World != null) Body.World.Remove(Body); fixtures.Clear(); buffers.Clear(); }
    }
}
