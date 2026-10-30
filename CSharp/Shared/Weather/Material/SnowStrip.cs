using System;

namespace ScpItdMod.Weather.Material;

// The particles of a strip of the map, read from the material once and kept in order of x. The picture asks for the surface under many neighbouring samples that are 3 cm apart, each
// within a few centimetres of its neighbours, so answering all of them from this list reads every cell once instead of once per sample. Column and TopState give exactly the answers of
// SnowMpm.Column and SnowMpm.TopState: a particle belongs to the row of cells its position falls in, and rows are joined into one body by the same gap rule.
internal sealed class SnowStrip
{
    private struct Entry { internal double X, Y; internal float Radius, Density, Stain; internal int Row; }
    private Entry[] entries = new Entry[256]; private double[] keys = new double[256]; private int count;
    private float[] lows = new float[64], highs = new float[64];
    internal int Count => count;
    internal void Clear() => count = 0;
    internal void Add(double x, double y, float radius, float density, float stain, int row)
    {
        if (count == entries.Length) { Array.Resize(ref entries, count * 2); Array.Resize(ref keys, count * 2); }
        keys[count] = x;
        entries[count++] = new Entry { X = x, Y = y, Radius = radius, Density = density, Stain = stain, Row = row };
    }
    internal void Seal() => Array.Sort(keys, entries, 0, count);
    private int First(double x)
    {
        int low = 0, high = count;
        while (low < high) { int mid = (low + high) >> 1; if (entries[mid].X < x) low = mid + 1; else high = mid; }
        return low;
    }
    internal bool Column(float x, float half, float bottom, float ceiling, out float low, out float high)
    {
        low = float.MaxValue; high = float.MinValue;
        int y0 = SnowMpm.RowOf(bottom), rows = SnowMpm.RowOf(ceiling) - y0 + 1;
        if (rows > lows.Length) { lows = new float[rows * 2]; highs = new float[rows * 2]; }
        Array.Fill(lows, float.MaxValue, 0, rows); Array.Fill(highs, float.MinValue, 0, rows);
        for (int i = First(x - half); i < count && entries[i].X <= x + half; i++)
        {
            ref readonly Entry e = ref entries[i];
            if (Math.Abs(e.X - x) > half || e.Y < bottom || e.Y > ceiling) continue;
            int r = e.Row - y0; if ((uint)r >= (uint)rows) continue;
            lows[r] = MathF.Min(lows[r], (float)e.Y - e.Radius); highs[r] = MathF.Max(highs[r], (float)e.Y + e.Radius);
        }
        // Packed to the rows that hold snow, in order of row, with the same early exit as the material's own query.
        int n = 0; float reach = float.MinValue;
        for (int r = 0; r < rows; r++)
        {
            if (n > 0 && (y0 + r + .5f) * SnowMpm.CellSize - SnowConstitutive.Spacing > reach + SnowMpm.SurfaceGap) break;
            if (lows[r] == float.MaxValue) continue;
            lows[n] = lows[r]; highs[n] = highs[r]; reach = MathF.Max(reach, highs[n]); n++;
        }
        if (n == 0) return false;
        lows.AsSpan(0, n).Sort(highs.AsSpan(0, n));
        low = lows[0]; high = highs[0];
        for (int i = 1; i < n && lows[i] <= high + SnowMpm.SurfaceGap; i++) high = MathF.Max(high, highs[i]);
        return true;
    }
    internal float TopState(float x, float half, float bottom, float ceiling, float high, float thickness, out float stain)
    {
        double sum = 0; float marked = 0; int taken = 0;
        int y0 = SnowMpm.RowOf(MathF.Max(bottom, high - thickness - SnowMpm.CellSize)), y1 = SnowMpm.RowOf(MathF.Min(ceiling, high));
        for (int i = First(x - half); i < count && entries[i].X <= x + half; i++)
        {
            ref readonly Entry e = ref entries[i];
            if (e.Row < y0 || e.Row > y1 || Math.Abs(e.X - x) > half || e.Y < high - thickness || e.Y > ceiling) continue;
            sum += e.Density; marked = MathF.Max(marked, e.Stain); taken++;
        }
        stain = marked;
        return taken > 0 ? (float)(sum / taken) : 0;
    }
}
