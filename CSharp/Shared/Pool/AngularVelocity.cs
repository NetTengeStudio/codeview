using System;

namespace ScpItdMod.Pool {
    public struct AngularVelocity {
        public float X, Y, Z;
        public AngularVelocity(float x, float y, float z) { X = x; Y = y; Z = z; }
        public bool IsZero => X == 0 && Y == 0 && Z == 0;
        public float LengthSquared() => X * X + Y * Y + Z * Z;
        public float Length() => MathF.Sqrt(LengthSquared());
    }
}
