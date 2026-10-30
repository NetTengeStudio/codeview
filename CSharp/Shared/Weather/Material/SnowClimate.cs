using System;
using System.Collections.Generic;
using ScpItdMod.Core;

namespace ScpItdMod.Weather.Material;

// Weather acts on the particles themselves. Snowfall adds fresh particles on top of the pack, wind lifts loose surface particles and sets them down downwind,
// warmth raises their enthalpy until they are wet and run off, frost takes it back and bonds the wet grains into crust. No second state exists next to the particles.
// The ledgers are per surface slot and the random stream is seeded, so peers that run the same steps on the same particles reproduce each other.
internal sealed class SnowClimate
{
    private const float LayerThickness = .12f, Hop = .5f, MaximumCarry = 8, LiftLayer = .06f;
    private readonly SnowPack pack;
    private readonly WeatherConfig config;
    private double[] depositDebt = Array.Empty<double>(), liftDebt = Array.Empty<double>(), landing = Array.Empty<double>();
    private const double GoldenStep = .6180339887498949;
    private bool[] wet = Array.Empty<bool>();
    private readonly List<int> layer = new();
    private uint random;
    private float[] airborne = Array.Empty<float>();
    private float[] crust = Array.Empty<float>(), warmth = Array.Empty<float>();
    private float clock;
    // Heat reaches the top layer of each slot every HeatStride-th step with that many steps of energy at once: the scan of a layer is the dearest thing here and the layer cannot change that fast.
    private const int HeatStride = 16;
    private int heatPhase;
    internal double Deposited, Lifted, Melted;
    internal SnowClimate(SnowPack pack, WeatherConfig config, uint seed)
    {
        this.pack = pack; this.config = config; random = seed | 1; Resize();
    }
    internal void Resize()
    {
        WeatherGrid grid = pack.Grid; int n = grid.SurfaceCount;
        depositDebt = new double[n]; liftDebt = new double[n]; landing = new double[n]; airborne = new float[n]; crust = new float[n]; warmth = new float[n]; wet = new bool[n];
        // Every slot owes the same mass at the same rate: left alone they would all reach a whole particle on the same step and the weather would arrive as one 30 ms burst every minute and a half. A random head start spreads it over time.
        for (int slot = 0; slot < n; slot++) { depositDebt[slot] = -Next() * Particle; liftDebt[slot] = -Next() * Particle; landing[slot] = Next(); }
    }
    // Share of the carrying capacity of the wind that a slot is using right now: what the drift layer of the picture shows.
    internal float Airborne(int slot) => (uint)slot < (uint)airborne.Length ? airborne[slot] : 0;
    private float Next() { random ^= random << 13; random ^= random >> 17; random ^= random << 5; return (random & 0xFFFFFF) / (float)0x1000000; }
    private static float Particle => SnowPhysics.FreshDensity * SnowConstitutive.Area;
    private bool Band(int slot, out float floor, out float ceiling) => pack.Band(slot, out floor, out ceiling);
    private float LocalWind(in WeatherState weather, int slot, float exposure)
    {
        Vec2 point = pack.Grid.Position(pack.Grid.SurfaceCell(slot));
        point = new Vec2(point.X, pack.Grid.SurfaceY(slot));
        return weather.Wind.X * exposure + (pack.ExternalWind?.Invoke(point).X ?? 0);
    }

