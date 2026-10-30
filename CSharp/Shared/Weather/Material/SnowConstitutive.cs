using System;
using Vector2 = ScpItdMod.Core.Vec2;

namespace ScpItdMod.Weather.Material;

internal readonly struct SnowBoundary
{
    internal readonly float Distance;
    internal readonly Vector2 Normal;
    internal SnowBoundary(float distance, Vector2 normal) { Distance = distance; Normal = normal; }
}

internal interface ISnowTerrain
{
    SnowBoundary Sample(Vector2 point);
    bool Clear(Vector2 from, Vector2 to);
    bool Contains(Vector2 point);
}

internal static partial class SnowConstitutive
{
    internal const float Gravity = 9.81f, Spacing = .06f, Bucket = Spacing * 1.5f;
    internal const float Area = Spacing * Spacing * .8660254037844386f;
    // A particle set down at rest on a surface has its centre half a spacing from it; placement accepts a little less, or float rounding of that half spacing
    // rejects the whole bottom row and the snow is laid hanging a row above the ground, to drop as soon as anything wakes it.
    internal const float Clearance = Spacing * .45f;
    internal const float MaximumVolume = Area * 1.5f;
    internal const float FreshDensity = 100, MaximumDensity = 650, WaterDensity = 1000;
    internal const float Young = 140000, Cohesion = 500, Friction = .55f;
    // Cold snow that lies still sinters: a particle that is not yielding regains its bond within 1/SinterRate seconds (about ten minutes, the time grain necks take
    // to grow in cold snow), and a bonded one resists SinteredCohesion of shear and tension. Freshly thrown or poured snow is loose for that long and slumps; an old pack stands.
    internal const float SinteredCohesion = 3000, SinterRate = 1 / 600f;
    internal const float IceHeat = 2100, WaterHeat = 4180, LatentHeat = 334000;
    internal const float Step = 1 / 240f;
    internal const float MaximumStep = 1 / 60f;
    internal const float SleepVelocity = .02f;
    internal static float PressureCap(float density) { float ratio = density / FreshDensity; return 500 * ratio * ratio * ratio; }
    internal static float CrushStrength(float density) { float ratio = density / FreshDensity; return 500 * ratio * ratio * ratio; }
    internal static float Radius(float volume) => .5f * Spacing * MathF.Sqrt(volume / Area);
}
