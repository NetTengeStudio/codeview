using System;
using System.Runtime.CompilerServices;

namespace ScpItdMod.Core {
    public readonly struct Vec2 : IEquatable<Vec2> {
        public readonly float X;
        public readonly float Y;

        public Vec2(float x, float y) {
            X = x;
            Y = y;
        }
        public Vec2(float value) : this(value, value) { }

        public static Vec2 Zero => default;

        public static Vec2 One => new Vec2(1, 1);
        public static Vec2 UnitX => new Vec2(1, 0);
        public static Vec2 UnitY => new Vec2(0, 1);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float LengthSquared() => X * X + Y * Y;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Length() => MathF.Sqrt(X * X + Y * Y);

        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);
        public static Vec2 operator -(Vec2 a) => new Vec2(-a.X, -a.Y);
        public static Vec2 operator *(Vec2 a, float k) => new Vec2(a.X * k, a.Y * k);
        public static Vec2 operator *(float k, Vec2 a) => new Vec2(a.X * k, a.Y * k);
        public static Vec2 operator *(Vec2 a, Vec2 b) => new Vec2(a.X * b.X, a.Y * b.Y);
        public static Vec2 operator /(Vec2 a, float k) => new Vec2(a.X / k, a.Y / k);
        public static bool operator ==(Vec2 a, Vec2 b) => a.X == b.X && a.Y == b.Y;
        public static bool operator !=(Vec2 a, Vec2 b) => !(a == b);

        public static float Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;
        public static Vec2 Min(Vec2 a, Vec2 b) => new Vec2(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y));
        public static Vec2 Max(Vec2 a, Vec2 b) => new Vec2(MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y));
        public static float Distance(Vec2 a, Vec2 b) => (a - b).Length();
        public static float DistanceSquared(Vec2 a, Vec2 b) => (a - b).LengthSquared();
        public static Vec2 Lerp(Vec2 a, Vec2 b, float t) => new Vec2(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

        public static Vec2 Normalize(Vec2 v) {
            float l = v.Length();
            return l > 0 ? v / l : default;
        }

        public bool Equals(Vec2 o) => this == o;

        public override bool Equals(object obj) => obj is Vec2 o && this == o;

        public override int GetHashCode() => HashCode.Combine(X, Y);

        public override string ToString() => $"({X}, {Y})";
    }
}
