using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FarseerPhysics.Dynamics;
using ScpItdMod.Core;
using ScpItdMod.Weather.Material;
using Vector2 = Microsoft.Xna.Framework.Vector2;

namespace ScpItdMod.Weather {
    internal sealed class SnowSupportField : IDisposable {
        private sealed class Chunk {
            internal SnowContactChain Surface;
            internal readonly List<Vec2> Points = new List<Vec2>();
            internal readonly List<int> Starts = new List<int>();
            internal WeatherGrid Grid;
            internal Vector2 Offset;
            internal double Seen, Built;
            internal ulong Stamp;
            internal int Layout = -1;
            internal bool Settling;
            internal uint Visited;
        }

        private readonly World world;
        private readonly object marker;
        private readonly Category category, collidesWith;
        private readonly float units;
        private readonly Dictionary<(object Owner, int Index), Chunk> chunks = new Dictionary<(object, int), Chunk>();
        private readonly List<(object Owner, int Index)> leaving = new List<(object, int)>();
        private readonly List<Vec2> points = new List<Vec2>();
        private readonly List<int> starts = new List<int>();
        private uint visit;
        private bool disposed;
        // Snow holds a body up and lets it go (FilterContact), so a line that climbs faster than the contact can carry leaves the body under it and drops it through. The line rises at a speed the contact solver follows
        // (less than a limb radius per step) and measured from the last visit, not from the last change: after a quiet spell the whole rise would otherwise come in one step. It may fall faster.
        internal const float ChunkWidth = 6, Reach = 4, Tolerance = .0025f, Rate = 2.5f, RiseRate = .5f, MaximumFrame = .1f;
        internal const double Linger = 2;

        internal SnowSupportField(World world, object marker, Category category, Category collidesWith, float units) {
            this.world = world ?? throw new ArgumentNullException(nameof(world));
            this.marker = marker ?? throw new ArgumentNullException(nameof(marker));
            if (!float.IsFinite(units) || units <= 0) throw new ArgumentOutOfRangeException(nameof(units));
            this.category = category; this.collidesWith = collidesWith; this.units = units;
        }

        internal void Begin() {
            if (disposed) throw new ObjectDisposedException(nameof(SnowSupportField));
            visit++;
        }

        internal void Include(object owner, Vector2 offset, float x, double now, SnowPack snow) {
            if (disposed) throw new ObjectDisposedException(nameof(SnowSupportField));
            if (offset == Vector2.Zero) owner = null;
            if (x + Reach < snow.Grid.Bounds.Left || x - Reach > snow.Grid.Bounds.Right) return;
            for (int key = (int)MathF.Floor((x - Reach) / ChunkWidth), last = (int)MathF.Floor((x + Reach) / ChunkWidth); key <= last; key++)
                Ensure(owner, offset, key, now, snow);
        }

        // The first crossing of the motion from -> to (simulation units, in the frame of the owner) through the snow line of the chunks that own that frame.
        internal bool Sweep(object owner, Vector2 offset, Vector2 from, Vector2 to, out Vector2 point, out Vector2 normal) {
            point = default; normal = default; if (disposed) return false;
            if (offset == Vector2.Zero) owner = null;
            float best = float.MaxValue; bool found = false;
            float low = (FarseerPhysics.ConvertUnits.ToDisplayUnits(MathF.Min(from.X, to.X)) + offset.X) / units, high = (FarseerPhysics.ConvertUnits.ToDisplayUnits(MathF.Max(from.X, to.X)) + offset.X) / units;
            for (int key = (int)MathF.Floor(low / ChunkWidth), last = (int)MathF.Floor(high / ChunkWidth); key <= last; key++) {
                if (!chunks.TryGetValue((owner, key), out Chunk chunk) || !chunk.Surface.Sweep(from, to, ref best, out Vector2 hit, out Vector2 up)) continue;
                point = hit; normal = up; found = true;
            }
            return found;
        }

        internal void End(double now) {
            leaving.Clear();
            foreach (var pair in chunks) if (now - pair.Value.Seen > Linger) leaving.Add(pair.Key);
            for (int i = 0; i < leaving.Count; i++) { chunks[leaving[i]].Surface.Dispose(); chunks.Remove(leaving[i]); }
        }

        private void Ensure(object owner, Vector2 offset, int key, double now, SnowPack snow) {
            var identity = (owner, key);
            bool fresh = !chunks.TryGetValue(identity, out Chunk chunk);
            if (fresh) {
                chunk = new Chunk { Surface = new SnowContactChain(world, marker, category, collidesWith) };
                chunks.Add(identity, chunk);
            }
            if (chunk.Visited == visit && !fresh) return;
            double since = fresh ? 0 : Math.Max(0, now - chunk.Seen); chunk.Visited = visit; chunk.Seen = now;
            ulong stamp = snow.Material.Stamp(key * ChunkWidth - snow.Grid.Cell - SnowSkin.Step * 4, (key + 1) * ChunkWidth + snow.Grid.Cell + SnowSkin.Step * 4);
            bool offsetChanged = chunk.Offset != offset;
            if (!fresh && !offsetChanged && !chunk.Settling && chunk.Stamp == stamp && ReferenceEquals(chunk.Grid, snow.Grid) && chunk.Layout == snow.Grid.LightRevision) return;
            chunk.Stamp = stamp; chunk.Grid = snow.Grid; chunk.Layout = snow.Grid.LightRevision; chunk.Settling = false;
            SnowSkin.Build(snow, key * ChunkWidth - snow.Grid.Cell, (key + 1) * ChunkWidth + snow.Grid.Cell, points, starts);
            if (!fresh && chunk.Points.Count == points.Count && chunk.Starts.Count == starts.Count) {
                float fall = Rate * (float)Math.Min(MaximumFrame, since), rise = RiseRate * (float)Math.Min(MaximumFrame, since);
                for (int i = 0; i < points.Count; i++) {
                    float was = chunk.Points[i].Y, change = points[i].Y - was, step = Math.Clamp(change, -fall, rise);
                    if (MathF.Abs(step - change) > 1e-6f) chunk.Settling = true;
                    points[i] = new Vec2(points[i].X, was + step);
                }
            }
            chunk.Built = now; chunk.Offset = offset;
            if (fresh || offsetChanged || Moved(chunk)) {
                chunk.Surface.Update(CollectionsMarshal.AsSpan(points), CollectionsMarshal.AsSpan(starts), offset, units);
                chunk.Points.Clear(); chunk.Points.AddRange(points); chunk.Starts.Clear(); chunk.Starts.AddRange(starts);
            }
        }

        private bool Moved(Chunk chunk) {
            if (chunk.Points.Count != points.Count || chunk.Starts.Count != starts.Count) return true;
            for (int i = 0; i < starts.Count; i++) if (chunk.Starts[i] != starts[i]) return true;
            for (int i = 0; i < points.Count; i++) if (MathF.Abs(chunk.Points[i].Y - points[i].Y) > Tolerance || MathF.Abs(chunk.Points[i].X - points[i].X) > Tolerance) return true;
            return false;
        }

        public void Dispose() {
            if (disposed) return;
            disposed = true;
            foreach (var pair in chunks) pair.Value.Surface.Dispose();
            chunks.Clear(); leaving.Clear();
        }
    }
}
