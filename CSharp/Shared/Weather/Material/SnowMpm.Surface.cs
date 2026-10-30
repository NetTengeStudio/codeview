using System;
using ScpItdMod.Core;

namespace ScpItdMod.Weather.Material;

internal sealed partial class SnowMpm
{
    // Vertical extent of the snow within half a width of x, read straight from the particles: the surface a foot or a tool meets is whatever the material currently is, with no height field behind it.
    // Each particle is linked to four cells and counted once, through its first link. The surface is the top of the body of snow that is connected to the lowest particle: a flake kicked up or thrown
    // over the pack is a separate body (a gap wider than SurfaceGap below it) and does not raise the line that feet, tools and the picture stand on.
    internal const float SurfaceGap = SnowConstitutive.Spacing * 2;
    private float[] columnLow = new float[256], columnHigh = new float[256];
    internal bool Column(float x, float half, float bottom, float ceiling, out float low, out float high)
    {
        int rows = Coordinate(ceiling) - Coordinate(bottom) + 1;
        if (rows > columnLow.Length) { columnLow = new float[rows * 2]; columnHigh = new float[rows * 2]; }
        return Column(x, half, bottom, ceiling, columnLow, columnHigh, out low, out high);
    }
    // The scratch spans hold one entry per row of cells between bottom and ceiling; a caller on a helper thread brings its own, so the query reads nothing but the particles.
    internal bool Column(float x, float half, float bottom, float ceiling, Span<float> lows, Span<float> highs, out float low, out float high)
    {
        low = float.MaxValue; high = float.MinValue; int n = 0;
        int x0 = Coordinate(x - half), x1 = Coordinate(x + half), y0 = Coordinate(bottom), y1 = Coordinate(ceiling);
        float reach = float.MinValue;
        for (int cy = y0; cy <= y1; cy++)
        {
            // A particle of row cy sits at least half a cell above the row's start, less its radius: once that is past everything found plus the gap, no later row can join the body.
            if (n > 0 && (cy + .5f) * CellSize - SnowConstitutive.Spacing > reach + SurfaceGap) break;
            float rowLow = float.MaxValue, rowHigh = float.MinValue;
            for (int cx = x0; cx <= x1; cx++)
            {
                if (!cellIndex.TryGetValue(Key(cx, cy), out int c)) continue;
                for (int link = cells[c].Head; link >= 0; link = links[link].Next)
                {
                    if ((link & 3) != 0) continue;
                    ref readonly SnowPoint point = ref points[link >> 2];
                    double px = point.Position.PreciseX, py = point.Position.PreciseY;
                    if (Math.Abs(px - x) > half || py < bottom || py > ceiling) continue;
                    float radius = point.Radius;
                    rowLow = MathF.Min(rowLow, (float)py - radius);
                    rowHigh = MathF.Max(rowHigh, (float)py + radius);
                }
            }
            if (rowLow == float.MaxValue) continue;
            lows[n] = rowLow; highs[n] = rowHigh; n++; reach = MathF.Max(reach, rowHigh);
        }
        if (n == 0) return false;
        lows.Slice(0, n).Sort(highs.Slice(0, n));
        low = lows[0]; high = highs[0];
        for (int i = 1; i < n && lows[i] <= high + SurfaceGap; i++) high = MathF.Max(high, highs[i]);
        return true;
    }
    // Mean density of the particles in the top layer under x: what a boot meets first, which decides whether the surface reads as powder or crust.
    internal float TopDensity(float x, float half, float bottom, float ceiling, float high, float thickness) => TopState(x, half, bottom, ceiling, high, thickness, out _);
    internal float TopState(float x, float half, float bottom, float ceiling, float high, float thickness, out float stain)
    {
        double sum = 0; float marked = 0; int count = 0;
        int x0 = Coordinate(x - half), x1 = Coordinate(x + half), y0 = Coordinate(MathF.Max(bottom, high - thickness - CellSize)), y1 = Coordinate(MathF.Min(ceiling, high));
        for (int cy = y0; cy <= y1; cy++) for (int cx = x0; cx <= x1; cx++)
        {
            if (!cellIndex.TryGetValue(Key(cx, cy), out int c)) continue;
            for (int link = cells[c].Head; link >= 0; link = links[link].Next)
            {
                if ((link & 3) != 0) continue;
                ref readonly SnowPoint point = ref points[link >> 2];
                if (Math.Abs(point.Position.PreciseX - x) > half || point.Position.PreciseY < high - thickness || point.Position.PreciseY > ceiling) continue;
                sum += point.Density; marked = MathF.Max(marked, point.Stain); count++;
            }
        }
        stain = marked;
        return count > 0 ? (float)(sum / count) : 0;
    }
    internal static int RowOf(double y) => Coordinate(y);
    // Loads the particles inside a box into a strip, each once, by the row of cells it is counted in.
    internal void Fill(SnowStrip strip, float left, float bottom, float right, float top)
    {
        strip.Clear();
        int x0 = Coordinate(left), x1 = Coordinate(right), y0 = Coordinate(bottom), y1 = Coordinate(top);
        for (int cy = y0; cy <= y1; cy++) for (int cx = x0; cx <= x1; cx++)
        {
            if (!cellIndex.TryGetValue(Key(cx, cy), out int c)) continue;
            for (int link = cells[c].Head; link >= 0; link = links[link].Next)
            {
                if ((link & 3) != 0) continue;
                ref readonly SnowPoint point = ref points[link >> 2];
                strip.Add(point.Position.PreciseX, point.Position.PreciseY, point.Radius, point.Density, point.Stain, cy);
            }
        }
        strip.Seal();
    }
    internal ref SnowPoint Edit(int id) { Touch(points[id].Position); return ref points[id]; }
    // Particle ids of the top layer under a span, for processes that act on the surface (heat, erosion, melt). Dead slots carry no mass and are skipped.
    internal void Layer(float x, float half, float bottom, float ceiling, float thickness, System.Collections.Generic.List<int> ids)
    {
        ids.Clear(); if (!Column(x, half, bottom, ceiling, out _, out float high)) return;
        int x0 = Coordinate(x - half), x1 = Coordinate(x + half), y0 = Coordinate(MathF.Max(bottom, high - thickness - CellSize)), y1 = Coordinate(MathF.Min(ceiling, high));
        for (int cy = y0; cy <= y1; cy++) for (int cx = x0; cx <= x1; cx++)
        {
            if (!cellIndex.TryGetValue(Key(cx, cy), out int c)) continue;
            for (int link = cells[c].Head; link >= 0; link = links[link].Next)
            {
                if ((link & 3) != 0) continue;
                int p = link >> 2; ref readonly SnowPoint point = ref points[p];
                if (Math.Abs(point.Position.PreciseX - x) > half || point.Position.PreciseY < high - thickness || point.Position.PreciseY > ceiling) continue;
                ids.Add(p);
            }
        }
    }
    // Removes a particle from the material (melted away, drained). Its slot is reused by later deposits.
    internal void Remove(int id)
    {
        if (!(points[id].Mass > 0)) return;
        Touch(points[id].Position);
        Unlink(id); Sleep(id); points[id].Mass = 0; status[id].Frozen = true;
        if (freePointCount == freePoints.Length) Array.Resize(ref freePoints, freePointCount * 2);
        freePoints[freePointCount++] = id; Revision++;
    }
    // Moves a particle to a new place where it rests as loose snow that has just landed (wind transport): the caller chose a place at rest, so nothing is woken.
    internal void Relocate(int id, Vec2 position)
    {
        Touch(points[id].Position); Touch(position);
        Unlink(id); points[id].Position = position; points[id].Previous = position; points[id].Velocity = default; points[id].Affine = default;
        status[id].Clearance = 0; status[id].Sampled = default; status[id].Normal = default; status[id].Frozen = false; Insert(id); Revision++;
    }
    // Presses a particle down into the snow below it: it moves to a new height and keeps its mass in a smaller volume, so its density rises by ratio. The snow stays at rest; nothing is woken.
    internal void Compress(int id, float y, float ratio)
    {
        ref SnowPoint point = ref points[id]; if (!(point.Mass > 0)) return;
        Mark(ChunkOf(point.Position.PreciseX)); Unlink(id); Pressed?.Invoke(point.Position);
        point.Position = new SnowPosition(point.Position.PreciseX, y); point.Previous = point.Position;
        Pressed?.Invoke(point.Position);
        point.Plastic = MathF.Max(point.Plastic / ratio, point.ReferenceDensity / SnowConstitutive.MaximumDensity);
        status[id].Clearance = 0; status[id].Sampled = default; status[id].Normal = default; Insert(id); Revision++;
    }
    // Particle ids inside a box, each once; the box edges are half open so neighbouring boxes never share a particle.
    internal void Collect(float left, float bottom, float right, float top, System.Collections.Generic.List<int> ids)
    {
        ids.Clear();
        int x0 = Coordinate(left), x1 = Coordinate(right) + 1, y0 = Coordinate(bottom), y1 = Coordinate(top) + 1;
        for (int cy = y0; cy <= y1; cy++) for (int cx = x0; cx <= x1; cx++)
        {
            if (!cellIndex.TryGetValue(Key(cx, cy), out int c)) continue;
            for (int link = cells[c].Head; link >= 0; link = links[link].Next)
            {
                if ((link & 3) != 0) continue;
                int p = link >> 2; double px = points[p].Position.PreciseX, py = points[p].Position.PreciseY;
                if (px >= left && px < right && py >= bottom && py < top) ids.Add(p);
            }
        }
    }
}
