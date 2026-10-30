using System;
using ScpItdMod.Core;

namespace ScpItdMod.Weather.Material;

internal struct SnowMatrix
{
    internal float A, B, C, D;
    internal SnowMatrix(float a, float b, float c, float d) { A = a; B = b; C = c; D = d; }
    internal static SnowMatrix Identity => new(1, 0, 0, 1);
    internal readonly float Determinant => A * D - B * C;
    internal readonly SnowMatrix Transpose => new(A, C, B, D);
    internal readonly SnowMatrix Inverse => new SnowMatrix(D, -B, -C, A) * (1 / Determinant);
    internal readonly float NormSquared => A * A + B * B + C * C + D * D;
    public static SnowMatrix operator +(SnowMatrix a, SnowMatrix b) => new(a.A + b.A, a.B + b.B, a.C + b.C, a.D + b.D);
    public static SnowMatrix operator -(SnowMatrix a, SnowMatrix b) => new(a.A - b.A, a.B - b.B, a.C - b.C, a.D - b.D);
    public static SnowMatrix operator *(SnowMatrix a, float b) => new(a.A * b, a.B * b, a.C * b, a.D * b);
    public static SnowMatrix operator *(SnowMatrix a, SnowMatrix b) => new(a.A * b.A + a.B * b.C, a.A * b.B + a.B * b.D, a.C * b.A + a.D * b.C, a.C * b.B + a.D * b.D);
    public static Vec2 operator *(SnowMatrix a, Vec2 b) => new(a.A * b.X + a.B * b.Y, a.C * b.X + a.D * b.Y);
    internal static SnowMatrix Outer(Vec2 a, Vec2 b) => new(a.X * b.X, a.X * b.Y, a.Y * b.X, a.Y * b.Y);
    // cos and sin of half the angle of the vector (x, y), taken from the half-angle identities: the same numbers as Atan2 / 2 followed by Cos and Sin, without the trigonometry that dominates the per-particle update.
    internal static void HalfAngle(float x, float y, out float c, out float s)
    {
        float r = MathF.Sqrt(x * x + y * y);
        if (r > 1e-20f) { c = MathF.Sqrt(MathF.Max(0, (1 + x / r) * .5f)); s = MathF.CopySign(MathF.Sqrt(MathF.Max(0, (1 - x / r) * .5f)), y); }
        else { c = 1; s = 0; }
    }
    internal readonly SnowMatrix Principal(out Vec2 singular, out SnowMatrix basis)
    {
        float x = A + D, y = C - B, inverse = 1 / MathF.Max(1e-12f, MathF.Sqrt(x * x + y * y));
        SnowMatrix rotation = new(x * inverse, -y * inverse, y * inverse, x * inverse);
        SnowMatrix symmetric = rotation.Transpose * this;
        HalfAngle(symmetric.A - symmetric.D, symmetric.B + symmetric.C, out float c, out float s);
        basis = new(c, -s, s, c);
        singular = new(MathF.Max(1e-6f, c*c*symmetric.A+c*s*(symmetric.B+symmetric.C)+s*s*symmetric.D),
            MathF.Max(1e-6f, s*s*symmetric.A-c*s*(symmetric.B+symmetric.C)+c*c*symmetric.D));
        return rotation * basis;
    }
}

