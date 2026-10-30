using System;
using ScpItdMod.Core;

namespace ScpItdMod.Weather.Material;

public sealed partial class SnowPack : ISnowTerrain
{
    private readonly WeatherConfig config;
    public WeatherGrid Grid { get; private set; }
    internal SnowMpm Material { get; }
    internal Func<Vec2, Vec2> ExternalWind { get; set; }
    public float MaximumDepth => config.MaximumDepth;
    public uint Revision => Material.Revision;
    public SnowPack(WeatherConfig config, WeatherGrid grid)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config)); Grid = grid ?? throw new ArgumentNullException(nameof(grid));
        Material = new SnowMpm(this);
    }
    internal void Initialize(float depth, float density, float temperature, bool resting = true)
    {
        if (Material.Count != 0) throw new InvalidOperationException("Snow initialization requires an empty material");
        if (!float.IsFinite(depth) || depth < 0 || depth > config.MaximumDepth || !float.IsFinite(density) || density < SnowConstitutive.FreshDensity || density > SnowConstitutive.MaximumDensity || !float.IsFinite(temperature) || temperature > 0)
            throw new ArgumentException("Invalid initial snow");
        const float spacing = SnowConstitutive.Spacing, row = spacing * .8660254037844386f;
        float[] settled = Repose(depth);
        var seeds = new Candidate[4096]; int count = 0;
        for (int slot = 0; slot < Grid.SurfaceCount; slot++)
        {
            if (!Exposed(slot)) continue;
            int cell = Grid.SurfaceCell(slot);
            float centre = Grid.Position(cell).X, left = centre - Grid.Cell * .5f, right = left + Grid.Cell, ground = Grid.SurfaceY(slot), ceiling = Grid.Ceiling(slot);
            float top = MathF.Min(ceiling, ground + settled[slot]);
            // The surface runs straight between the tops of neighbouring cells instead of standing in one flat step per cell: the repose limit only bounds the rise from one centre to the next, and a
            // terrace of that height at every cell edge is what a foot and the picture would meet. Beside an edge, a wall or a terrain step of more than a cell the cell keeps its own top.
            int before = Neighbour(slot, -1, Grid.Cell * 1.01f), after = Neighbour(slot, 1, Grid.Cell * 1.01f);
            float topBefore = before >= 0 ? MathF.Min(Grid.Ceiling(before), Grid.SurfaceY(before) + settled[before]) : top, topAfter = after >= 0 ? MathF.Min(Grid.Ceiling(after), Grid.SurfaceY(after) + settled[after]) : top;
            int first = (int)MathF.Ceiling(left / spacing);
            for (int layer = 0; ground + row * (layer + 1) <= MathF.Max(top, MathF.Max(topBefore, topAfter)); layer++)
            {
                float offset = (layer & 1) * spacing * .5f;
                for (int column = first - 1; ; column++)
                {
                    float x = column * spacing + offset; if (x < left) continue; if (x >= right) break;
                    float t = MathF.Abs(x - centre) / Grid.Cell, here = MathF.Min(ceiling, top + ((x < centre ? topBefore : topAfter) - top) * t);
                    if (ground + row * (layer + 1) > here) continue;
                    if (count == seeds.Length) Array.Resize(ref seeds, count * 2);
                    seeds[count++] = new Candidate { Position = new Vec2(x, ground + spacing * .5f + layer * row), Top = here };
                }
            }
        }
        // The distance to the terrain is the dominant cost of seeding and each candidate is independent of the others, so the queries run across the solver's helper threads and only the insertion stays serial.
        Grid.Geometry.Seal();
        var fork = new SnowFork(SnowFork.DefaultHelpers); Candidate[] list = seeds;
        fork.Run(count, 4096, (begin, end) => { for (int i = begin; i < end; i++) list[i].Distance = Sample(list[i].Position).Distance; });
        int kept = 0;
        for (int i = 0; i < count; i++)
        {
            Vec2 position = list[i].Position;
            if (list[i].Distance >= SnowConstitutive.Clearance && Grid.Bounds.Contains(position.X, position.Y)) list[kept++] = list[i];
        }
        // The pack size is known before the first particle goes in, so the storage is allocated once at that size instead of doubling its way there and being trimmed afterwards.
        Material.Reserve(kept);
        for (int i = 0; i < kept; i++)
        {
            ref readonly Candidate seed = ref list[i]; Vec2 position = seed.Position;
            SnowMatrix elastic = SnowPlasticity.Overburden(density * SnowConstitutive.Gravity * MathF.Max(spacing, seed.Top - position.Y));
            int id = Material.Seed(new SnowPoint { Position = position, Mass = density * SnowConstitutive.Area, ReferenceVolume = SnowConstitutive.Area,
                Enthalpy = temperature * SnowConstitutive.IceHeat, Elastic = elastic, Plastic = 1, Bond = .1f }, resting);
            Material.Sampled(id, seed.Distance);
        }
        Material.Trim();
        Material.Reserve();
    }
    private struct Candidate { internal Vec2 Position; internal float Top, Distance; }
    // A deposit steeper than the angle of repose is not at rest: seeding it would leave the unstable slopes avalanching for seconds and keep tens of thousands of particles awake.
    // The initial depth is therefore lowered, column against neighbouring column, until no free surface exceeds the friction slope.
    private float[] Repose(float depth)
    {
        var settled = new float[Grid.SurfaceCount]; float rise = SnowConstitutive.Friction * Grid.Cell, reach = Grid.Cell * 3;
        for (int slot = 0; slot < settled.Length; slot++)
        {
            settled[slot] = Exposed(slot) ? MathF.Min(depth, MathF.Max(0, Grid.Ceiling(slot) - Grid.SurfaceY(slot))) : 0;
        }
        for (int pass = 0, changed = 1; changed != 0 && pass < 4096; pass++)
        {
            changed = 0;
            for (int slot = 0; slot < settled.Length; slot++)
            {
                if (settled[slot] <= 0) continue;
                float ground = Grid.SurfaceY(slot), top = ground + settled[slot], limit = top;
                for (int side = -1; side <= 1; side += 2)
                {
                    int x = Grid.SurfaceCell(slot) % Grid.Width + side; if ((uint)x >= (uint)Grid.Width) continue;
                    int best = Neighbour(slot, side, reach);
                    if (best >= 0) limit = MathF.Min(limit, Grid.SurfaceY(best) + settled[best] + rise);
                    else if (Drops(x, ground, reach)) limit = MathF.Min(limit, ground + rise);
                }
                if (limit < top - 1e-5f) { settled[slot] = MathF.Max(0, limit - ground); changed = 1; }
            }
        }
        return settled;
    }
    // The surface of the neighbouring column whose ground lies nearest to this slot's, within reach; -1 where there is none (an edge, a wall or the border of the map).
    private int Neighbour(int slot, int side, float reach)
    {
        int x = Grid.SurfaceCell(slot) % Grid.Width + side; if ((uint)x >= (uint)Grid.Width) return -1;
        float ground = Grid.SurfaceY(slot), gap = float.PositiveInfinity; int best = -1;
        for (int k = Grid.ColumnStart(x), end = Grid.ColumnStart(x + 1); k < end; k++)
        {
            int other = Grid.ColumnSlot(k); float distance = MathF.Abs(Grid.SurfaceY(other) - ground);
            if (distance <= reach && distance < gap) { gap = distance; best = other; }
        }
        return best;
    }
    // A neighbouring column with no surface within reach of this ground level and nothing but air or a deeper drop beside it is an edge: snow must taper to nothing there instead of standing as a wall.
    private bool Drops(int column, float ground, float reach)
    {
        for (int k = Grid.ColumnStart(column), end = Grid.ColumnStart(column + 1); k < end; k++)
            if (Grid.SurfaceY(Grid.ColumnSlot(k)) > ground + reach) return false;
        return true;
    }
    internal void Rebuild(WeatherGrid grid)
    {
        Grid = grid ?? throw new ArgumentNullException(nameof(grid)); exposed = Array.Empty<bool>(); Material.SetTerrain(this);
    }
    SnowBoundary ISnowTerrain.Sample(Vec2 position) => Sample(position);
    internal SnowBoundary Sample(Vec2 position)
    {
        GeometrySample sample = Grid.Geometry.Sample(position); return new SnowBoundary(sample.Distance, sample.Normal);
    }
    bool ISnowTerrain.Clear(Vec2 from, Vec2 to) => Grid.Geometry.SegmentClear(from, to);
    bool ISnowTerrain.Contains(Vec2 position) => Grid.Bounds.Contains(position.X, position.Y);
}
