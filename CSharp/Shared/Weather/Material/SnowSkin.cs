using System;
using System.Collections.Generic;
using ScpItdMod.Core;

namespace ScpItdMod.Weather.Material;

// The engine finds the floor under a character with a ray against static fixtures, so the snow must be present there as a polyline.
// The line is read from the particles themselves, once per sample, and carries no state: wherever the material is pressed down, pushed aside or blown away the line follows it.
internal static class SnowSkin
{
    // Samples half a particle spacing apart, each the top of the particles within a little more than half a spacing: the narrowest window that always holds a particle of the top row of the
    // hexagonal pack (a window of exactly half a spacing catches the neighbours of a row only on one side of rounding, and the line steps by a row), so a footprint keeps its width.
    internal const float Step = SnowConstitutive.Spacing * .5f, Half = SnowConstitutive.Spacing * .6f, MinimumDepth = .03f;
    // A foot walks over what is no steeper than ClimbSlope; a mound or a ridge steeper than that is not a wall to a boot, which treads it down. The floor line is therefore the highest line that rises no faster
    // than the slope and lies no more than Give below the snow: a bump of a hand's height becomes a ramp, and a step taller than Give stays a step.
    internal const float ClimbSlope = .5f, Give = .2f;
    private sealed class Run { internal readonly List<float> Top = new(); internal float First, LastX, Floor, StartFloor; internal bool Open; }
    private static readonly List<Run> runs = new();
    private static int used;
    private static Run Next(float x, float floor)
    {
        if (used == runs.Count) runs.Add(new Run());
        Run run = runs[used++]; run.Top.Clear(); run.First = x; run.LastX = x; run.Floor = floor; run.StartFloor = floor; run.Open = true; return run;
    }
    private static float[] raw = new float[256];
    private static void Climbable(List<float> top)
    {
        int n = top.Count; if (raw.Length < n) raw = new float[n * 2];
        float rise = ClimbSlope * Step;
        for (int j = 0; j < n; j++) raw[j] = top[j];
        for (int j = 1; j < n; j++) top[j] = MathF.Min(top[j], top[j - 1] + rise);
        for (int j = n - 2; j >= 0; j--) top[j] = MathF.Min(top[j], top[j + 1] + rise);
        for (int j = 0; j < n; j++) top[j] = MathF.Max(top[j], raw[j] - Give);
    }
    // Each run starts and ends at the bottom of the snow, so a deposit tapers to the ground or meets a wall.
    internal static int Build(SnowPack pack, float left, float right, List<Vec2> points, List<int> starts)
    {
        points.Clear(); starts.Clear(); used = 0;
        WeatherGrid grid = pack.Grid; SnowMpm material = pack.Material;
        int first = (int)MathF.Floor(left / Step), last = (int)MathF.Ceiling(right / Step);
        for (int i = first; i <= last; i++)
        {
            float x = i * Step; int column = (int)MathF.Floor((x - grid.Origin.X) / grid.Cell);
            if ((uint)column >= (uint)grid.Width) continue;
            for (int k = 0; k < used; k++) runs[k].Open = runs[k].LastX >= x - Step * 1.5f;
            for (int at = grid.ColumnStart(column), end = grid.ColumnStart(column + 1); at < end; at++)
            {
                int slot = grid.ColumnSlot(at), cell = grid.SurfaceCell(slot);
                if (grid.Blocked(cell) || grid.OpenGate(cell)) continue;
                float ground = grid.SurfaceY(slot) - grid.Cell, ceiling = MathF.Min(grid.Ceiling(slot), grid.SurfaceY(slot) + pack.MaximumDepth + grid.Cell);
                if (!material.Column(x, Half, ground, ceiling, out float low, out float high) || high - low < MinimumDepth) continue;
                Run target = null;
                for (int k = 0; k < used && target == null; k++)
                    if (runs[k].Open && MathF.Abs(runs[k].Floor - low) < grid.Cell * 1.5f && runs[k].Top.Count == (int)MathF.Round((x - runs[k].First) / Step)) target = runs[k];
                if (target == null) { target = Next(x, low); }
                target.Top.Add(high); target.LastX = x; target.Floor = low;
            }
        }
        for (int k = 0; k < used; k++)
        {
            Run run = runs[k]; int n = run.Top.Count; if (n == 0) continue;
            Climbable(run.Top);
            starts.Add(points.Count);
            points.Add(new Vec2(run.First - Step * .5f, run.StartFloor - Step * .5f));
            for (int j = 0; j < n; j++)
            {
                // A five-tap bell: a single stray grain does not make a step in the floor under a boot.
                float sum = 0, weight = 0;
                for (int d = -2; d <= 2; d++) { int at = j + d; if (at < 0 || at >= n) continue; float w = 3 - Math.Abs(d); sum += run.Top[at] * w; weight += w; }
                points.Add(new Vec2(run.First + j * Step, sum / weight));
            }
            points.Add(new Vec2(run.First + (n - 1) * Step + Step * .5f, run.Floor - Step * .5f));
        }
        return starts.Count;
    }
}