internal static class SnowPlasticity
{
    internal const float Young = 140000, Poisson = .2f;
    internal static SnowMatrix Overburden(float pressure)
    {
        if (!float.IsFinite(pressure) || pressure < 0) throw new ArgumentOutOfRangeException(nameof(pressure));
        float mu = Young / (2 * (1 + Poisson)), lambda = Young * Poisson / ((1 + Poisson) * (1 - 2 * Poisson));
        float yy = -pressure, xx = yy * (1 - .55f / 1.4142135623730951f);
        float trace = (xx + yy) / (2 * mu + 2 * lambda);
        return new SnowMatrix(MathF.Exp((xx - lambda * trace) / (2 * mu)), 0, 0, MathF.Exp((yy - lambda * trace) / (2 * mu)));
    }
    internal static SnowMatrix FromStress(SnowMatrix stress, in SnowPoint point)
    {
        float hardening = MathF.Max(.001f, point.SolidFraction) / (point.Plastic * point.Plastic);
        float mu = Young / (2 * (1 + Poisson)) * hardening;
        float lambda = Young * Poisson / ((1 + Poisson) * (1 - 2 * Poisson)) * hardening;
        float trace = (stress.A + stress.D) / (2 * mu + 2 * lambda);
        SnowMatrix.HalfAngle(stress.A - stress.D, stress.B + stress.C, out float c, out float s);
        SnowMatrix basis = new(c, -s, s, c), diagonal = basis.Transpose * stress * basis;
        return basis * new SnowMatrix(MathF.Exp((diagonal.A - lambda * trace) / (2 * mu)), 0, 0, MathF.Exp((diagonal.D - lambda * trace) / (2 * mu))) * basis.Transpose;
    }
    internal static SnowMatrix Stress(in SnowPoint point)
    {
        float hardening = point.SolidFraction / (point.Plastic * point.Plastic);
        float mu = Young / (2 * (1 + Poisson)) * hardening;
        float lambda = Young * Poisson / ((1 + Poisson) * (1 - 2 * Poisson)) * hardening;
        SnowMatrix u = point.Elastic.Principal(out Vec2 singular, out _);
        float e0 = MathF.Log(singular.X), e1 = MathF.Log(singular.Y), trace = e0 + e1;
        return u * new SnowMatrix(2 * mu * e0 + lambda * trace, 0, 0, 2 * mu * e1 + lambda * trace) * u.Transpose;
    }
    internal static SnowMatrix Project(SnowMatrix trial, ref SnowPoint point, float dt)
    {
        // The step limiter keeps this unreachable for finite input; a point that still arrives here is released from its elastic strain instead of stopping the game.
        if (!(trial.Determinant > 1e-6f) || !float.IsFinite(trial.NormSquared)) { point.Bond = 0; return SnowMatrix.Identity; }
        SnowMatrix u = trial.Principal(out Vec2 singular, out SnowMatrix basis);
        float hardening = MathF.Max(.001f, point.SolidFraction) / (point.Plastic * point.Plastic);
        float mu = Young / (2 * (1 + Poisson)) * hardening;
        float bulk = Young / (2 * (1 + Poisson) * (1 - 2 * Poisson)) * hardening;
        float e0 = MathF.Log(singular.X), e1 = MathF.Log(singular.Y), trace = e0 + e1;
        float deviator = (e0 - e1) * .5f, norm = MathF.Abs(deviator) * 1.4142135623730951f;
        float cohesion = SnowConstitutive.SinteredCohesion * point.Bond * point.SolidFraction;
        float ratioDensity = point.ReferenceDensity / (SnowConstitutive.FreshDensity * point.Plastic);
        float cap = 500 * ratioDensity * ratioDensity * ratioDensity;
        float projectedTrace = Math.Clamp(trace, -cap / bulk, cohesion / bulk);
        float projectedNorm = MathF.Min(norm, (cohesion + .55f * MathF.Max(0, -bulk * projectedTrace)) / (2 * mu));
        float ratio = norm > 1e-9f ? projectedNorm / norm : 1;
        float a = MathF.Exp(projectedTrace * .5f + deviator * ratio), b = MathF.Exp(projectedTrace * .5f - deviator * ratio);
        float determinant = singular.X * singular.Y, previous = point.Plastic;
        point.Plastic = Math.Clamp(previous * determinant / (a * b), point.ReferenceDensity / SnowConstitutive.MaximumDensity, 2);
        float scale = MathF.Sqrt(determinant * previous / (point.Plastic * a * b));
        point.Bond *= MathF.Exp(-MathF.Max(0, norm - projectedNorm) / .05f);
        if (norm <= projectedNorm && point.Bond < 1) point.Bond = MathF.Min(1, point.Bond + dt * SnowConstitutive.SinterRate);
        return u * new SnowMatrix(a * scale, 0, 0, b * scale) * basis.Transpose;
    }
}
