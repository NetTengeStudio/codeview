using System;
using System.Collections.Generic;
using ScpItdMod.Core;

namespace ScpItdMod.Weather.Material;

internal readonly struct SnowVehicleSurface
{
    internal readonly float Bottom, Top, Density, Crush, Modulus, Cohesion, Friction, Water, Compaction;
    internal SnowVehicleSurface(float bottom, float top, float density, float crush, float modulus, float cohesion, float friction, float water)
    {
        Bottom = bottom; Top = top; Density = density; Crush = crush; Modulus = modulus; Cohesion = cohesion; Friction = friction; Water = water;
        Compaction = SnowPhysics.Compaction(density);
    }
}

internal sealed class SnowVehicleContacts
{
    private readonly List<int> particles = new(256);
    private readonly SnowPack pack;
    private readonly Action<Vec2, Vec2, float> airborne;

    internal SnowVehicleContacts(SnowPack pack, Action<Vec2, Vec2, float> airborne = null)
    {
        this.pack = pack ?? throw new ArgumentNullException(nameof(pack));
        this.airborne = airborne;
    }

    internal bool Sample(Vec2 point, float reach, out SnowVehicleSurface surface)
    {
        int slot = pack.FindGround(point, pack.Grid.Cell, reach);
        return Read(slot, point.X, out surface);
    }

    private bool Read(int slot, float x, out SnowVehicleSurface surface)
    {
        surface = default;
        if (slot < 0 || !pack.Band(slot, out float floor, out float ceiling)) return false;
        SnowMpm material = pack.Material;
        if (!material.Column(x, SnowSkin.Half, floor, ceiling, out float low, out float high) || high - low < SnowSkin.MinimumDepth) return false;
        material.Collect(x - SnowConstitutive.Spacing, low - SnowConstitutive.Spacing,
            x + SnowConstitutive.Spacing, high + SnowConstitutive.Spacing, particles);
        double mass = 0, volume = 0, water = 0, cohesion = 0, modulus = 0;
        for (int i = 0; i < particles.Count; i++)
        {
            ref readonly SnowPoint p = ref material.Point(particles[i]);
            if (p.Position.PreciseY > high + SnowConstitutive.Spacing * .5f) continue;
            mass += p.Mass; volume += p.Mass / p.Density; water += p.Mass * p.Water;
            cohesion += p.Mass * SnowConstitutive.SinteredCohesion * p.Bond * p.SolidFraction;
            modulus += p.Mass * SnowConstitutive.Young * MathF.Max(.001f, p.SolidFraction) / (p.Plastic * p.Plastic);
        }
        if (!(mass > 0) || !(volume > 0)) return false;
        float density = (float)(mass / volume), wet = (float)(water / mass);
        surface = new SnowVehicleSurface(low, high, density, SnowConstitutive.CrushStrength(density) * (1 - wet),
            (float)(modulus / mass), (float)(cohesion / mass), SnowConstitutive.Friction * (1 - wet), wet);
        return true;
    }

    internal bool Load(Vec2 point, float halfLength, float width, float normalForce, float seconds, float slipSpeed,
        float sinkage, float shearWork, SnowFlights flights, float temperature, ushort predictor = 0)
    {
        if (!(halfLength > 0) || !(width > 0) || !(normalForce > 0) || !(seconds > 0) || seconds > 1 ||
            !float.IsFinite(halfLength + width + normalForce + seconds + slipSpeed + sinkage + shearWork)) return false;
        int slot = pack.FindSurface(point, halfLength);
        if (slot < 0 || !pack.Band(slot, out float floor, out float ceiling) || !Read(slot, point.X, out SnowVehicleSurface surface)) return false;
        float pressure = normalForce / (2 * halfLength * width);
        float rate = MathF.Sqrt(surface.Modulus / surface.Density) / SnowConstitutive.Spacing;
        bool changed = SnowPrint.Press(pack.Material, point.X, halfLength, pressure, seconds, floor, ceiling, rate, predictor);
        if (!(shearWork > 0) || !(sinkage > 0) || slipSpeed == 0 || flights == null || flights.Count == SnowFlights.Capacity) return changed;
        double specificCut = SnowPhysics.ExcavationWorkDensity(surface.Density, sinkage, sinkage, surface.Cohesion, surface.Friction) / surface.Density;
        float launchSpeed = MathF.Abs(slipSpeed);
        double maximumMass = shearWork / (width * (specificCut + .5 * launchSpeed * launchSpeed + SnowConstitutive.Gravity * sinkage));
        if (!(maximumMass > 0) || !double.IsFinite(maximumMass)) return changed;
        SnowPayload payload = SnowShovel.Dig(pack, point, 2 * halfLength, sinkage, maximumMass);
        if (!(payload.Mass > 0)) return changed;
        Vec2 velocity = new(-MathF.Sign(slipSpeed) * launchSpeed, MathF.Sqrt(2 * SnowConstitutive.Gravity * sinkage));
        Vec2 launch = new(point.X, surface.Top + SnowConstitutive.Spacing);
        float kilograms = (float)(payload.Mass * width);
        if (!flights.Launch(launch, velocity, ref payload, width))
            SnowShovel.Pour(pack, temperature, point, halfLength, ref payload);
        else airborne?.Invoke(launch, velocity, kilograms);
        return true;
    }
}