    internal void Observe(in WeatherState weather, float dt)
    {
        WeatherGrid grid = pack.Grid;
        float density = weather.Pressure * 100 / (287.05f * (weather.Temperature + 273.15f));
        float blend = 1 - MathF.Exp(-dt / 2);
        for (int slot = 0; slot < wet.Length; slot++)
        {
            float target = 0;
            if (Band(slot, out float floor, out float ceiling))
            {
                float exposure = pack.Exposed(slot) ? grid.Exposure(grid.SurfaceCell(slot)) : 0;
                float capacity = SnowPhysics.CarryingMass(MathF.Abs(LocalWind(weather, slot, exposure)), config.WindThreshold, density);
                if (capacity > 0)
                {
                    float x = grid.Position(grid.SurfaceCell(slot)).X;
                    pack.Material.Layer(x, grid.Cell * .5f, floor, ceiling, LiftLayer, layer);
                    double loose = 0, mass = 0;
                    for (int i = 0; i < layer.Count; i++)
                    {
                        ref readonly SnowPoint p = ref pack.Material.Point(layer[i]);
                        mass += p.Mass;
                        if (p.Density <= SnowPhysics.PackedDensity && p.Water == 0 && p.Bond <= .5f) loose += p.Mass;
                    }
                    if (mass > 0) target = MathF.Min(1, capacity / (config.CarryDepth * SnowPhysics.FreshDensity)) * (float)(loose / mass);
                }
            }
            airborne[slot] += (target - airborne[slot]) * blend;
        }
    }
    internal void Step(in WeatherState weather, float dt)
    {
        if (!float.IsFinite(dt) || dt <= 0) throw new ArgumentOutOfRangeException(nameof(dt));
        WeatherGrid grid = pack.Grid;
        Observe(weather, dt);
        float air = weather.Temperature;
        float fallMass = weather.Snowfall * config.SnowRate * SnowPhysics.FreshDensity * dt * grid.Cell;
        float airDensity = weather.Pressure * 100 / (287.05f * (air + 273.15f)), exchange = 1 - MathF.Exp(-config.TransportRate * dt);
        heatPhase = (heatPhase + 1) % HeatStride; clock += dt;
        for (int slot = 0; slot < wet.Length; slot++)
        {
            float exposure = pack.Exposed(slot) ? grid.Exposure(grid.SurfaceCell(slot)) : 0;
            if (fallMass > 0) { depositDebt[slot] += fallMass * exposure; if (depositDebt[slot] >= Particle) Deposit(slot, air); }
            float localWind = LocalWind(weather, slot, exposure), wind = MathF.Abs(localWind);
            int direction = localWind >= 0 ? 1 : -1;
            if (wind > config.WindThreshold)
            {
                float local = wind, capacity = SnowPhysics.CarryingMass(local, config.WindThreshold, airDensity);
                if (capacity > 0)
                {
                    liftDebt[slot] += MathF.Min(capacity, config.CarryDepth * SnowPhysics.FreshDensity) * exchange * grid.Cell;
                    if (liftDebt[slot] >= Particle) Lift(slot, direction, local);
                }
            }
            if (slot % HeatStride != heatPhase) continue;
            float warm = warmth[slot], here = air + warm * (config.IndoorTemperature - air);
            if (here > 0 || wet[slot]) Heat(slot, config.MeltRate * SnowPhysics.FreshDensity * here * SnowConstitutive.LatentHeat * dt * grid.Cell * HeatStride * MathF.Max(exposure, warm), here);
        }
    }

