using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ScpItdMod.Core;

namespace ScpItdMod.Weather {
    public readonly struct WeatherRect {
        public readonly float Left, Bottom, Right, Top;
        public WeatherRect(float left, float bottom, float right, float top) { Left = left; Bottom = bottom; Right = right; Top = top; }
        public bool Contains(float x, float y) => x >= Left && x <= Right && y >= Bottom && y <= Top;
        public bool Overlaps(in WeatherRect other) => Left < other.Right && other.Left < Right && Bottom < other.Top && other.Bottom < Top;
        public WeatherRect Intersect(in WeatherRect other) => new WeatherRect(MathF.Max(Left, other.Left), MathF.Max(Bottom, other.Bottom), MathF.Min(Right, other.Right), MathF.Min(Top, other.Top));
    }

    public sealed class WeatherGrid {
        private const byte Solid = 1, Hull = 2, Sky = 4, Gate = 8, GateOpen = 16, Fading = 32, Exterior = 64;
        public const int DistanceUnits = 5, Far = 255;
        private const int Orthogonal = 5, Diagonal = 7;
        private readonly byte[] flags, distance, reach, faceLeft, faceRight;
        private readonly float[] solidFill, solidBase;
        private readonly float[] columnCeiling;
        private readonly float[] lut = new float[Far + 1];
        private SkyVisibility skyVisibility;
        public WeatherGeometry Geometry { get; } = new WeatherGeometry();
        public WeatherGeometry LightGeometry { get; } = new WeatherGeometry();
        private int[] gateShapes = new int[16];
        private int[] gateLightShapes = new int[16];
        private float[] surfaceHeight = Array.Empty<float>(), surfaceCeiling = Array.Empty<float>(), surfaceBottom = Array.Empty<float>();
        private int[] surfaces = Array.Empty<int>(), columnSlots = Array.Empty<int>();
        private readonly int[] surfaceSlots, surfaceRows, topSurfaces, columnStarts;
        private readonly int[][] buckets = new int[Far + 1][];
        private readonly int[] bucketCount = new int[Far + 1];
        private int[] gateCells = new int[256], gateStart = new int[16], touched = new int[1024];
        private WeatherRect[] gateRects = new WeatherRect[16];
        private int gateCellCount, gateCount, touchedCount;
        private bool gatesDirty;
        private int[] fadeCell = new int[256];
        private float[] fadeValue = new float[256];
        private readonly Dictionary<int, int> fadeIndex = new Dictionary<int, int>();
        private int fadeCount;
        public const float DoorTravelSeconds = .6f;
        public float DoorSeconds { get; set; }
        public int Easing => fadeCount;
        private float penetration = -1;
        public int Width { get; }
        public int Height { get; }
        public float Cell { get; }
        public Vec2 Origin { get; }
        public WeatherRect Bounds { get; }
        public int Cells => flags.Length;
        public int SurfaceCount { get; private set; }
        public int LightRevision { get; private set; }
        public int Gates => gateCount;
        public float Penetration => penetration;
        public static float PenetrationLength(float wind) => Math.Clamp(.6f + .2f * MathF.Abs(wind), .6f, 6f);
        public static float FitCell(WeatherRect bounds, float cell, int maximumCells) {
            if (!float.IsFinite(cell) || cell <= 0) return cell;
            for (int attempt = 0; attempt < 8; attempt++) {
                double width = Math.Ceiling((bounds.Right - Math.Floor(bounds.Left / cell) * cell) / cell) + 1, height = Math.Ceiling((bounds.Top - Math.Floor(bounds.Bottom / cell) * cell) / cell) + 1;
                double cells = width * height;
                if (cells <= maximumCells * .98) return cell;
                cell *= (float)Math.Sqrt(cells / (maximumCells * .98)) * 1.01f;
            }
            return cell;
        }
        public WeatherGrid(WeatherRect bounds, float cell, int maximumCells) {
            if (!float.IsFinite(cell) || cell <= 0 || !float.IsFinite(bounds.Right - bounds.Left) || !float.IsFinite(bounds.Top - bounds.Bottom)) throw new ArgumentException("Invalid weather grid bounds");
            Bounds = bounds;
            Cell = cell; Origin = new Vec2(MathF.Floor(bounds.Left / cell) * cell, MathF.Floor(bounds.Bottom / cell) * cell);
            Width = checked((int)MathF.Ceiling((bounds.Right - Origin.X) / cell)); Height = checked((int)MathF.Ceiling((bounds.Top - Origin.Y) / cell));
            long cells = (long)Width * Height;
            if (Width <= 0 || Height <= 0 || cells > maximumCells) throw new InvalidOperationException("Weather geometry exceeds configured cell budget: " + cells);
            flags = new byte[(int)cells]; faceLeft = new byte[(int)cells]; faceRight = new byte[(int)cells]; distance = new byte[(int)cells]; reach = new byte[(int)cells]; surfaceSlots = new int[(int)cells]; solidFill = new float[(int)cells]; solidBase = new float[(int)cells];
            Array.Fill(solidBase, cell); Array.Fill(faceLeft, (byte)255); Array.Fill(distance, (byte)Far); Array.Fill(reach, (byte)Far);
            surfaceRows = new int[Height + 1];
            topSurfaces = new int[Width]; Array.Fill(topSurfaces, -1);
            columnStarts = new int[Width + 1];
            columnCeiling = new float[Width];
            Array.Fill(surfaceSlots, -1);
            for (int i = 0; i < buckets.Length; i++) buckets[i] = new int[64];
            SetPenetration(PenetrationLength(0));

        }
        public void ClearGeometry() {
            Geometry.Clear(); LightGeometry.Clear(); LightRevision++;
            Array.Clear(flags); Array.Fill(faceLeft, (byte)255); Array.Clear(faceRight); Array.Fill(distance, (byte)Far); Array.Fill(reach, (byte)Far); Array.Clear(solidFill); Array.Fill(solidBase, Cell); Array.Fill(surfaceSlots, -1);
            Array.Clear(surfaceRows); Array.Clear(columnStarts); Array.Fill(topSurfaces, -1); SurfaceCount = gateCellCount = gateCount = touchedCount = 0; gatesDirty = false; fadeCount = 0; fadeIndex.Clear();
        }
        public void Paint(in WeatherRect r, bool solid, bool interior, bool clearSolid = false) {
            if (clearSolid) { Geometry.Remove(r); LightGeometry.Remove(r); }
            if (solid) { Geometry.Add(r); LightGeometry.Add(r); }
            LightRevision++;
            int x0 = Math.Max(0, (int)MathF.Floor((r.Left - Origin.X) / Cell)), y0 = Math.Max(0, (int)MathF.Floor((r.Bottom - Origin.Y) / Cell));
            int x1 = Math.Min(Width - 1, (int)MathF.Ceiling((r.Right - Origin.X) / Cell) - 1), y1 = Math.Min(Height - 1, (int)MathF.Ceiling((r.Top - Origin.Y) / Cell) - 1);
            byte set = (byte)((solid ? Solid : 0) | (interior ? Hull : 0));
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) {
                int at = y * Width + x;
                flags[at] = (byte)((clearSolid ? flags[at] & ~Solid : flags[at]) | set);
                if (clearSolid) { solidFill[at] = 0; solidBase[at] = Cell; }
                if (!solid) continue;
                Cover(at, r.Left, r.Right);
                float cellBottom = Origin.Y + y * Cell, covered = Math.Clamp(r.Top - cellBottom, 0, Cell);
                if (clearSolid) solidFill[at] = covered; else if (covered > solidFill[at]) solidFill[at] = covered;
                solidBase[at] = MathF.Min(solidBase[at], Math.Clamp(r.Bottom - cellBottom, 0, Cell));
            }
        }
        private void Cover(int at, float left, float right) {
            float origin = Origin.X + (at % Width) * Cell;
            int l = (int)MathF.Floor(Math.Clamp((left - origin) / Cell, 0, 1) * 255), r = (int)MathF.Ceiling(Math.Clamp((right - origin) / Cell, 0, 1) * 255);
            if (r <= l) return;
            if (l < faceLeft[at]) faceLeft[at] = (byte)l;
            if (r > faceRight[at]) faceRight[at] = (byte)r;
        }
        public float Face(int at, bool right) {
            if ((flags[at] & Gate) != 0)
                for (int g = 0; g < gateCount; g++) for (int i = gateStart[g]; i < gateStart[g + 1]; i++) if (gateCells[i] == at) return right ? gateRects[g].Right : gateRects[g].Left;
            float origin = Origin.X + (at % Width) * Cell;
            if (faceRight[at] <= faceLeft[at]) return right ? origin + Cell : origin;
            return origin + (right ? faceRight[at] : faceLeft[at]) * (Cell / 255);
        }
        private static bool Across(ReadOnlySpan<Vec2> polygon, float y, out float left, out float right) {
            left = float.PositiveInfinity; right = float.NegativeInfinity;
            for (int i = 0; i < polygon.Length; i++) {
                Vec2 a = polygon[i], b = polygon[i + 1 == polygon.Length ? 0 : i + 1];
                if ((a.Y > y) == (b.Y > y)) continue;
                float x = a.X + (b.X - a.X) * ((y - a.Y) / (b.Y - a.Y));
                left = MathF.Min(left, x); right = MathF.Max(right, x);
            }
            return right > left;
        }
        private static void Span(ReadOnlySpan<Vec2> polygon, float x0, float x1, out float low, out float high) {
            low = float.PositiveInfinity; high = float.NegativeInfinity;
            for (int i = 0; i < polygon.Length; i++) {
                Vec2 a = polygon[i], b = polygon[i + 1 == polygon.Length ? 0 : i + 1];
                float left = MathF.Max(x0, MathF.Min(a.X, b.X)), right = MathF.Min(x1, MathF.Max(a.X, b.X));
                if (left > right) continue;
                if (a.X == b.X) { low = MathF.Min(low, MathF.Min(a.Y, b.Y)); high = MathF.Max(high, MathF.Max(a.Y, b.Y)); continue; }
                float ya = a.Y + (b.Y - a.Y) * ((left - a.X) / (b.X - a.X)), yb = a.Y + (b.Y - a.Y) * ((right - a.X) / (b.X - a.X));
                low = MathF.Min(low, MathF.Min(ya, yb)); high = MathF.Max(high, MathF.Max(ya, yb));
            }
        }
        public void PaintConvex(ReadOnlySpan<Vec2> polygon, bool castsShadow = true, bool frontSurface = false) {
            LightRevision++;
            if (polygon.Length < 3) return;
            Geometry.Add(polygon);
            if (castsShadow) LightGeometry.Add(polygon, frontSurface);
            float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
            for (int i = 0; i < polygon.Length; i++) { minX = MathF.Min(minX, polygon[i].X); maxX = MathF.Max(maxX, polygon[i].X); }
            int first = Math.Max(0, (int)MathF.Floor((minX - Origin.X) / Cell)), last = Math.Min(Width - 1, (int)MathF.Floor((maxX - Origin.X) / Cell));
            for (int x = first; x <= last; x++) {
                float left = Origin.X + x * Cell, cx = left + .5f * Cell;
                Span(polygon, cx, cx, out float low, out float high);
                if (!(high > low)) {
                    Span(polygon, left, left + Cell, out low, out high);
                    if (!(high > low)) continue;
                }
                int y0 = Math.Max(0, (int)MathF.Floor((low - Origin.Y) / Cell)), y1 = Math.Min(Height - 1, (int)MathF.Ceiling((high - Origin.Y) / Cell) - 1);
                for (int y = y0; y <= y1; y++) {
                    int at = y * Width + x; flags[at] |= Solid;
                    if (Across(polygon, Origin.Y + (y + .5f) * Cell, out float faceA, out float faceB)) Cover(at, faceA, faceB); else Cover(at, MathF.Max(left, minX), MathF.Min(left + Cell, maxX));
                    float covered = y == y1 ? Math.Clamp(high - (Origin.Y + y * Cell), 0, Cell) : Cell;
                    if (covered > solidFill[at]) solidFill[at] = covered;
                    solidBase[at] = MathF.Min(solidBase[at], Math.Clamp(low - (Origin.Y + y * Cell), 0, Cell));
                }
            }
        }
        public int AddGate(in WeatherRect r) {
            int x0 = Math.Max(0, (int)MathF.Ceiling((r.Left - Origin.X) / Cell - .5f)), y0 = Math.Max(0, (int)MathF.Floor((r.Bottom - Origin.Y) / Cell));
            int x1 = Math.Min(Width - 1, (int)MathF.Floor((r.Right - Origin.X) / Cell - .5f)), y1 = Math.Min(Height - 1, (int)MathF.Ceiling((r.Top - Origin.Y) / Cell) - 1);
            if (x1 < x0) x0 = x1 = Math.Clamp((int)MathF.Floor(((r.Left + r.Right) * .5f - Origin.X) / Cell), 0, Width - 1);
            if (gateCount + 2 > gateStart.Length) { Array.Resize(ref gateStart, gateStart.Length * 2); Array.Resize(ref gateRects, gateStart.Length); }
            if (gateShapes.Length <= gateCount) { Array.Resize(ref gateShapes, gateShapes.Length * 2); Array.Resize(ref gateLightShapes, gateShapes.Length); }
            gateShapes[gateCount] = Geometry.AddGate(r);
            gateLightShapes[gateCount] = LightGeometry.AddGate(r);
            gateRects[gateCount] = r;
            gateStart[gateCount] = gateCellCount;
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) {
                int at = y * Width + x; if ((flags[at] & (Solid | Gate)) != 0) continue;
                if (gateCellCount == gateCells.Length) Array.Resize(ref gateCells, gateCells.Length * 2);
                flags[at] |= Gate; gateCells[gateCellCount++] = at;
            }
            gateStart[++gateCount] = gateCellCount;
            return gateCount - 1;
        }
        public bool GateOpened(int gate) => gateStart[gate] < gateStart[gate + 1] && (flags[gateCells[gateStart[gate]]] & GateOpen) != 0;
        public void SetGate(int gate, bool open) {
            if ((uint)gate >= (uint)gateCount) throw new ArgumentOutOfRangeException(nameof(gate));
            if (GateOpened(gate) == open) return;
            Geometry.Enable(gateShapes[gate], !open);
            LightGeometry.Enable(gateLightShapes[gate], !open);
            for (int i = gateStart[gate]; i < gateStart[gate + 1]; i++) { int at = gateCells[i]; flags[at] = (byte)(open ? flags[at] | GateOpen : flags[at] & ~GateOpen); }
            gatesDirty = true;
        }
        public bool Blocked(int at) => at >= 0 && (flags[at] & (Gate | GateOpen)) == Gate;
        public bool Gated(int at) => at >= 0 && (flags[at] & Gate) != 0;
        public bool OpenGate(int at) => at >= 0 && (flags[at] & (Gate | GateOpen)) == (Gate | GateOpen);
        public bool Wall(int at) => at >= 0 && ((flags[at] & Solid) != 0 || (flags[at] & (Gate | GateOpen)) == Gate);
        public void SetPenetration(float metres) {
            if (!float.IsFinite(metres) || metres <= 0) throw new ArgumentOutOfRangeException(nameof(metres));
            if (MathF.Abs(metres - penetration) <= penetration * .01f) return;
            penetration = metres;
            Fill(lut, metres);
        }
        private void Fill(float[] table, float metres) {
            float unit = Cell / DistanceUnits;
            for (int d = 0; d < Far; d++) { float t = d / (float)(Far - 1); table[d] = MathF.Exp(-d * unit / metres) * (1 - WeatherNoise.Smooth((t - .75f) / .25f)); }
            table[Far] = 0;
        }
        public void Seal() {
            Geometry.Seal(); LightGeometry.Seal(); LightRevision++;
            int count = 0;
            Array.Clear(topSurfaces);
            for (int y = Height - 1; y >= 0; y--) {
                for (int x = 0; x < Width; x++) {
                    int at = y * Width + x; byte f = (byte)(flags[at] & ~Sky);
                    if ((f & Solid) != 0) topSurfaces[x] = 1;
                    if (topSurfaces[x] == 0) f |= Sky;
                    flags[at] = f;
                    if (y > 0 && (f & Solid) == 0 && (flags[at - Width] & Solid) != 0) count++;
                }
            }
            if (surfaces.Length < count) {
                surfaces = new int[count]; columnSlots = new int[count]; surfaceHeight = new float[count]; surfaceCeiling = new float[count]; surfaceBottom = new float[count];
            }
            SurfaceCount = 0; Array.Fill(surfaceSlots, -1); Array.Clear(columnStarts);
            for (int y = 0; y < Height; y++) {
                surfaceRows[y] = SurfaceCount;
                if (y == 0) continue;
                for (int x = 0; x < Width; x++) {
                    int at = y * Width + x;
                    if ((flags[at] & Solid) != 0 || (flags[at - Width] & Solid) == 0) continue;
                    surfaceHeight[SurfaceCount] = Origin.Y + (y - 1) * Cell + (solidFill[at - Width] > 0 ? solidFill[at - Width] : Cell);
                    columnStarts[x + 1]++; surfaces[SurfaceCount] = at; surfaceSlots[at] = SurfaceCount++;
                }
            }
            surfaceRows[Height] = SurfaceCount;
            for (int x = 0; x < Width; x++) { columnStarts[x + 1] += columnStarts[x]; topSurfaces[x] = columnStarts[x]; }
            for (int i = 0; i < SurfaceCount; i++) columnSlots[topSurfaces[surfaces[i] % Width]++] = i;
            Array.Fill(topSurfaces, -1); Array.Fill(columnCeiling, float.PositiveInfinity);
            for (int y = Height - 1; y >= 0; y--) {
                for (int x = 0; x < Width; x++) {
                    int at = y * Width + x; if (!IsSolid(at)) continue;
                    int slot = y + 1 < Height ? surfaceSlots[at + Width] : -1;
                    if (slot >= 0) { topSurfaces[x] = slot; surfaceCeiling[slot] = columnCeiling[x]; }
                    if (y > 0 && IsSolid(at - Width)) continue;
                    float bottom = Origin.Y + y * Cell + solidBase[at];
                    if (topSurfaces[x] >= 0) surfaceBottom[topSurfaces[x]] = bottom;
                    columnCeiling[x] = bottom; topSurfaces[x] = -1;
                }
            }
            for (int x = 0; x < Width; x++) topSurfaces[x] = columnStarts[x] < columnStarts[x + 1] ? columnSlots[columnStarts[x + 1] - 1] : -1;
            ClassifyRooms();
            Array.Fill(distance, (byte)Far);
            for (int at = 0; at < flags.Length; at++) {
                if ((flags[at] & (Sky | Solid | Gate)) != Sky) continue;
                distance[at] = 0;
                if (BordersShade(at)) Push(at, 0);
            }
            Flood(distance, false);
            ClearReach(); gatesDirty = true; RefreshGates(); DropFades();
        }
        private void ClassifyRooms() {
            for (int i = 0; i < flags.Length; i++) flags[i] &= unchecked((byte)~Exterior);
            int[] queue = System.Buffers.ArrayPool<int>.Shared.Rent(flags.Length);
            try {
                int read = 0, written = 0;
                for (int x = 0; x < Width; x++) { EnqueueExterior(x, queue, ref written); EnqueueExterior((Height - 1) * Width + x, queue, ref written); }
                for (int y = 1; y + 1 < Height; y++) { EnqueueExterior(y * Width, queue, ref written); EnqueueExterior(y * Width + Width - 1, queue, ref written); }
                while (read < written) {
                    int at = queue[read++], x = at % Width;
                    if (x > 0) EnqueueExterior(at - 1, queue, ref written);
                    if (x + 1 < Width) EnqueueExterior(at + 1, queue, ref written);
                    if (at >= Width) EnqueueExterior(at - Width, queue, ref written);
                    if (at + Width < flags.Length) EnqueueExterior(at + Width, queue, ref written);
                }
            } finally { System.Buffers.ArrayPool<int>.Shared.Return(queue); }
        }
        private void EnqueueExterior(int at, int[] queue, ref int written) {
            if ((flags[at] & (Solid | Gate | Exterior)) != 0) return;
            flags[at] |= Exterior; queue[written++] = at;
        }
        private bool BordersShade(int at) {
            int x = at % Width, y = at / Width;
            for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) {
                int nx = x + dx, ny = y + dy; if ((uint)nx >= (uint)Width || (uint)ny >= (uint)Height) continue;
                if ((flags[ny * Width + nx] & (Sky | Solid)) == 0) return true;
            }
            return false;
        }
        private void Push(int at, int d) {
            ref int[] bucket = ref buckets[d];
            if (bucketCount[d] == bucket.Length) Array.Resize(ref bucket, bucket.Length * 2);
            bucket[bucketCount[d]++] = at;
        }
        private bool Passable(int at, bool overlay) {
            byte f = flags[at];
            if ((f & Solid) != 0) return false;
            return (f & Gate) == 0 || overlay && (f & GateOpen) != 0;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int Effective(int at) => Math.Min(distance[at], reach[at]);
        private void Flood(byte[] target, bool overlay) {
            for (int d = 0; d < Far; d++) {
                for (int k = 0; k < bucketCount[d]; k++) {
                    int at = buckets[d][k];
                    if ((overlay ? Effective(at) : target[at]) != d) continue;
                    int x = at % Width, y = at / Width;
                    for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) {
                        if (dx == 0 && dy == 0) continue;
                        int nx = x + dx, ny = y + dy; if ((uint)nx >= (uint)Width || (uint)ny >= (uint)Height) continue;
                        int n = ny * Width + nx; if (!Passable(n, overlay)) continue;
                        if (dx != 0 && dy != 0 && (!Passable(y * Width + nx, overlay) || !Passable(ny * Width + x, overlay))) continue;
                        int nd = d + (dx != 0 && dy != 0 ? Diagonal : Orthogonal); if (nd >= Far) continue;
                        if (overlay) {
                            if (nd >= Effective(n)) continue;
                            if (reach[n] == Far) { if (touchedCount == touched.Length) Array.Resize(ref touched, touched.Length * 2); touched[touchedCount++] = n; }
                            reach[n] = (byte)nd;
                        } else {
                            if (nd >= target[n]) continue;
                            target[n] = (byte)nd;
                        }
                        Push(n, nd);
                    }
                }
                bucketCount[d] = 0;
            }
        }
        private void ClearReach() { for (int i = 0; i < touchedCount; i++) reach[touched[i]] = Far; touchedCount = 0; }
        public bool RefreshGates() {
            if (!gatesDirty) return false;
            gatesDirty = false; LightRevision++;
            bool ease = DoorSeconds > 0;
            if (ease) for (int i = 0; i < touchedCount; i++) Hold(touched[i], lut[Effective(touched[i])]);
            ClearReach();
            bool any = false;
            for (int g = 0; g < gateCount; g++) {
                if (!GateOpened(g)) continue;
                for (int i = gateStart[g]; i < gateStart[g + 1]; i++) {
                    int at = gateCells[i], x = at % Width, y = at / Width;
                    for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) {
                        int nx = x + dx, ny = y + dy; if ((uint)nx >= (uint)Width || (uint)ny >= (uint)Height) continue;
                        int n = ny * Width + nx; if (!Passable(n, false) || distance[n] >= Far) continue;
                        Push(n, distance[n]); any = true;
                    }
                }
            }
            if (any) Flood(reach, true);
            if (ease) for (int i = 0; i < touchedCount; i++) Hold(touched[i], lut[distance[touched[i]]]);
            return true;
        }
        private void Hold(int at, float shown) {
            if ((flags[at] & Fading) != 0) return;
            if (fadeCount == fadeCell.Length) { Array.Resize(ref fadeCell, fadeCount * 2); Array.Resize(ref fadeValue, fadeCount * 2); }
            fadeCell[fadeCount] = at; fadeValue[fadeCount] = shown; fadeIndex[at] = fadeCount++; flags[at] |= Fading;
        }
        private void DropFades() {
            for (int i = 0; i < fadeCount; i++) flags[fadeCell[i]] &= unchecked((byte)~Fading);
            fadeCount = 0; fadeIndex.Clear();
        }
        public bool Settle(float dt) {
            if (fadeCount == 0) return false;
            float k = DoorSeconds > 0 ? 1 - MathF.Exp(-Math.Max(0, dt) / DoorSeconds) : 1;
            for (int i = 0; i < fadeCount;) {
                int at = fadeCell[i]; float target = lut[Effective(at)], v = fadeValue[i] + (target - fadeValue[i]) * k;
                if (MathF.Abs(target - v) > 1f / 512) { fadeValue[i++] = v; continue; }
                flags[at] &= unchecked((byte)~Fading); fadeIndex.Remove(at);
                int last = --fadeCount;
                if (i != last) { fadeCell[i] = fadeCell[last]; fadeValue[i] = fadeValue[last]; fadeIndex[fadeCell[i]] = i; }
            }
            return fadeCount > 0;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Index(float x, float y) {
            int ix = (int)MathF.Floor((x - Origin.X) / Cell), iy = (int)MathF.Floor((y - Origin.Y) / Cell);
            return (uint)ix < (uint)Width && (uint)iy < (uint)Height ? iy * Width + ix : -1;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsSolid(int at) => at >= 0 && (flags[at] & Solid) != 0;
        public bool IsInterior(int at) => at >= 0 && (flags[at] & (Solid | Hull | Exterior)) == Hull;
        public bool OpenSky(int at) => at < 0 || (flags[at] & (Solid | Sky)) == Sky;
        public float SkyDistance(int at) => at < 0 || (flags[at] & Solid) != 0 || Effective(at) >= Far ? float.PositiveInfinity : Effective(at) * Cell / DistanceUnits;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Exposure(int at) => at < 0 || (flags[at] & (Solid | Gate)) != 0 ? 0 : lut[Effective(at)];
        public float ExposureShown(int at) => Level(at, true);
        private float Level(int at, bool shown) {
            if (at < 0 || (flags[at] & (Solid | Gate)) != 0) return 0;
            return shown && (flags[at] & Fading) != 0 ? fadeValue[fadeIndex[at]] : lut[Effective(at)];
        }
        public float AirExposure(int at) => Air(at, false);
        private float Air(int at, bool shown) {
            if (at < 0) return 0;
            if ((flags[at] & Solid) == 0) return shown && (flags[at] & Fading) != 0 ? fadeValue[fadeIndex[at]] : lut[Effective(at)];
            int x = at % Width; float best = 0;
            if (x > 0) best = MathF.Max(best, Level(at - 1, shown));
            if (x + 1 < Width) best = MathF.Max(best, Level(at + 1, shown));
            if (at >= Width) best = MathF.Max(best, Level(at - Width, shown));
            if (at + Width < flags.Length) best = MathF.Max(best, Level(at + Width, shown));
            return best;
        }
        public float ExposureSmooth(float x, float y) => Smooth(x, y, false);
        public float ExposureSmoothShown(float x, float y) => Smooth(x, y, true);
        internal float ShownAir(int x, int y) => AirAt(true, x, y);
        private float Smooth(float x, float y, bool shown) {
            float u = (x - Origin.X) / Cell, v = (y - Origin.Y) / Cell;
            int ix = (int)MathF.Floor(u), iy = (int)MathF.Floor(v); float fx = u - ix - .5f, fy = v - iy - .5f;
            float x0 = .5f * (.5f - fx) * (.5f - fx), x2 = .5f * (.5f + fx) * (.5f + fx), x1 = 1 - x0 - x2;
            float y0 = .5f * (.5f - fy) * (.5f - fy), y2 = .5f * (.5f + fy) * (.5f + fy), y1 = 1 - y0 - y2;
            return y0 * (x0 * AirAt(shown, ix - 1, iy - 1) + x1 * AirAt(shown, ix, iy - 1) + x2 * AirAt(shown, ix + 1, iy - 1))
                + y1 * (x0 * AirAt(shown, ix - 1, iy) + x1 * AirAt(shown, ix, iy) + x2 * AirAt(shown, ix + 1, iy))
                + y2 * (x0 * AirAt(shown, ix - 1, iy + 1) + x1 * AirAt(shown, ix, iy + 1) + x2 * AirAt(shown, ix + 1, iy + 1));
        }
        private float AirAt(bool shown, int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height ? Air(y * Width + x, shown) : 0;
        public float ExposureAt(float x, float y) => At(x, y, false);
        public float ExposureShownAt(float x, float y) => At(x, y, true);
        public float LightExposure(int at) => (uint)at >= (uint)Cells ? 0 : (skyVisibility ??= new SkyVisibility(this)).Sample(at);
        public uint LightDirections(int at) => (uint)at >= (uint)Cells ? 0 : (skyVisibility ??= new SkyVisibility(this)).Mask(at);
        public uint LightDirectionsAt(float x, float y) => (skyVisibility ??= new SkyVisibility(this)).At(new Vec2(x, y));
        public float LightExposureAt(float x, float y) => System.Numerics.BitOperations.PopCount(LightDirectionsAt(x, y)) * (1f / SkyVisibility.Directions);
        public void PrepareSkyLight(int x0, int y0, int width, int height) => (skyVisibility ??= new SkyVisibility(this)).Prepare(x0, y0, width, height);
        public void SkyLight(int x0, int y0, int width, int height, Span<float> output) {
            if (x0 < 0 || y0 < 0 || width < 0 || height < 0 || width > Width || height > Height
                || x0 > Width - width || y0 > Height - height || output.Length < checked(width * height)) throw new ArgumentOutOfRangeException(nameof(output));
            PrepareSkyLight(x0, y0, width, height);
            int index = 0;
            for (int y = y0; y < y0 + height; y++) for (int x = x0; x < x0 + width; x++) output[index++] = LightExposure(y * Width + x);
        }
        private float At(float x, float y, bool shown) {
            int at = Index(x, y); if (at < 0) return 0;
            if ((flags[at] & Solid) == 0) return Level(at, shown);
            float local = y - (Origin.Y + (at / Width) * Cell);
            if (local > solidFill[at]) return at + Width < flags.Length ? Level(at + Width, shown) : 1f;
            if (local < solidBase[at]) return at >= Width ? Level(at - Width, shown) : 0f;
            return 0;
        }
        public float ExposureBox(float x, float y, float halfX, float halfY) {
            const int Samples = 2;
            float sum = 0;
            for (int j = 0; j < Samples; j++) for (int i = 0; i < Samples; i++)
                sum += ExposureAt(x + ((i + .5f) / Samples * 2 - 1) * halfX, y + ((j + .5f) / Samples * 2 - 1) * halfY);
            return sum * (1f / (Samples * Samples));
        }
        public const int SkylineSamples = 121;
        public float Skyline(float x, float halfWidth) {
            Span<float> heights = stackalloc float[SkylineSamples]; int count = 0;
            for (int k = 0; k < SkylineSamples; k++) {
                int column = (int)MathF.Floor((x - halfWidth + 2 * halfWidth * k / (SkylineSamples - 1) - Origin.X) / Cell);
                if ((uint)column >= (uint)Width || topSurfaces[column] < 0) continue;
                heights[count++] = surfaceHeight[topSurfaces[column]];
            }
            if (count == 0) return Origin.Y + Height * Cell * .5f;
            heights.Slice(0, count).Sort();
            return heights[count / 4];
        }
        public int SurfaceCell(int slot) => surfaces[slot];
        public int HighestSurface(int cell) => cell >= 0 ? topSurfaces[cell % Width] : -1;
        public int SurfaceStart(int row) => surfaceRows[Math.Clamp(row, 0, Height)];
        public int SurfaceStart(int row, int column) {
            int lo = SurfaceStart(row), hi = SurfaceStart(row + 1), target = row * Width + column;
            while (lo < hi) { int mid = lo + (hi - lo) / 2; if (surfaces[mid] < target) lo = mid + 1; else hi = mid; }
            return lo;
        }
        public float SurfaceY(int slot) => surfaceHeight[slot];
        public float Ceiling(int slot) => surfaceCeiling[slot];
        public float SolidBottom(int slot) => surfaceBottom[slot];
        public int ColumnStart(int column) => columnStarts[column];
        public int ColumnSlot(int index) => columnSlots[index];
        public Vec2 CellCenter(int cell) => new Vec2(Origin.X + (cell % Width + .5f) * Cell, Origin.Y + (cell / Width + .5f) * Cell);
        public Vec2 Position(int cell) => new Vec2(Origin.X + (cell % Width + .5f) * Cell, Origin.Y + (cell / Width) * Cell);
        public int SurfaceSlot(int cell) => cell >= 0 && cell < surfaceSlots.Length ? surfaceSlots[cell] : -1;
        public int Ground(float x, float y, int maximumSteps = 16) {
            int at = Index(x, y); if (at < 0 || maximumSteps <= 0) return -1;
            int column = at % Width, lo = columnStarts[column], hi = columnStarts[column + 1];
            while (lo < hi) { int mid = lo + (hi - lo) / 2; if (surfaces[columnSlots[mid]] <= at) lo = mid + 1; else hi = mid; }
            if (lo == columnStarts[column]) return -1;
            int slot = columnSlots[lo - 1];
            return (at - surfaces[slot]) / Width < maximumSteps ? slot : -1;
        }
        public float SolidTop(int cell) {
            int x = cell % Width, lo = columnStarts[x], hi = columnStarts[x + 1];
            while (lo < hi) { int mid = lo + (hi - lo) / 2; if (surfaces[columnSlots[mid]] <= cell) lo = mid + 1; else hi = mid; }
            return lo < columnStarts[x + 1] ? surfaceHeight[columnSlots[lo]] : Origin.Y + Height * Cell;
        }
        public bool SegmentClear(Vec2 a, Vec2 b) => Geometry.SegmentClear(a, b);
    }
}
