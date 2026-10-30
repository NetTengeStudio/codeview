using System;
using System.Collections.Generic;
using System.Threading;
using System.Runtime.InteropServices;
using ScpItdMod.Core;

namespace ScpItdMod.Weather {
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public readonly struct SkyTransferVertex {
        public readonly Vec2 Position;
        public readonly float Exposure;
        public SkyTransferVertex(Vec2 position, float exposure) { Position = position; Exposure = exposure; }
    }
    public sealed class SkyTransferBuilder {
        private readonly struct Piece {
            internal readonly int First, Count;
            internal Piece(int first, int count) { First = first; Count = count; }
        }
        private readonly Dictionary<Vec2, float> samples = new Dictionary<Vec2, float>(2048);
        private Vec2[] pointsA = new Vec2[128], pointsB = new Vec2[128], clipA = new Vec2[64], clipB = new Vec2[64], gaps = Array.Empty<Vec2>();
        private Piece[] piecesA = new Piece[32], piecesB = new Piece[32];
        private int[] candidates = Array.Empty<int>();
        private WeatherRect[] bounds = Array.Empty<WeatherRect>();
        private SkyTransferVertex[] vertices = new SkyTransferVertex[4096];
        private WeatherGeometry geometry;
        private const int MaximumRefinement = 4;
        private const float InterpolationTolerance = 1f / 255;
        private int written;
        internal SkyTransferVertex[] Storage => vertices;
        public ReadOnlySpan<SkyTransferVertex> Vertices => vertices.AsSpan(0, written);
        private static void Ensure<T>(ref T[] values, int count) { if (values.Length < count) Array.Resize(ref values, Math.Max(count, values.Length * 2)); }
        public ReadOnlySpan<SkyTransferVertex> Build(WeatherGeometry source, in WeatherRect area, float spacing, CancellationToken cancellation = default) {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (!float.IsFinite(spacing) || spacing <= 0 || !float.IsFinite(area.Right - area.Left) || !float.IsFinite(area.Top - area.Bottom) || !(area.Right > area.Left && area.Top > area.Bottom)) throw new ArgumentOutOfRangeException(nameof(spacing));
            geometry = source;
            try {
                written = 0; samples.Clear();
                Ensure(ref gaps, geometry.Count + 1); Ensure(ref candidates, geometry.Count); Ensure(ref bounds, geometry.Count);
                int shapes = geometry.Overlapping(area, candidates);
                for (int i = 0; i < shapes; i++) {
                    ReadOnlySpan<Vec2> polygon = geometry.PolygonVertices(candidates[i], out _);
                    float left = polygon[0].X, right = left, bottom = polygon[0].Y, top = bottom;
                    for (int k = 1; k < polygon.Length; k++) { left = MathF.Min(left, polygon[k].X); right = MathF.Max(right, polygon[k].X); bottom = MathF.Min(bottom, polygon[k].Y); top = MathF.Max(top, polygon[k].Y); }
                    bounds[i] = new WeatherRect(left, bottom, right, top);
                }
                int columns = checked((int)MathF.Ceiling((area.Right - area.Left) / spacing)), rows = checked((int)MathF.Ceiling((area.Top - area.Bottom) / spacing));
                for (int y = 0; y < rows; y++) for (int x = 0; x < columns; x++) {
                    cancellation.ThrowIfCancellationRequested();
                    var cell = new WeatherRect(area.Left + x * spacing, area.Bottom + y * spacing, MathF.Min(area.Right, area.Left + (x + 1) * spacing), MathF.Min(area.Top, area.Bottom + (y + 1) * spacing));
                    pointsA[0] = new Vec2(cell.Left, cell.Bottom); pointsA[1] = new Vec2(cell.Right, cell.Bottom); pointsA[2] = new Vec2(cell.Right, cell.Top); pointsA[3] = new Vec2(cell.Left, cell.Top);
                    piecesA[0] = new Piece(0, 4); int pieceCount = 1;
                    for (int shape = 0; shape < shapes && pieceCount > 0; shape++) {
                        if (!cell.Overlaps(bounds[shape])) continue;
                        ReadOnlySpan<Vec2> blocker = geometry.PolygonVertices(candidates[shape], out _);
                        int outputPoints = 0, outputPieces = 0;
                        for (int piece = 0; piece < pieceCount; piece++) {
                            Piece input = piecesA[piece]; Ensure(ref clipA, input.Count + blocker.Length + 1); Ensure(ref clipB, clipA.Length);
                            pointsA.AsSpan(input.First, input.Count).CopyTo(clipA); int inside = input.Count;
                            for (int edge = 0; edge < blocker.Length && inside >= 3; edge++) {
                                Vec2 a = blocker[edge], b = blocker[(edge + 1) % blocker.Length];
                                Ensure(ref pointsB, outputPoints + inside + 1);
                                int outside = Clip(clipA.AsSpan(0, inside), pointsB.AsSpan(outputPoints), a, b, false);
                                if (outside >= 3) { Ensure(ref piecesB, outputPieces + 1); piecesB[outputPieces++] = new Piece(outputPoints, outside); outputPoints += outside; }
                                inside = Clip(clipA.AsSpan(0, inside), clipB, a, b, true);
                                Vec2[] swap = clipA; clipA = clipB; clipB = swap;
                            }
                        }
                        Vec2[] pointSwap = pointsA; pointsA = pointsB; pointsB = pointSwap;
                        Piece[] pieceSwap = piecesA; piecesA = piecesB; piecesB = pieceSwap; pieceCount = outputPieces;
                    }
                    for (int piece = 0; piece < pieceCount; piece++) {
                        Piece record = piecesA[piece]; ReadOnlySpan<Vec2> polygon = pointsA.AsSpan(record.First, record.Count);
                        Vec2 centre = default;
                        for (int i = 0; i < polygon.Length; i++) centre += polygon[i] * (1f / polygon.Length);
                        SkyTransferVertex c = Sample(centre, centre);
                        for (int i = 0; i < polygon.Length; i++) Refine(Sample(polygon[i], centre), Sample(polygon[(i + 1) % polygon.Length], centre), c, 0);
                    }
                }
                return vertices.AsSpan(0, written);
            } finally { geometry = null; }
        }
        private static double Side(Vec2 p, Vec2 a, Vec2 b) => ((double)b.X - a.X) * ((double)p.Y - a.Y) - ((double)b.Y - a.Y) * ((double)p.X - a.X);
        private static int Clip(ReadOnlySpan<Vec2> input, Span<Vec2> output, Vec2 edgeA, Vec2 edgeB, bool inside) {
            int count = 0; Vec2 a = input[input.Length - 1]; double da = Side(a, edgeA, edgeB) * (inside ? 1 : -1);
            for (int i = 0; i < input.Length; i++) {
                Vec2 b = input[i]; double db = Side(b, edgeA, edgeB) * (inside ? 1 : -1);
                if (da >= 0 != db >= 0) output[count++] = a + (b - a) * (float)(da / (da - db));
                if (db >= 0) output[count++] = b;
                a = b; da = db;
            }
            return count;
        }
        private SkyTransferVertex Sample(Vec2 position, Vec2 towards) {
            float x = towards.X > position.X ? MathF.BitIncrement(position.X) : towards.X < position.X ? MathF.BitDecrement(position.X) : position.X;
            float y = towards.Y > position.Y ? MathF.BitIncrement(position.Y) : towards.Y < position.Y ? MathF.BitDecrement(position.Y) : position.Y;
            var probe = new Vec2(x, y);
            ref float exposure = ref CollectionsMarshal.GetValueRefOrAddDefault(samples, probe, out bool present);
            if (!present) exposure = geometry.SkyExposure(probe, gaps);
            return new SkyTransferVertex(position, exposure);
        }
        private void Refine(SkyTransferVertex a, SkyTransferVertex b, SkyTransferVertex c, int depth) {
            if (Side(c.Position, a.Position, b.Position) <= 0) return;
            if (depth == MaximumRefinement) { Emit(a, b, c); return; }
            Vec2 centre = a.Position / 3 + b.Position / 3 + c.Position / 3;
            SkyTransferVertex ab = Sample((a.Position + b.Position) * .5f, centre), bc = Sample((b.Position + c.Position) * .5f, centre), ca = Sample((c.Position + a.Position) * .5f, centre);
            float error = MathF.Max(MathF.Abs(ab.Exposure - (a.Exposure + b.Exposure) * .5f), MathF.Max(MathF.Abs(bc.Exposure - (b.Exposure + c.Exposure) * .5f), MathF.Abs(ca.Exposure - (c.Exposure + a.Exposure) * .5f)));
            if (error > InterpolationTolerance) {
                Refine(a, ab, ca, depth + 1); Refine(ab, b, bc, depth + 1); Refine(ca, bc, c, depth + 1); Refine(ab, bc, ca, depth + 1); return;
            }
            Emit(a, b, c);
        }
        private void Emit(SkyTransferVertex a, SkyTransferVertex b, SkyTransferVertex c) {
            if (a.Exposure == 0 && b.Exposure == 0 && c.Exposure == 0) return;
            Ensure(ref vertices, written + 3); vertices[written++] = a; vertices[written++] = b; vertices[written++] = c;
        }
    }
}