    // Heaters warm the snow around them toward indoor temperature; they register every step and the registration is cleared before the next one.
    internal void ClearHeat() => Array.Clear(warmth);
    internal void Heat(Vec2 centre, float radius, float strength)
    {
        WeatherGrid grid = pack.Grid;
        for (int slot = 0; slot < warmth.Length; slot++)
        {
            Vec2 at = grid.Position(grid.SurfaceCell(slot)); float d = Vec2.Distance(new Vec2(at.X, grid.SurfaceY(slot)), centre);
            if (d < radius) warmth[slot] = MathF.Max(warmth[slot], strength * (1 - d / radius));
        }
    }
    // Sets the snow cover of every open surface to a depth: particles above it are taken away, particles are laid on top until it is reached.
    internal void SetDepth(float depth)
    {
        WeatherGrid grid = pack.Grid; SnowMpm material = pack.Material; var ids = new List<int>(); float spacing = SnowConstitutive.Spacing, row = spacing * .8660254f;
        for (int slot = 0; slot < wet.Length; slot++)
        {
            if (!pack.Exposed(slot) || !Band(slot, out float floor, out float ceiling)) continue;
            float left = grid.Position(grid.SurfaceCell(slot)).X - grid.Cell * .5f, right = left + grid.Cell;
            for (float x = left + spacing * .5f; x < right; x += spacing)
            {
                if (material.Column(x, spacing * .5f, floor, ceiling, out float low, out float high))
                {
                    if (high - low > depth)
                    {
                        material.Layer(x, spacing * .5f, floor, ceiling, high - low, ids);
                        for (int i = 0; i < ids.Count; i++) if (material.Point(ids[i]).Position.PreciseY > low + depth) material.Remove(ids[i]);
                        continue;
                    }
                }
                else { low = grid.SurfaceY(slot); high = low; }
                for (float y = high + spacing * .5f; y - low + spacing * .5f <= depth; y += row) Place(new Vec2(x, y), Particle, -5);
            }
            if (material.Column(left + grid.Cell * .5f, grid.Cell * .5f, floor, ceiling, out _, out float top)) material.Disturb(new Vec2(left + grid.Cell * .5f, top), grid.Cell, 2);
        }
    }
    private void Deposit(int slot, float air)
    {
        WeatherGrid grid = pack.Grid; SnowMpm material = pack.Material;
        if (!Band(slot, out float floor, out float ceiling)) { depositDebt[slot] = 0; return; }
        float left = grid.Position(grid.SurfaceCell(slot)).X - grid.Cell * .5f;
        while (depositDebt[slot] >= Particle)
        {
            // Falling snow lands evenly over the cell: successive grains of a slot follow the golden-ratio sequence, which covers the cell without the random mounds that a drawn position piles up, so no grain has to be moved sideways to even the surface out (that would pour the snow of a whole cell into any footprint or pit).
            depositDebt[slot] -= Particle; landing[slot] = (landing[slot] + GoldenStep) % 1; float x = left + (float)landing[slot] * grid.Cell;
            if (!material.Column(x, SnowConstitutive.Spacing, floor, ceiling, out float low, out float high)) { low = grid.SurfaceY(slot); high = low; }
            if (high - low >= pack.MaximumDepth) continue;
            high = pack.Roll(ref x, floor, ceiling, high, grid.SurfaceY(slot), true);
            Place(new Vec2(x, high + SnowConstitutive.Spacing * .5f), Particle, MathF.Min(0, air));
        }
    }
    private void Place(Vec2 position, double mass, float temperature)
    {
        SnowBoundary hit = pack.Sample(position);
        if (hit.Distance < SnowConstitutive.Clearance || !pack.Grid.Bounds.Contains(position.X, position.Y)) return;
        SnowMatrix elastic = SnowPlasticity.Overburden(SnowPhysics.FreshDensity * SnowConstitutive.Gravity * SnowConstitutive.Spacing);
        int id = pack.Material.Seed(new SnowPoint { Position = position, Mass = mass, ReferenceVolume = SnowConstitutive.Area, Enthalpy = temperature * SnowConstitutive.IceHeat, Elastic = elastic, Plastic = 1, Bond = .1f }, true);
        pack.Material.Sampled(id, hit.Distance); Deposited += mass;
    }
    // Wind takes whole surface particles, loose and dry ones only, and sets them down where a hop of the saltation layer ends.
    private void Lift(int slot, int direction, float wind)
    {
        WeatherGrid grid = pack.Grid; SnowMpm material = pack.Material;
        if (!Band(slot, out float floor, out float ceiling)) { liftDebt[slot] = 0; return; }
        float x = grid.Position(grid.SurfaceCell(slot)).X, packed = SnowPhysics.Density(SnowContactModel.PackedCompaction);
        while (liftDebt[slot] >= Particle)
        {
            liftDebt[slot] -= Particle;
            material.Layer(x, grid.Cell * .5f, floor, ceiling, LiftLayer, layer);
            int pick = -1; float best = float.MaxValue;
            for (int i = 0; i < layer.Count; i++)
            {
                ref readonly SnowPoint point = ref material.Point(layer[i]);
                if (point.Density > packed || point.Water > 0 || point.Bond > .5f) continue;
                float key = -(float)point.Position.PreciseY + Next() * .02f; if (key < best) { best = key; pick = layer[i]; }
            }
            if (pick < 0) { liftDebt[slot] = 0; crust[slot] = clock + 30; return; }
            float travel = MathF.Min(MaximumCarry, wind * Hop * (.5f + Next())), target = (float)material.Point(pick).Position.PreciseX + direction * travel;
            double mass = material.Point(pick).Mass;
            if (!LandingTop(ref target, grid.SurfaceY(slot), out float top)) { material.Remove(pick); Lifted += mass; continue; }
            Vec2 landing = new(target, top + SnowConstitutive.Spacing * .5f);
            if (pack.Sample(landing).Distance < SnowConstitutive.Clearance) continue;
            material.Relocate(pick, landing); Lifted += mass;
        }
    }
    private bool LandingTop(ref float x, float reference, out float top)
    {
        WeatherGrid grid = pack.Grid; top = 0; int column = (int)MathF.Floor((x - grid.Origin.X) / grid.Cell);
        if ((uint)column >= (uint)grid.Width) return false;
        int best = -1; float gap = float.MaxValue;
        for (int at = grid.ColumnStart(column), end = grid.ColumnStart(column + 1); at < end; at++)
        {
            int slot = grid.ColumnSlot(at); if (!pack.Exposed(slot)) continue; float distance = MathF.Abs(grid.SurfaceY(slot) - reference);
            if (distance < gap) { gap = distance; best = slot; }
        }
        if (best < 0 || gap > 2) return false;
        Band(best, out float floor, out float ceiling);
        top = pack.Material.Column(x, SnowConstitutive.Spacing, floor, ceiling, out _, out float high) ? high : grid.SurfaceY(best);
        top = pack.Level(ref x, grid.Cell * 2, floor, ceiling, top, grid.SurfaceY(best)); top = pack.Roll(ref x, floor, ceiling, top, grid.SurfaceY(best), true);
        return true;
    }
    // Energy reaching a slot goes into its top layer, kilogram for kilogram. Warm air melts; frost returns the heat and refreezes what was wet into bonded crust.
    private void Heat(int slot, float joules, float air)
    {
        WeatherGrid grid = pack.Grid; SnowMpm material = pack.Material;
        if (!Band(slot, out float floor, out float ceiling)) return;
        material.Layer(grid.Position(grid.SurfaceCell(slot)).X, grid.Cell * .5f, floor, ceiling, LayerThickness, layer);
        double mass = 0; for (int i = 0; i < layer.Count; i++) mass += material.Point(layer[i]).Mass;
        if (mass <= 0) { wet[slot] = false; return; }
        float perKilogram = (float)(joules / mass), coldest = MathF.Min(0, air) * SnowConstitutive.IceHeat; bool any = false;
        for (int i = 0; i < layer.Count; i++)
        {
            int id = layer[i]; ref SnowPoint point = ref material.Edit(id); float before = point.Water;
            point.Enthalpy = Math.Max(coldest, point.Enthalpy + perKilogram);
            if (point.Enthalpy >= SnowConstitutive.LatentHeat) { Melted += point.Mass; material.Remove(id); continue; }
            float after = point.Water; if (after > 0) any = true;
            if (after < before) { point.Refrozen += before - after; point.Bond = MathF.Min(1, point.Bond + (before - after) * 2); }
        }
        wet[slot] = any;
    }
}
