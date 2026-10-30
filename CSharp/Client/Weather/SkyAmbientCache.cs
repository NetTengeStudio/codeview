using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;

namespace ScpItdMod.Weather {
    internal sealed class SkyAmbientCache : IDisposable {
        private readonly struct Key : IEquatable<Key> {
            private readonly int x, y, lod;
            internal Key(int x, int y, int lod) { this.x = x; this.y = y; this.lod = lod; }
            public bool Equals(Key other) => x == other.x && y == other.y && lod == other.lod;
            public override bool Equals(object obj) => obj is Key other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(x, y, lod);
        }
        private sealed class Tile { internal Key Key; internal VertexBuffer Buffer; internal int Count; internal ulong Used; }
        private sealed class Worker {
            internal readonly SkyTransferBuilder Builder = new SkyTransferBuilder();
            internal readonly Action Run;
            internal Task Task;
            internal WeatherGeometry Geometry;
            internal WeatherRect Area;
            internal float Spacing;
            internal Key Key;
            internal uint Epoch;
            internal CancellationToken Token;
            internal Worker() { Run = Build; }
            private void Build() { Builder.Build(Geometry, Area, Spacing, Token); }
        }
        private const int Capacity = 128;
        private readonly GraphicsDevice device;
        private readonly SkyTransferBuilder builder = new SkyTransferBuilder();
        private readonly Dictionary<Key, Tile> resident = new Dictionary<Key, Tile>(Capacity);
        private readonly Tile[] tiles = new Tile[Capacity];
        private readonly Worker[] workers = { new Worker(), new Worker() };
        private readonly VertexDeclaration declaration = new VertexDeclaration(
            new VertexElement(0, VertexElementFormat.Vector2, VertexElementUsage.Position, 0),
            new VertexElement(8, VertexElementFormat.Single, VertexElementUsage.TextureCoordinate, 0));
        private WeatherGrid bound;
        private WeatherGeometry snapshot;
        private CancellationTokenSource cancellation;
        private uint revision, epoch;
        private int occupied, lod, left, right, bottom, top, preLeft, preRight, preBottom, preTop;
        private float side;
        private ulong frame;
        private Matrix lastCamera;
        private bool cameraSeen, visibleResident;
        internal uint Builds { get; private set; }
        internal uint WorkRevision { get; private set; }
        internal long GpuBytes { get; private set; }
        internal SkyAmbientCache(GraphicsDevice device) { this.device = device; if (Marshal.SizeOf<SkyTransferVertex>() != declaration.VertexStride) throw new InvalidOperationException("Sky vertex layout is incompatible with the graphics backend"); for (int i = 0; i < tiles.Length; i++) tiles[i] = new Tile(); }
        internal void Invalidate() {
            cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null;
            bound = null; snapshot = null; resident.Clear(); occupied = 0; epoch++; cameraSeen = false; visibleResident = false;
        }
        private void Bind(WeatherGrid grid) {
            if (ReferenceEquals(bound, grid) && revision == grid.LightGeometry.Revision) return;
            Invalidate(); bound = grid; revision = grid.LightGeometry.Revision; cancellation = new CancellationTokenSource();
            snapshot = new WeatherGeometry();
            for (int i = 0; i < grid.LightGeometry.Count; i++) {
                ReadOnlySpan<Vec2> points = grid.LightGeometry.PolygonVertices(i, out bool front);
                if (!points.IsEmpty) snapshot.Add(points, front);
            }
            snapshot.Seal();
        }
        private void Footprint(WeatherGrid grid, Matrix worldToScreen, int width, int height) {
            int oldLod = lod, oldLeft = left, oldRight = right, oldBottom = bottom, oldTop = top;
            Matrix inverse = Matrix.Invert(worldToScreen);
            Vector2 a = Vector2.Transform(Vector2.Zero, inverse), b = Vector2.Transform(new Vector2(width, 0), inverse);
            Vector2 c = Vector2.Transform(new Vector2(0, height), inverse), d = Vector2.Transform(new Vector2(width, height), inverse);
            Vector2 centre = (a + d) * .5f;
            float radius = MathF.Max((a - centre).Length(), (b - centre).Length());
            if (!float.IsFinite(radius) || radius <= 0) throw new InvalidOperationException("Sky projection must be finite and invertible");
            side = 8; lod = 0;
            while (Math.Ceiling((double)radius * 2 / side) + 2 > 8) { side *= 2; lod++; }
            WeatherRect visible = new WeatherRect(MathF.Min(MathF.Min(a.X, b.X), MathF.Min(c.X, d.X)), MathF.Min(MathF.Min(a.Y, b.Y), MathF.Min(c.Y, d.Y)),
                MathF.Max(MathF.Max(a.X, b.X), MathF.Max(c.X, d.X)), MathF.Max(MathF.Max(a.Y, b.Y), MathF.Max(c.Y, d.Y))).Intersect(grid.Bounds);
            WeatherRect prefetch = new WeatherRect(centre.X - radius, centre.Y - radius, centre.X + radius, centre.Y + radius).Intersect(grid.Bounds);
            left = (int)MathF.Floor(visible.Left / side); right = (int)MathF.Ceiling(visible.Right / side);
            bottom = (int)MathF.Floor(visible.Bottom / side); top = (int)MathF.Ceiling(visible.Top / side);
            preLeft = (int)MathF.Floor(prefetch.Left / side); preRight = (int)MathF.Ceiling(prefetch.Right / side);
            preBottom = (int)MathF.Floor(prefetch.Bottom / side); preTop = (int)MathF.Ceiling(prefetch.Top / side);
            visibleResident &= oldLod == lod && oldLeft == left && oldRight == right && oldBottom == bottom && oldTop == top;
        }
        private WeatherRect Area(WeatherGrid grid, int x, int y) => new WeatherRect(x * side, y * side, (x + 1) * side, (y + 1) * side).Intersect(grid.Bounds);
        private Worker Pending(Key key) {
            for (int i = 0; i < workers.Length; i++) if (workers[i].Task != null && workers[i].Epoch == epoch && workers[i].Key.Equals(key)) return workers[i];
            return null;
        }
        private Tile Upload(Key key, SkyTransferBuilder source) {
            ReadOnlySpan<SkyTransferVertex> data = source.Vertices;
            if (resident.TryGetValue(key, out Tile existing)) return existing;
            Tile tile;
            if (occupied < Capacity) tile = tiles[occupied++];
            else {
                tile = tiles[0]; for (int i = 1; i < tiles.Length; i++) if (tiles[i].Used < tile.Used) tile = tiles[i];
                if (tile.Used == frame) throw new InvalidOperationException("Sky tile footprint exceeded its residency bound");
                resident.Remove(tile.Key); visibleResident = false;
            }
            tile.Key = key; tile.Count = data.Length; tile.Used = frame;
            if (data.Length > 0) {
                if (tile.Buffer == null || tile.Buffer.IsDisposed || tile.Buffer.VertexCount < data.Length) {
                    if (tile.Buffer != null) { GpuBytes -= (long)tile.Buffer.VertexCount * declaration.VertexStride; tile.Buffer.Dispose(); }
                    tile.Buffer = new VertexBuffer(device, declaration, data.Length, BufferUsage.WriteOnly);
                    GpuBytes += (long)tile.Buffer.VertexCount * declaration.VertexStride;
                }
                tile.Buffer.SetData(source.Storage, 0, data.Length);
            }
            resident.Add(key, tile); Builds++; WorkRevision++; return tile;
        }
        private void Complete(Worker worker) {
            if (!worker.Task.IsCanceled) {
                worker.Task.GetAwaiter().GetResult();
                if (worker.Epoch == epoch) Upload(worker.Key, worker.Builder);
            }
            worker.Task = null; worker.Geometry = null;
        }
        internal void Advance(WeatherGrid grid, Matrix worldToScreen, int width, int height, in Matrix camera) {
            Bind(grid); Footprint(grid, worldToScreen, width, height); frame++;
            bool moving = cameraSeen && camera != lastCamera; lastCamera = camera; cameraSeen = true;
            for (int i = 0; i < workers.Length; i++) if (workers[i].Task?.IsCompleted == true) Complete(workers[i]);
            if (!visibleResident) Queue(grid, false);
            if (moving) Queue(grid, true);
        }
        private void Queue(WeatherGrid grid, bool prefetch) {
            int y0 = prefetch ? preBottom : bottom, y1 = prefetch ? preTop : top;
            int x0 = prefetch ? preLeft : left, x1 = prefetch ? preRight : right;
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) {
                if (prefetch && x >= left && x < right && y >= bottom && y < top) continue;
                var key = new Key(x, y, lod);
                if (resident.ContainsKey(key) || Pending(key) != null) continue;
                Worker worker = null;
                for (int i = 0; i < workers.Length; i++) if (workers[i].Task == null) { worker = workers[i]; break; }
                if (worker == null) return;
                worker.Key = key; worker.Area = Area(grid, x, y); worker.Spacing = side / 16; worker.Epoch = epoch; worker.Geometry = snapshot; worker.Token = cancellation.Token;
                WorkRevision++;
                if (ExecutionContext.IsFlowSuppressed()) worker.Task = Task.Run(worker.Run, worker.Token);
                else { using (ExecutionContext.SuppressFlow()) worker.Task = Task.Run(worker.Run, worker.Token); }
            }
        }
        private void BuildAvailable(WeatherGrid grid) {
            for (int y = bottom; y < top; y++) for (int x = left; x < right; x++) {
                var key = new Key(x, y, lod);
                if (resident.ContainsKey(key) || Pending(key) != null) continue;
                builder.Build(snapshot, Area(grid, x, y), side / 16); Upload(key, builder); return;
            }
        }
        internal void Draw(WeatherGrid grid) {
            for (int y = bottom; y < top; y++) for (int x = left; x < right; x++) {
                var key = new Key(x, y, lod);
                if (!resident.TryGetValue(key, out Tile tile)) {
                    Worker pending = Pending(key);
                    if (pending != null) {
                        if (!pending.Task.IsCompleted) BuildAvailable(grid);
                        Complete(pending); tile = resident[key];
                    } else { builder.Build(snapshot, Area(grid, x, y), side / 16); tile = Upload(key, builder); }
                    Queue(grid, false);
                }
                tile.Used = frame;
                if (tile.Count > 0) { device.SetVertexBuffer(tile.Buffer); device.DrawPrimitives(PrimitiveType.TriangleList, 0, tile.Count / 3); }
            }
            visibleResident = true;
        }
        public void Dispose() {
            Invalidate();
            Task pending = Task.WhenAll(workers[0].Task ?? Task.CompletedTask, workers[1].Task ?? Task.CompletedTask);
            try { pending.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) when (pending.IsCanceled) { }
            finally {
                for (int i = 0; i < workers.Length; i++) { workers[i].Task = null; workers[i].Geometry = null; }
                for (int i = 0; i < tiles.Length; i++) { tiles[i].Buffer?.Dispose(); tiles[i].Buffer = null; }
                declaration.Dispose(); GpuBytes = 0;
            }
        }
    }
}
