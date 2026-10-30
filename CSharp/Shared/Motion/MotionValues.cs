using System;

namespace ScpItdMod.Motion {
    public readonly struct Vec3 {
        public readonly float X, Y, Z;
        public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }
        public static Vec3 UnitY => new Vec3(0, 1, 0);
        public static Vec3 UnitZ => new Vec3(0, 0, 1);
        public float Length() => MathF.Sqrt(X * X + Y * Y + Z * Z);
        public static Vec3 operator /(Vec3 value, float scale) => new Vec3(value.X / scale, value.Y / scale, value.Z / scale);
    }

    public readonly struct Vec4 : IEquatable<Vec4> {
        public readonly float X, Y, Z, W;
        public Vec4(float value) : this(value, value, value, value) { }
        public Vec4(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
        public static Vec4 Zero => default;
        public static Vec4 operator +(Vec4 a, Vec4 b) => new Vec4(a.X + b.X, a.Y + b.Y, a.Z + b.Z, a.W + b.W);
        public static Vec4 operator -(Vec4 a, Vec4 b) => new Vec4(a.X - b.X, a.Y - b.Y, a.Z - b.Z, a.W - b.W);
        public static Vec4 operator *(Vec4 value, float scale) => new Vec4(value.X * scale, value.Y * scale, value.Z * scale, value.W * scale);
        public bool Equals(Vec4 other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z) && W.Equals(other.W);
        public override bool Equals(object obj) => obj is Vec4 other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(X, Y, Z, W);
    }

    public readonly struct Rotation3 : IEquatable<Rotation3> {
        public readonly float X, Y, Z, W;
        public Rotation3(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
        public static Rotation3 Identity => new Rotation3(0, 0, 0, 1);
        public float LengthSquared() => X * X + Y * Y + Z * Z + W * W;
        public float Length() => MathF.Sqrt(LengthSquared());
        public static float Dot(Rotation3 a, Rotation3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
        public static Rotation3 Normalize(Rotation3 value) {
            float inverse = 1 / value.Length();
            return new Rotation3(value.X * inverse, value.Y * inverse, value.Z * inverse, value.W * inverse);
        }
        public static Rotation3 Conjugate(Rotation3 value) => new Rotation3(-value.X, -value.Y, -value.Z, value.W);
        public static Rotation3 CreateFromAxisAngle(Vec3 unitAxis, float angle) {
            float sine = MathF.Sin(angle * 0.5f), cosine = MathF.Cos(angle * 0.5f);
            return new Rotation3(unitAxis.X * sine, unitAxis.Y * sine, unitAxis.Z * sine, cosine);
        }
        public static Rotation3 operator *(Rotation3 a, Rotation3 b) => new Rotation3(
            a.X * b.W + a.W * b.X + a.Y * b.Z - a.Z * b.Y,
            a.Y * b.W + a.W * b.Y + a.Z * b.X - a.X * b.Z,
            a.Z * b.W + a.W * b.Z + a.X * b.Y - a.Y * b.X,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);
        public static Rotation3 Slerp(Rotation3 a, Rotation3 b, float t) {
            float cosine = Dot(a, b), sign = 1;
            if (cosine < 0) { cosine = -cosine; sign = -1; }
            float left, right;
            if (cosine > 1 - 1e-6f) { left = 1 - t; right = t * sign; }
            else {
                float angle = MathF.Acos(cosine), inverseSine = 1 / MathF.Sin(angle);
                left = MathF.Sin((1 - t) * angle) * inverseSine;
                right = MathF.Sin(t * angle) * inverseSine * sign;
            }
            return new Rotation3(a.X * left + b.X * right, a.Y * left + b.Y * right, a.Z * left + b.Z * right, a.W * left + b.W * right);
        }
        public static bool operator ==(Rotation3 a, Rotation3 b) => a.X == b.X && a.Y == b.Y && a.Z == b.Z && a.W == b.W;
        public static bool operator !=(Rotation3 a, Rotation3 b) => !(a == b);
        public bool Equals(Rotation3 other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z) && W.Equals(other.W);
        public override bool Equals(object obj) => obj is Rotation3 other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(X, Y, Z, W);
    }

    public readonly struct Affine2 {
        public readonly float M11, M12, M21, M22, M31, M32;
        public Affine2(float m11, float m12, float m21, float m22, float m31, float m32) {
            M11 = m11; M12 = m12; M21 = m21; M22 = m22; M31 = m31; M32 = m32;
        }
        public Core.Vec2 Translation => new Core.Vec2(M31, M32);
        public float GetDeterminant() => M11 * M22 - M21 * M12;
        public static Affine2 Identity => new Affine2(1, 0, 0, 1, 0, 0);
        public static Affine2 CreateScale(Core.Vec2 scale) => CreateScale(scale.X, scale.Y);
        public static Affine2 CreateScale(float x, float y) => new Affine2(x, 0, 0, y, 0, 0);
        public static Affine2 CreateTranslation(Core.Vec2 translation) => CreateTranslation(translation.X, translation.Y);
        public static Affine2 CreateTranslation(float x, float y) => new Affine2(1, 0, 0, 1, x, y);
        public static Affine2 CreateRotation(float angle) {
            float cosine = MathF.Cos(angle), sine = MathF.Sin(angle);
            return new Affine2(cosine, sine, -sine, cosine, 0, 0);
        }
        public static Affine2 operator *(Affine2 a, Affine2 b) => new Affine2(
            a.M11 * b.M11 + a.M12 * b.M21, a.M11 * b.M12 + a.M12 * b.M22,
            a.M21 * b.M11 + a.M22 * b.M21, a.M21 * b.M12 + a.M22 * b.M22,
            a.M31 * b.M11 + a.M32 * b.M21 + b.M31, a.M31 * b.M12 + a.M32 * b.M22 + b.M32);
    }
}
