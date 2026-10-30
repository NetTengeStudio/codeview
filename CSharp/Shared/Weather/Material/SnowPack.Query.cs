using System;
using ScpItdMod.Core;

namespace ScpItdMod.Weather.Material;

// Questions the rest of the game asks of the snow, answered from the particles. A slot is a walkable surface of the weather grid; its snow is whatever particles lie in its column.
public sealed partial class SnowPack
{
    internal SnowClimate Climate { get; set; }
    // Advanced once per rendered or simulated frame: cached column depths are good for a few tenths of a second, which is all a mask or a drift test needs.
    internal uint Frame { get; set; }
    private float[] depthCache = Array.Empty<float>(); private uint[] depthStamp = Array.Empty<uint>();
    public float Ground(int slot) => Grid.SurfaceY(slot);
    // Whether the weather reaches a slot: it lies under open sky, outside every room and outside every doorway. The one answer for where snow is dealt by the sky, taken by the wind and allowed to come to rest.
    private bool[] exposed = Array.Empty<bool>();
    internal bool Exposed(int slot)
    {
        if (exposed.Length != Grid.SurfaceCount)
        {
            var table = new bool[Grid.SurfaceCount];
            for (int k = 0; k < table.Length; k++) { int cell = Grid.SurfaceCell(k); table[k] = Grid.OpenSky(cell) && !Grid.IsInterior(cell) && !Grid.Gated(cell); }
            exposed = table;
        }
        return exposed[slot];
    }
    // The same for the ground at x that lies nearest to the given height; a column with none within a cell of it (a drop, a wall) is not exposed.
    internal bool ExposedAt(float x, float ground)
    {
        int column = (int)MathF.Floor((x - Grid.Origin.X) / Grid.Cell); if ((uint)column >= (uint)Grid.Width) return false;
        int best = -1; float gap = Grid.Cell;
        for (int k = Grid.ColumnStart(column), end = Grid.ColumnStart(column + 1); k < end; k++)
        {
            int slot = Grid.ColumnSlot(k); float distance = MathF.Abs(Grid.SurfaceY(slot) - ground);
            if (distance <= gap) { gap = distance; best = slot; }
        }
        return best >= 0 && Exposed(best);
    }
    // The vertical band of a slot in which its snow lies: from a cell under its ground to the deepest snow it can hold or the ceiling above it.
    internal bool Band(int slot, out float floor, out float ceiling)
    {
        float ground = Grid.SurfaceY(slot); floor = ground - Grid.Cell; ceiling = MathF.Min(Grid.Ceiling(slot), ground + MaximumDepth + Grid.Cell); return ceiling > floor;
    }
    // Height of the snow surface over a slot at x, or the ground when nothing lies there.
    // The query uses its own scratch rows, so any number of threads may ask it at once while nothing changes the particles.
    private const int TopRows = 64;
    public float Top(int slot, float x)
    {
        if (!Band(slot, out float floor, out float ceiling)) return Grid.SurfaceY(slot);
        int rows = SnowMpm.RowOf(ceiling) - SnowMpm.RowOf(floor) + 1;
        Span<float> lows = rows <= TopRows ? stackalloc float[TopRows] : new float[rows], highs = rows <= TopRows ? stackalloc float[TopRows] : new float[rows];
        return Material.Column(x, SnowSkin.Half, floor, ceiling, lows, highs, out _, out float high) ? high : Grid.SurfaceY(slot);
    }
    public float Depth(int slot)
    {
        if (depthCache.Length < Grid.SurfaceCount) { depthCache = new float[Grid.SurfaceCount]; depthStamp = new uint[Grid.SurfaceCount]; }
        if (depthStamp[slot] != 0 && Frame + 1 - depthStamp[slot] < 15) return depthCache[slot];
        float cx = Grid.Position(Grid.SurfaceCell(slot)).X;
        float depth = Band(slot, out float floor, out float ceiling) && Material.Column(cx, Grid.Cell * .5f, floor, ceiling, out float low, out float high) ? MathF.Max(0, high - low) : 0;
        depthCache[slot] = depth; depthStamp[slot] = Frame + 1; return depth;
    }
    private readonly System.Collections.Generic.List<int> stained = new();
    // Marks the particles of the top layer around a point; a stain deepens with every drop and never washes out on its own.
    public void Stain(Vec2 point, float radius, float quantity)
    {
        int slot = FindSurface(point, Grid.Cell); if (slot < 0 || !Band(slot, out float floor, out float ceiling)) return;
        Material.Layer(point.X, radius, floor, ceiling, SnowConstitutive.Spacing, stained);
        float share = 1 - MathF.Exp(-quantity / SnowConstitutive.Spacing);
        for (int i = 0; i < stained.Count; i++) { ref SnowPoint p = ref Material.Edit(stained[i]); p.Stain = 1 - (1 - p.Stain) * (1 - share); }
    }
    // Top of the snow at x over the highest ground that lies inside reach of y, or the bare ground there.
    public bool TopUnder(float x, float y, float above, float below, out float top)
    {
        top = 0; int column = (int)MathF.Floor((x - Grid.Origin.X) / Grid.Cell);
        if ((uint)column >= (uint)Grid.Width) return false;
        int best = -1;
        for (int at = Grid.ColumnStart(column), end = Grid.ColumnStart(column + 1); at < end; at++)
        {
            int slot = Grid.ColumnSlot(at); float ground = Grid.SurfaceY(slot);
            if (ground > y + above || ground < y - below - MaximumDepth) continue;
            if (best < 0 || ground > Grid.SurfaceY(best)) best = slot;
        }
        if (best < 0) return false;
        top = Top(best, x); return true;
    }
    public float AirborneFraction(int slot) => Climate?.Airborne(slot) ?? 0;
    public int FindGround(Vec2 point, float above, float below)
    {
        int x = (int)MathF.Floor((point.X - Grid.Origin.X) / Grid.Cell);
        if ((uint)x >= (uint)Grid.Width) return -1;
        int best = -1; float gap = float.PositiveInfinity;
        for (int k = Grid.ColumnStart(x), end = Grid.ColumnStart(x + 1); k < end; k++)
        {
            int slot = Grid.ColumnSlot(k); float g = Grid.SurfaceY(slot);
            if (g > point.Y + above || g < point.Y - below) continue;
            float d = MathF.Abs(g - point.Y); if (d < gap) { gap = d; best = slot; }
        }
        return best;
    }
    // The highest the snow can be anywhere over a slot, read from the whole width of its cell and kept for TopBoundFrames passes; a point above it by more than TopBoundMargin cannot be inside any snow there.
    internal const float TopBoundMargin = .4f; private const uint TopBoundFrames = 15;
    private float[] topBoundCache = System.Array.Empty<float>(), columnTopBound = System.Array.Empty<float>(); private uint[] topBoundStamp = System.Array.Empty<uint>(); private uint topBoundTick;
    private void ComputeTopBound(int slot)
    {
        float cx = Grid.Position(Grid.SurfaceCell(slot)).X, bound = Grid.SurfaceY(slot);
        if (Band(slot, out float floor, out float ceiling) && Material.Column(cx, Grid.Cell * .5f + SnowSkin.Half, floor, ceiling, out _, out float high)) bound = MathF.Max(bound, high);
        topBoundCache[slot] = bound; topBoundStamp[slot] = topBoundTick;
    }
    // Brings the bounds of the columns under the box up to date: a slot never read is computed now, the others one pass in TopBoundFrames, spread by slot so that no pass pays for all of them.
    // Called once per particle step on the owning thread; InSnow then only reads.
    public void RefreshTopBounds(in WeatherRect box)
    {
        if (topBoundCache.Length < Grid.SurfaceCount) { topBoundCache = new float[Grid.SurfaceCount]; topBoundStamp = new uint[Grid.SurfaceCount]; columnTopBound = System.Array.Empty<float>(); }
        if (columnTopBound.Length != Grid.Width) { columnTopBound = new float[Grid.Width]; System.Array.Fill(columnTopBound, float.PositiveInfinity); }
        topBoundTick++;
        int x0 = Math.Max(0, (int)MathF.Floor((box.Left - Grid.Origin.X) / Grid.Cell)), x1 = Math.Min(Grid.Width - 1, (int)MathF.Floor((box.Right - Grid.Origin.X) / Grid.Cell));
        for (int x = x0; x <= x1; x++)
        {
            float highest = float.NegativeInfinity;
            for (int k = Grid.ColumnStart(x), end = Grid.ColumnStart(x + 1); k < end; k++)
            {
                int slot = Grid.ColumnSlot(k);
                if (topBoundStamp[slot] == 0 || (topBoundTick + (uint)slot) % TopBoundFrames == 0) ComputeTopBound(slot);
                highest = MathF.Max(highest, topBoundCache[slot]);
            }
            columnTopBound[x] = highest;
        }
    }
    // Whether the point lies in snow: the same answer as FindSurface(point, 0) >= 0, with the exact column query skipped for every point that is clear of the snow of each ground beneath it.
    // A slot whose bound was never computed is treated as near, so the answer is exact whatever was refreshed. Reads only, and so safe on any number of threads between refreshes.
    public bool InSnow(Vec2 point)
    {
        int x = (int)MathF.Floor((point.X - Grid.Origin.X) / Grid.Cell);
        if ((uint)x >= (uint)Grid.Width) return false;
        if ((uint)x < (uint)columnTopBound.Length && point.Y > columnTopBound[x] + TopBoundMargin) return false;
        bool near = false;
        for (int k = Grid.ColumnStart(x), end = Grid.ColumnStart(x + 1); k < end && !near; k++)
        {
            int slot = Grid.ColumnSlot(k);
            if (Grid.SurfaceY(slot) > point.Y) continue;
            near = (uint)slot >= (uint)topBoundStamp.Length || topBoundStamp[slot] == 0 || point.Y <= topBoundCache[slot] + TopBoundMargin;
        }
        return near && FindSurface(point, 0) >= 0;
    }
    // The slot whose ground lies under the point and whose snow reaches it, within reach.
    public int FindSurface(Vec2 point, float reach)
    {
        int x = (int)MathF.Floor((point.X - Grid.Origin.X) / Grid.Cell);
        if ((uint)x >= (uint)Grid.Width) return -1;
        int best = -1; float bestGround = float.NegativeInfinity;
        for (int k = Grid.ColumnStart(x), end = Grid.ColumnStart(x + 1); k < end; k++)
        {
            int slot = Grid.ColumnSlot(k); float ground = Grid.SurfaceY(slot);
            if (ground > point.Y + reach || ground <= bestGround) continue;
            if (Top(slot, point.X) >= point.Y - reach) { best = slot; bestGround = ground; }
        }
        return best;
    }
    // A grain set down on the snow does not stay on a crest steeper than the angle of repose: it rolls to the lower neighbour, two spacings at a time (one spacing only sees the rows of the
    // hexagonal pack), until no neighbour lies more than the friction slope below it. It then rests where it lies and the solver never has to wake it.
    // Wind-borne and falling snow settles only where the weather reaches (exposedOnly); a tool sets its snow down wherever the player works.
    internal float Roll(ref float x, float floor, float ceiling, float high, float ground, bool exposedOnly = false)
    {
        const float Reach = SnowConstitutive.Spacing * 2, Rise = SnowConstitutive.Friction * Reach;
        for (int k = 0; k < 8; k++)
        {
            float best = high - Rise, to = x;
            for (int side = -1; side <= 1; side += 2)
            {
                float nx = x + side * Reach; if (!Grid.Bounds.Contains(nx, high) || exposedOnly && !ExposedAt(nx, ground)) continue;
                float top = Material.Column(nx, SnowSkin.Half, floor, ceiling, out _, out float h) ? h : ground;
                // A grain does not roll into a wall or a closed door: the place it would come to rest must be free.
                if (top < best && Sample(new Vec2(nx, top + SnowConstitutive.Spacing * .5f)).Distance >= SnowConstitutive.Clearance) { best = top; to = nx; }
            }
            if (to == x) break;
            x = to; high = best;
        }
        return high;
    }
    // Snow that falls or is blown settles evenly: a grain does not stay where it first touched but goes to the lowest snow within reach, a row or more under the place it landed. Without this the
    // random places of the grains pile up into mounds a cell wide whose height grows with the number of grains, and the surface turns to a scalloped rim. Only places that carry snow are candidates.
    internal float Level(ref float x, float reach, float floor, float ceiling, float high, float ground)
    {
        const float Step = SnowConstitutive.Spacing * 2, Margin = SnowConstitutive.Spacing * .5f;
        float best = high, at = x;
        for (float dx = Step; dx <= reach; dx += Step)
            for (int side = -1; side <= 1; side += 2)
            {
                float nx = x + side * dx; if (!Grid.Bounds.Contains(nx, high) || !ExposedAt(nx, ground) || !Material.Column(nx, SnowSkin.Half, floor, ceiling, out _, out float top)) continue;
                if (top < best - Margin) { best = top; at = nx; }
            }
        x = at; return best;
    }
    // Where a grain dropped at x from about y comes to rest: the top of the snow over the highest ground within reach of y, after rolling off any crest. False outside the map or over no ground.
    internal bool Landing(ref float x, float y, float above, float below, out float top)
    {
        top = 0; int column = (int)MathF.Floor((x - Grid.Origin.X) / Grid.Cell);
        if ((uint)column >= (uint)Grid.Width) return false;
        int best = -1;
        for (int at = Grid.ColumnStart(column), end = Grid.ColumnStart(column + 1); at < end; at++)
        {
            int slot = Grid.ColumnSlot(at); float ground = Grid.SurfaceY(slot);
            if (ground > y + above || ground < y - below - MaximumDepth) continue;
            if (best < 0 || ground > Grid.SurfaceY(best)) best = slot;
        }
        if (best < 0 || !Band(best, out float floor, out float ceiling)) return false;
        top = Material.Column(x, SnowSkin.Half, floor, ceiling, out _, out float high) ? high : Grid.SurfaceY(best);
        top = Roll(ref x, floor, ceiling, top, Grid.SurfaceY(best)); return true;
    }
    private float[] bins = new float[64]; private readonly System.Collections.Generic.List<int> grains = new();
    // Snow that a tool left standing steeper than the given slope slides: the top grain of the steeper side of the section between left and right is taken to the lower side and rolled to rest, one grain
    // at a time, until no two bins a hand apart differ by more than the slope allows or the budget is spent. A grain is only moved, never made or lost, and nothing is woken: the cut walls of a pit
    // settle to their angle at once and the solver is not asked to slump them for seconds. Returns the grains moved.
    internal int Slump(float left, float right, float reference, float slope, int budget)
    {
        const float Width = SnowConstitutive.Spacing * 2;
        int n = (int)MathF.Ceiling((right - left) / Width); if (n < 2) return 0;
        if (n > bins.Length) bins = new float[n * 2];
        float limit = slope * Width; bool known = false;
        for (int i = 0; i < n; i++) { bins[i] = Surface(left + (i + .5f) * Width, reference); known |= !float.IsNaN(bins[i]); }
        if (!known) return 0;
        int moved = 0;
        for (; moved < budget; moved++)
        {
            int from = -1, to = -1; float worst = limit;
            for (int i = 0; i + 1 < n; i++)
            {
                if (float.IsNaN(bins[i]) || float.IsNaN(bins[i + 1])) continue;
                float drop = bins[i] - bins[i + 1];
                if (drop > worst) { worst = drop; from = i; to = i + 1; } else if (-drop > worst) { worst = -drop; from = i + 1; to = i; }
            }
            if (from < 0) break;
            float y = bins[from];
            // The donor is the highest grain over the steeper bin; it lands on the lower bin and rolls on from there.
            Material.Layer(left + (from + .5f) * Width, Width * .5f, y - MaximumDepth, y + .1f, SnowConstitutive.Spacing, grains);
            int pick = -1; float highest = float.NegativeInfinity;
            for (int k = 0; k < grains.Count; k++) { float py = (float)Material.Point(grains[k]).Position.PreciseY; if (py > highest) { highest = py; pick = grains[k]; } }
            if (pick < 0) { bins[from] = float.NaN; continue; }
            float target = left + (to + .5f) * Width;
            if (!Landing(ref target, y, 1, 1, out float top)) { bins[from] = float.NaN; continue; }
            Vec2 landing = new(target, top + SnowConstitutive.Spacing * .5f);
            if (Sample(landing).Distance < SnowConstitutive.Clearance) { bins[from] = float.NaN; continue; }
            Material.Relocate(pick, landing);
            bins[from] = Surface(left + (from + .5f) * Width, reference);
            int landed = Math.Clamp((int)MathF.Floor((target - left) / Width), 0, n - 1); bins[landed] = Surface(left + (landed + .5f) * Width, reference);
        }
        return moved;
    }
    private float Surface(float x, float reference) => TopUnder(x, reference, 1, 1, out float top) ? top : float.NaN;
}
