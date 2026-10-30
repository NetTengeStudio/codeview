using System;
using Vector2 = ScpItdMod.Core.Vec2;

namespace ScpItdMod.Weather.Material;

internal sealed class SnowCollider
{
    internal readonly Vector2[] Vertices;
    internal readonly Vector2 Centre;
    internal readonly float Radius;
    internal readonly float Bound;
    // Farthest distance of the shape from its own Centre: waking only what a shape can touch keeps a tall leg from waking a metre of snow around it.
    internal readonly float Reach;
    internal SnowCollider(Vector2 centre, float radius)
    {
        if (!float.IsFinite(radius) || radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius));
        Centre = centre; Radius = radius; Bound = centre.Length() + radius; Reach = radius; Vertices = Array.Empty<Vector2>();
    }
    internal SnowCollider(ReadOnlySpan<Vector2> vertices)
    {
        if (vertices.Length < 3) throw new ArgumentException("A convex snow collider requires three vertices", nameof(vertices));
        Vertices = vertices.ToArray();
        double area = 0;
        for (int i = 0; i < Vertices.Length; i++)
        {
            Vector2 a = Vertices[i], b = Vertices[(i + 1) % Vertices.Length];
            float square = a.LengthSquared();
            if (!float.IsFinite(square)) throw new ArgumentException("Nonfinite snow collider", nameof(vertices));
            area += (double)a.X * b.Y - (double)a.Y * b.X; Bound = MathF.Max(Bound, MathF.Sqrt(square));
        }
        if (area == 0) throw new ArgumentException("Degenerate snow collider", nameof(vertices));
        if (area < 0) Array.Reverse(Vertices);
        Vector2 sum = default; for (int i = 0; i < Vertices.Length; i++) sum += Vertices[i];
        Centre = sum / Vertices.Length; for (int i = 0; i < Vertices.Length; i++) Reach = MathF.Max(Reach, (Vertices[i] - Centre).Length());
        for (int i = 0; i < Vertices.Length; i++)
        {
            Vector2 a = Vertices[i], b = Vertices[(i + 1) % Vertices.Length], c = Vertices[(i + 2) % Vertices.Length];
            double turn = ((double)b.X-a.X)*((double)c.Y-b.Y)-((double)b.Y-a.Y)*((double)c.X-b.X);
            if (turn < 0) throw new ArgumentException("Nonconvex snow collider", nameof(vertices));
        }
    }
    internal SnowBoundary Sample(Vector2 position)
    {
        if (Vertices.Length == 0)
        {
            Vector2 delta = position - Centre; float length = delta.Length();
            return new SnowBoundary(length - Radius, length > 1e-9f ? delta / length : Vector2.UnitY);
        }
        float best = float.PositiveInfinity, separation = float.NegativeInfinity;
        Vector2 normal = default, faceNormal = default;
        for (int i = 0; i < Vertices.Length; i++)
        {
            Vector2 a = Vertices[i], edge = Vertices[(i + 1) % Vertices.Length] - a;
            float lengthSquared = edge.LengthSquared(); if (lengthSquared < 1e-16f) continue;
            Vector2 n = new Vector2(edge.Y, -edge.X) / MathF.Sqrt(lengthSquared), d = position - a;
            float signed = Vector2.Dot(d, n);
            if (signed > separation) { separation = signed; faceNormal = n; }
            Vector2 nearest = d - edge * Math.Clamp(Vector2.Dot(d, edge) / lengthSquared, 0, 1);
            float distance = nearest.LengthSquared();
            if (distance < best) { best = distance; normal = nearest; }
        }
        if (separation <= 0) return new SnowBoundary(separation, faceNormal);
        float radius = MathF.Sqrt(best);
        return new SnowBoundary(radius, radius > 1e-9f ? normal / radius : faceNormal);
    }
}

internal partial struct SnowRigid
{
    internal SnowPosition Position, Previous;
    internal float Rotation, PreviousRotation, InverseMass, InverseInertia;
    internal float FrameSeconds, LinearResponseSeconds, AngularResponseSeconds;
    internal SnowPosition FrameOrigin;
    internal float FrameRotation, FrameElapsed, FreeAngularVelocity;
    internal Vector2 FreeVelocity, Velocity;
    internal float AngularVelocity;
    internal SnowCollider[] Shapes;
    internal Vector2 Impulse;
    internal float AngularImpulse;
    internal uint NativeFrame;
    // How far from this body unsettled snow stays live; zero means the material's default. A boot has no business keeping snow moving metres away.
    internal float Horizon;
    internal readonly float ContactInverseMass => InverseMass * (FrameSeconds > 0 ? LinearResponseSeconds / FrameSeconds : 1);
    internal readonly float ContactInverseInertia => InverseInertia * (FrameSeconds > 0 ? AngularResponseSeconds / FrameSeconds : 1);
    internal void BeginStep(float dt)
    {
        if (FrameSeconds <= 0) return;
        if (!float.IsFinite(dt) || dt <= 0 || !float.IsFinite(FrameElapsed) || FrameElapsed < 0 || FrameElapsed + dt > FrameSeconds * 1.00001f)
            throw new InvalidOperationException("Snow rigid advanced beyond its native frame");
        Previous = Position; PreviousRotation = Rotation;
        Position += Velocity * dt; Rotation += AngularVelocity * dt; FrameElapsed += dt;
    }
    internal void EndStep(float dt)
    {
        if (FrameSeconds <= 0) return;
        Velocity = (Position - Previous) / dt; AngularVelocity = (Rotation - PreviousRotation) / dt;
    }

    internal readonly Vector2 Local(SnowPosition world)
    {
        float c = MathF.Cos(Rotation), s = MathF.Sin(Rotation); Vector2 d = world - Position;
        return new Vector2(c * d.X + s * d.Y, -s * d.X + c * d.Y);
    }
    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
}
