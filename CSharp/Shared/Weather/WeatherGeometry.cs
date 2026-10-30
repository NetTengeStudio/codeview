using System;
using System.Collections.Generic;
using System.Buffers;
using System.Runtime.InteropServices;
using ScpItdMod.Core;

namespace ScpItdMod.Weather {
    public readonly struct GeometrySample {
        public readonly float Distance;
        public readonly Vec2 Normal;
        public readonly int Shape;
        public readonly bool FrontSurface;
        public GeometrySample(float distance, Vec2 normal, int shape, bool frontSurface = false) { Distance = distance; Normal = normal; Shape = shape; FrontSurface = frontSurface; }
    }

    public sealed class WeatherGeometry {
        private readonly struct Edge {
            internal readonly Vec2 A, Delta, Normal;
            internal readonly float InverseLengthSquared;
            internal Edge(Vec2 a, Vec2 b) {
                A = a; Delta = b - a; float length = Delta.LengthSquared();
                InverseLengthSquared = length > 1e-16f ? 1 / length : 0;
                Normal = InverseLengthSquared > 0 ? new Vec2(Delta.Y, -Delta.X) / MathF.Sqrt(length) : default;
            }
        }
        private struct Polygon { internal int First, Count; internal WeatherRect Bounds; internal bool Enabled, Gate, FrontSurface, Rectangle; }
        private struct Node { internal WeatherRect Bounds; internal int First, Count, Left, Right; }
        private sealed class ByAxis : IComparer<int> {
            private readonly WeatherGeometry geometry;
            private readonly bool horizontal;
            internal ByAxis(WeatherGeometry geometry, bool horizontal) { this.geometry = geometry; this.horizontal = horizontal; }
            public int Compare(int a, int b) {
                WeatherRect x = geometry.polygons[a].Bounds, y = geometry.polygons[b].Bounds;
                return horizontal ? (x.Left + x.Right).CompareTo(y.Left + y.Right) : (x.Bottom + x.Top).CompareTo(y.Bottom + y.Top);
            }
        }
        private readonly List<Vec2> vertices = new List<Vec2>();
        private readonly List<Edge> edges = new List<Edge>();
        private readonly List<Polygon> polygons = new List<Polygon>();
        private readonly ByAxis byX, byY;
        private int[] order = Array.Empty<int>();
        private Node[] nodes = Array.Empty<Node>();
        private int used;
        private bool dirty;
        private const int ClearanceTileBudget = 1 << 21, ClearanceFar = 255;
        private const float ClearanceTileMetres = .5f;
        private byte[] clearance = Array.Empty<byte>();
        private int clearanceColumns, clearanceRows;
        private float clearanceLeft, clearanceBottom, clearanceTile, clearanceInverseTile;
        private readonly WeatherRect[] changes = new WeatherRect[64];
        private void Changed(in WeatherRect bounds) { Revision++; changes[Revision % 64] = bounds; }
        public bool ChangesSince(uint revision, out WeatherRect bounds) {
            uint count = unchecked(Revision - revision); bounds = default;
            if (count == 0) return true;
            if (count > 64) return false;
            bounds = changes[(revision + 1) % 64];
            for (uint i = 2; i <= count; i++) {
                WeatherRect b = changes[unchecked(revision + i) % 64];
                bounds = new WeatherRect(MathF.Min(bounds.Left, b.Left), MathF.Min(bounds.Bottom, b.Bottom), MathF.Max(bounds.Right, b.Right), MathF.Max(bounds.Top, b.Top));
            }
            return float.IsFinite(bounds.Right - bounds.Left) && float.IsFinite(bounds.Top - bounds.Bottom);
        }
        public int Count => polygons.Count;
        public uint Revision { get; private set; }
        public ReadOnlySpan<Vec2> PolygonVertices(int shape, out bool frontSurface) {
            if ((uint)shape >= (uint)polygons.Count) throw new ArgumentOutOfRangeException(nameof(shape));
            ref readonly Polygon polygon = ref CollectionsMarshal.AsSpan(polygons)[shape];
            frontSurface = polygon.FrontSurface;
            return polygon.Enabled ? CollectionsMarshal.AsSpan(vertices).Slice(polygon.First, polygon.Count) : ReadOnlySpan<Vec2>.Empty;
        }
        public WeatherGeometry() { byX = new ByAxis(this, true); byY = new ByAxis(this, false); }
        public void Clear() { vertices.Clear(); edges.Clear(); polygons.Clear(); dirty = true; Changed(new WeatherRect(float.NegativeInfinity, float.NegativeInfinity, float.PositiveInfinity, float.PositiveInfinity)); }
        public int Add(in WeatherRect rect, bool frontSurface = false) {
            Span<Vec2> points = stackalloc Vec2[4];
            points[0] = new Vec2(rect.Left, rect.Bottom); points[1] = new Vec2(rect.Right, rect.Bottom);
            points[2] = new Vec2(rect.Right, rect.Top); points[3] = new Vec2(rect.Left, rect.Top);
            return Add(points, frontSurface);
        }
        public int Add(ReadOnlySpan<Vec2> points, bool frontSurface = false) {
            if (points.Length < 3) throw new ArgumentException("A convex polygon needs three vertices", nameof(points));
            double area = 0; bool rectangle = points.Length == 4;
            float left = float.PositiveInfinity, right = float.NegativeInfinity, bottom = float.PositiveInfinity, top = float.NegativeInfinity;
            for (int i = 0; i < points.Length; i++) {
                Vec2 p = points[i], q = points[(i + 1) % points.Length];
                if (!float.IsFinite(p.X) || !float.IsFinite(p.Y)) throw new ArgumentException("Nonfinite geometry", nameof(points));
                area += (double)p.X * q.Y - (double)p.Y * q.X;
                rectangle &= (p.X == q.X) != (p.Y == q.Y);
                left = MathF.Min(left, p.X); right = MathF.Max(right, p.X); bottom = MathF.Min(bottom, p.Y); top = MathF.Max(top, p.Y);
            }
            if (Math.Abs(area) < 1e-12) throw new ArgumentException("Degenerate geometry", nameof(points));
            int first = vertices.Count;
            if (area > 0) for (int i = 0; i < points.Length; i++) vertices.Add(points[i]);
            else for (int i = points.Length - 1; i >= 0; i--) vertices.Add(points[i]);
            for (int i = 0; i < points.Length; i++) edges.Add(new Edge(vertices[first + i], vertices[first + (i + 1) % points.Length]));
            int result = polygons.Count;
            polygons.Add(new Polygon { First = first, Count = points.Length, Bounds = new WeatherRect(left, bottom, right, top), Enabled = true, FrontSurface = frontSurface, Rectangle = rectangle });
            dirty = true; Changed(new WeatherRect(left, bottom, right, top)); return result;
        }
        public int AddGate(in WeatherRect rect) {
            int shape = Add(rect, true); Polygon polygon = polygons[shape]; polygon.Gate = true; polygons[shape] = polygon; return shape;
        }
        public void Enable(int shape, bool enabled) {
            if ((uint)shape >= (uint)polygons.Count) throw new ArgumentOutOfRangeException(nameof(shape));
            Polygon polygon = polygons[shape]; if (polygon.Enabled == enabled) return;
            polygon.Enabled = enabled; polygons[shape] = polygon; Changed(polygon.Bounds);
        }
        public void Remove(in WeatherRect rectangle) {
            if (!(rectangle.Right > rectangle.Left && rectangle.Top > rectangle.Bottom)) return;
            int count = polygons.Count, maximum = 0;
            for (int i = 0; i < count; i++) maximum = Math.Max(maximum, polygons[i].Count);
            Vec2[] first = ArrayPool<Vec2>.Shared.Rent(maximum + 8), second = ArrayPool<Vec2>.Shared.Rent(maximum + 8), outside = ArrayPool<Vec2>.Shared.Rent(maximum + 8);
            try {
                for (int i = 0; i < count; i++) {
                    Polygon polygon = polygons[i]; WeatherRect b = polygon.Bounds;
                    if (!polygon.Enabled || polygon.Gate || b.Right <= rectangle.Left || b.Left >= rectangle.Right || b.Top <= rectangle.Bottom || b.Bottom >= rectangle.Top) continue;
                    int length = polygon.Count;
                    for (int k = 0; k < length; k++) first[k] = vertices[polygon.First + k];
                    Enable(i, false);
                    for (int side = 0; side < 4 && length >= 3; side++) {
                        int n = ClipPolygon(first.AsSpan(0, length), outside, rectangle, side, false);
                        if (n >= 3 && Nondegenerate(outside.AsSpan(0, n))) Add(outside.AsSpan(0, n), polygon.FrontSurface);
                        length = ClipPolygon(first.AsSpan(0, length), second, rectangle, side, true);
                        Vec2[] swap = first; first = second; second = swap;
                    }
                }
            } finally { ArrayPool<Vec2>.Shared.Return(first); ArrayPool<Vec2>.Shared.Return(second); ArrayPool<Vec2>.Shared.Return(outside); }
        }
        private static bool Nondegenerate(ReadOnlySpan<Vec2> points) {
            double area = 0;
            for (int i = 0; i < points.Length; i++) { Vec2 a = points[i], b = points[(i + 1) % points.Length]; area += (double)a.X * b.Y - (double)a.Y * b.X; }
            return Math.Abs(area) > 1e-12;
        }
        private static double HalfPlane(Vec2 p, in WeatherRect r, int side) => side switch { 0 => (double)p.X - r.Left, 1 => (double)r.Right - p.X, 2 => (double)p.Y - r.Bottom, _ => (double)r.Top - p.Y };
        private static int ClipPolygon(ReadOnlySpan<Vec2> source, Span<Vec2> destination, in WeatherRect rectangle, int side, bool inside) {
            int count = 0; Vec2 a = source[source.Length - 1]; double da = HalfPlane(a, rectangle, side) * (inside ? 1 : -1);
            for (int i = 0; i < source.Length; i++) {
                Vec2 b = source[i]; double db = HalfPlane(b, rectangle, side) * (inside ? 1 : -1);
                if (da >= 0 != db >= 0) destination[count++] = a + (b - a) * (float)(da / (da - db));
                if (db >= 0) destination[count++] = b;
                a = b; da = db;
            }
            return count;
        }
        public void Seal() {
            if (!dirty) return;
            order = new int[polygons.Count]; nodes = new Node[Math.Max(1, polygons.Count * 2)];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            used = 0; if (order.Length > 0) Build(0, order.Length);
            BuildClearance();
            dirty = false;
        }
        // Chebyshev distance in tiles from every tile to the nearest tile touched by the bounds of any polygon, enabled or not, so that enabling a polygon never invalidates it.
        // Two chamfer passes over the tiles. A segment shorter than (distance - 1) tiles that starts in a tile cannot reach any polygon.
        private void BuildClearance() {
            clearanceColumns = clearanceRows = 0;
            if (polygons.Count == 0) return;
            WeatherRect all = polygons[0].Bounds;
            for (int i = 1; i < polygons.Count; i++) {
                WeatherRect b = polygons[i].Bounds;
                all = new WeatherRect(MathF.Min(all.Left, b.Left), MathF.Min(all.Bottom, b.Bottom), MathF.Max(all.Right, b.Right), MathF.Max(all.Top, b.Top));
            }
            float width = all.Right - all.Left, height = all.Top - all.Bottom;
            if (!float.IsFinite(width) || !float.IsFinite(height)) return;
            float tile = ClearanceTileMetres;
            while ((width / tile + 3) * (height / tile + 3) > ClearanceTileBudget) tile *= 2;
            int columns = (int)MathF.Ceiling(width / tile) + 3, rows = (int)MathF.Ceiling(height / tile) + 3;
            if (clearance.Length < columns * rows) clearance = new byte[columns * rows];
            Span<byte> field = clearance.AsSpan(0, columns * rows);
            field.Fill((byte)ClearanceFar);
            clearanceLeft = all.Left - tile; clearanceBottom = all.Bottom - tile; clearanceTile = tile; clearanceInverseTile = 1 / tile;
            for (int i = 0; i < polygons.Count; i++) {
                WeatherRect b = polygons[i].Bounds;
                int x0 = (int)MathF.Floor((b.Left - clearanceLeft) * clearanceInverseTile), x1 = (int)MathF.Floor((b.Right - clearanceLeft) * clearanceInverseTile);
                int y0 = (int)MathF.Floor((b.Bottom - clearanceBottom) * clearanceInverseTile), y1 = (int)MathF.Floor((b.Top - clearanceBottom) * clearanceInverseTile);
                for (int y = y0; y <= y1; y++) field.Slice(y * columns + x0, x1 - x0 + 1).Clear();
            }
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < columns; x++) {
                    int at = y * columns + x, best = field[at];
                    if (best == 0) continue;
                    if (x > 0) best = Math.Min(best, field[at - 1] + 1);
                    if (y > 0) {
                        best = Math.Min(best, field[at - columns] + 1);
                        if (x > 0) best = Math.Min(best, field[at - columns - 1] + 1);
                        if (x + 1 < columns) best = Math.Min(best, field[at - columns + 1] + 1);
                    }
                    field[at] = (byte)Math.Min(best, ClearanceFar);
                }
            for (int y = rows - 1; y >= 0; y--)
                for (int x = columns - 1; x >= 0; x--) {
                    int at = y * columns + x, best = field[at];
                    if (best == 0) continue;
                    if (x + 1 < columns) best = Math.Min(best, field[at + 1] + 1);
                    if (y + 1 < rows) {
                        best = Math.Min(best, field[at + columns] + 1);
                        if (x + 1 < columns) best = Math.Min(best, field[at + columns + 1] + 1);
                        if (x > 0) best = Math.Min(best, field[at + columns - 1] + 1);
                    }
                    field[at] = (byte)Math.Min(best, ClearanceFar);
                }
            clearanceColumns = columns; clearanceRows = rows;
        }
        public float ClearShare(in WeatherRect area, float reachMetres) {
            Seal(); int near = 0, total = 0;
            for (float y = area.Bottom; y < area.Top; y += .5f)
                for (float x = area.Left; x < area.Right; x += .5f) { total++; if (ProvablyClear(new Vec2(x, y), new Vec2(x + reachMetres, y))) near++; }
            return total == 0 ? 0 : near / (float)total;
        }
        private bool ProvablyClear(Vec2 a, Vec2 b) {
            int x = (int)MathF.Floor((a.X - clearanceLeft) * clearanceInverseTile), y = (int)MathF.Floor((a.Y - clearanceBottom) * clearanceInverseTile);
            if ((uint)x >= (uint)clearanceColumns || (uint)y >= (uint)clearanceRows) return false;
            float reach = (clearance[y * clearanceColumns + x] - 1) * clearanceTile - 1e-3f;
            Vec2 delta = b - a;
            return reach > 0 && delta.X * delta.X + delta.Y * delta.Y < reach * reach;
        }
        private int Build(int first, int count) {
            int index = used++;
            WeatherRect bounds = polygons[order[first]].Bounds;
            for (int i = first + 1; i < first + count; i++) {
                WeatherRect b = polygons[order[i]].Bounds;
                bounds = new WeatherRect(MathF.Min(bounds.Left, b.Left), MathF.Min(bounds.Bottom, b.Bottom), MathF.Max(bounds.Right, b.Right), MathF.Max(bounds.Top, b.Top));
            }
            nodes[index] = new Node { Bounds = bounds, First = first, Count = count, Left = -1, Right = -1 };
            if (count <= 4) return index;
            Array.Sort(order, first, count, bounds.Right - bounds.Left >= bounds.Top - bounds.Bottom ? byX : byY);
            int half = count / 2, left = Build(first, half), right = Build(first + half, count - half);
            nodes[index].Left = left; nodes[index].Right = right; nodes[index].Count = 0;
            return index;
        }
        private static float BoundsDistance(in WeatherRect b, Vec2 p) {
            float x = MathF.Max(b.Left - p.X, MathF.Max(0, p.X - b.Right)), y = MathF.Max(b.Bottom - p.Y, MathF.Max(0, p.Y - b.Top));
            return x * x + y * y;
        }
        public GeometrySample Sample(Vec2 point, float maximum = float.MaxValue) {
            Seal();
            float best = maximum; Vec2 normal = Vec2.UnitY; int shape = -1;
            if (used > 0) Nearest(0, point, ref best, ref normal, ref shape);
            return new GeometrySample(best, normal, shape, shape >= 0 && polygons[shape].FrontSurface);
        }
        private void Nearest(int index, Vec2 point, ref float best, ref Vec2 normal, ref int shape) {
            ref readonly Node node = ref nodes[index];
            float bound = BoundsDistance(node.Bounds, point);
            if (best < 0 ? bound > 0 : bound > best * best) return;
            if (node.Count == 0) {
                int a = node.Left, b = node.Right;
                if (BoundsDistance(nodes[a].Bounds, point) > BoundsDistance(nodes[b].Bounds, point)) { int swap = a; a = b; b = swap; }
                Nearest(a, point, ref best, ref normal, ref shape); Nearest(b, point, ref best, ref normal, ref shape); return;
            }
            for (int k = node.First; k < node.First + node.Count; k++) {
                int id = order[k]; Polygon polygon = polygons[id]; if (!polygon.Enabled) continue;
                float squared = float.PositiveInfinity; bool inside = true, face = false; Vec2 outward = default;
                for (int j = 0; j < polygon.Count; j++) {
                    ref readonly Edge record = ref CollectionsMarshal.AsSpan(edges)[polygon.First + j];
                    if (record.InverseLengthSquared <= 0) continue;
                    Vec2 delta = point - record.A, n = record.Normal, edge = record.Delta;
                    float signed = (float)((double)delta.X * n.X + (double)delta.Y * n.Y);
                    if (signed > 0) inside = false;
                    float t = Vec2.Dot(delta, edge) * record.InverseLengthSquared;
                    bool onFace = t > 0 && t < 1;
                    Vec2 closest = delta - edge * Math.Clamp(t, 0, 1);
                    float d = onFace ? signed * signed : closest.LengthSquared();
                    if (d < squared) { squared = d; face = onFace; outward = onFace || d <= 1e-16f ? n : closest / MathF.Sqrt(d); }
                }
                float distance = MathF.Sqrt(squared) * (inside ? -1 : 1);
                if (distance >= best) continue;
                best = distance; normal = !face && inside && squared > 1e-16f ? -outward : outward; shape = id;
            }
        }
        public float SkyExposure(Vec2 point, Span<Vec2> gaps) {
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) throw new ArgumentOutOfRangeException(nameof(point));
            if (gaps.Length < polygons.Count + 1) throw new ArgumentException("Sky interval workspace is too small", nameof(gaps));
            Seal(); int count = 1; gaps[0] = new Vec2(-1, 1);
            if (used > 0) Occlude(0, point, gaps, ref count);
            float angle = 0;
            for (int i = 0; i < count; i++) angle += MathF.Atan2(gaps[i].Y, 1 - MathF.Abs(gaps[i].Y)) - MathF.Atan2(gaps[i].X, 1 - MathF.Abs(gaps[i].X));
            return Math.Clamp(angle / MathF.PI, 0, 1);
        }
        private static float Project(float x, float y) { float denominator = MathF.Abs(x) + y; return denominator > 0 ? x / denominator : 0; }
        private static void AngularBounds(in WeatherRect bounds, Vec2 point, out float low, out float high) {
            float y0 = MathF.Max(0, bounds.Bottom - point.Y), y1 = bounds.Top - point.Y;
            float x0 = bounds.Left - point.X, x1 = bounds.Right - point.X;
            low = Project(x0, x0 >= 0 ? y1 : y0);
            high = Project(x1, x1 <= 0 ? y1 : y0);
        }
        private static bool Visible(Span<Vec2> gaps, int count, float low, float high) {
            for (int i = 0; i < count; i++) if (gaps[i].Y > low && gaps[i].X < high) return true;
            return false;
        }
        private void Occlude(int index, Vec2 point, Span<Vec2> gaps, ref int count) {
            ref readonly Node node = ref nodes[index];
            if (count == 0 || node.Bounds.Top <= point.Y) return;
            AngularBounds(node.Bounds, point, out float low, out float high);
            if (!Visible(gaps, count, low, high)) return;
            if (node.Count == 0) {
                int first = node.Left, second = node.Right;
                if (BoundsDistance(nodes[first].Bounds, point) > BoundsDistance(nodes[second].Bounds, point)) { int swap = first; first = second; second = swap; }
                Occlude(first, point, gaps, ref count); Occlude(second, point, gaps, ref count); return;
            }
            for (int k = node.First; k < node.First + node.Count && count > 0; k++) {
                ref readonly Polygon polygon = ref CollectionsMarshal.AsSpan(polygons)[order[k]];
                if (!polygon.Enabled || polygon.Bounds.Top <= point.Y) continue;
                if (polygon.Rectangle) AngularBounds(polygon.Bounds, point, out low, out high);
                else {
                    low = 1; high = -1;
                    for (int i = 0; i < polygon.Count; i++) {
                        Vec2 p = vertices[polygon.First + i] - point, q = vertices[polygon.First + (i + 1) % polygon.Count] - point;
                        if (p.Y >= 0) { float value = Project(p.X, p.Y); low = MathF.Min(low, value); high = MathF.Max(high, value); }
                        if (p.Y < 0 != q.Y < 0) {
                            double cross = p.X + ((double)q.X - p.X) * (-p.Y / ((double)q.Y - p.Y));
                            float value = cross < 0 ? -1 : cross > 0 ? 1 : 0;
                            low = MathF.Min(low, value); high = MathF.Max(high, value);
                        }
                    }
                }
                if (high <= low) continue;
                for (int i = 0; i < count; i++) {
                    Vec2 gap = gaps[i];
                    if (gap.Y <= low || gap.X >= high) continue;
                    if (low <= gap.X && high >= gap.Y) { gaps.Slice(i + 1, count - i - 1).CopyTo(gaps.Slice(i)); count--; i--; }
                    else if (low <= gap.X) gaps[i] = new Vec2(high, gap.Y);
                    else if (high >= gap.Y) gaps[i] = new Vec2(gap.X, low);
                    else { gaps.Slice(i + 1, count - i - 1).CopyTo(gaps.Slice(i + 2)); gaps[i] = new Vec2(gap.X, low); gaps[i + 1] = new Vec2(high, gap.Y); count++; i++; }
                }
            }
        }
        public int Overlapping(in WeatherRect bounds, Span<int> shapes) {
            if (shapes.Length < polygons.Count) throw new ArgumentException("Polygon query workspace is too small", nameof(shapes));
            Seal(); int count = 0; if (used > 0) OverlapNode(0, bounds, shapes, ref count); return count;
        }
        private void OverlapNode(int index, in WeatherRect bounds, Span<int> shapes, ref int count) {
            ref readonly Node node = ref nodes[index]; if (!node.Bounds.Overlaps(bounds)) return;
            if (node.Count == 0) { OverlapNode(node.Left, bounds, shapes, ref count); OverlapNode(node.Right, bounds, shapes, ref count); return; }
            for (int k = node.First; k < node.First + node.Count; k++) { int id = order[k]; if (polygons[id].Enabled && polygons[id].Bounds.Overlaps(bounds)) shapes[count++] = id; }
        }
        public bool SegmentClear(Vec2 a, Vec2 b, bool includeGates = true) {
            Seal(); return used == 0 || ProvablyClear(a, b) || ClearNode(0, a, b - a, includeGates);
        }
        private static bool Intersects(in WeatherRect bounds, Vec2 origin, Vec2 delta) {
            float lo = 0, hi = 1;
            return Clip(origin.X, delta.X, bounds.Left, bounds.Right, ref lo, ref hi) && Clip(origin.Y, delta.Y, bounds.Bottom, bounds.Top, ref lo, ref hi);
        }
        private static bool Clip(float origin, float delta, float low, float high, ref float enter, ref float exit) {
            if (MathF.Abs(delta) < 1e-12f) return origin >= low && origin <= high;
            float a = (low - origin) / delta, b = (high - origin) / delta;
            if (a > b) { float swap = a; a = b; b = swap; }
            enter = MathF.Max(enter, a); exit = MathF.Min(exit, b); return enter <= exit;
        }
        private bool ClearNode(int index, Vec2 origin, Vec2 delta, bool includeGates) {
            ref readonly Node node = ref nodes[index]; if (!Intersects(node.Bounds, origin, delta)) return true;
            if (node.Count == 0) return ClearNode(node.Left, origin, delta, includeGates) && ClearNode(node.Right, origin, delta, includeGates);
            for (int k = node.First; k < node.First + node.Count; k++) {
                Polygon polygon = polygons[order[k]]; if (!polygon.Enabled || !includeGates && polygon.Gate || !Intersects(polygon.Bounds, origin, delta)) continue;
                float enter = 0, exit = 1; bool hit = true;
                for (int j = 0; j < polygon.Count; j++) {
                    ref readonly Edge edge = ref CollectionsMarshal.AsSpan(edges)[polygon.First + j];
                    Vec2 outward = edge.Normal;
                    float distance = Vec2.Dot(origin - edge.A, outward), rate = Vec2.Dot(delta, outward);
                    if (MathF.Abs(rate) < 1e-12f) { if (distance > 0) { hit = false; break; } continue; }
                    float t = -distance / rate;
                    if (rate < 0) enter = MathF.Max(enter, t); else exit = MathF.Min(exit, t);
                    if (enter > exit) { hit = false; break; }
                }
                if (hit && exit > 1e-6f && enter < 1 - 1e-6f) return false;
            }
            return true;
        }
    }
}
