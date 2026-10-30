using System;
using Vector2 = ScpItdMod.Core.Vec2;

namespace ScpItdMod.Weather.Material;

internal readonly struct SnowPosition
{
    private readonly double x, y;
    internal float X => (float)x;
    internal float Y => (float)y;
    internal double PreciseX => x;
    internal double PreciseY => y;
    internal SnowPosition(double x, double y) { this.x = x; this.y = y; }
    public static implicit operator SnowPosition(Vector2 value) => new(value.X, value.Y);
    public static implicit operator Vector2(SnowPosition value) => new(value.X, value.Y);
    public static SnowPosition operator +(SnowPosition a, Vector2 b) => new(a.x + b.X, a.y + b.Y);
    public static SnowPosition operator +(Vector2 a, SnowPosition b) => b + a;
    public static SnowPosition operator +(SnowPosition a, SnowPosition b) => new(a.x + b.x, a.y + b.y);
    public static Vector2 operator -(SnowPosition a, SnowPosition b) => new((float)(a.x - b.x), (float)(a.y - b.y));
    public static SnowPosition operator -(SnowPosition a, Vector2 b) => new(a.x - b.X, a.y - b.Y);
    public static SnowPosition operator -(Vector2 a, SnowPosition b) => new(a.X - b.x, a.Y - b.y);
    public static SnowPosition operator *(SnowPosition a, float b) => new(a.x * b, a.y * b);
    public static SnowPosition operator /(SnowPosition a, float b) => new(a.x / b, a.y / b);
    internal static float DistanceSquared(SnowPosition a, SnowPosition b) => (a - b).LengthSquared();
    internal static float Distance(SnowPosition a, SnowPosition b) => (a - b).Length();
}
