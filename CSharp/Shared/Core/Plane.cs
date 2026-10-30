using System;

namespace ScpItdMod.Core {
    public readonly struct Plane {
        public readonly Vec2 Origin, X, Y;
        private readonly float inverse;
        public Plane(Vec2 origin, Vec2 x, Vec2 y) {
            float determinant = x.X * y.Y - x.Y * y.X;
            if (!float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || !float.IsFinite(determinant) || MathF.Abs(determinant) < 1e-8f) { throw new ArgumentException("Degenerate surface"); }
            Origin = origin; X = x; Y = y; inverse = 1 / determinant;
        }
        public Vec2 Point(Vec2 position) => Origin + X * position.X + Y * position.Y;
        public Vec2 Position(Vec2 point) { point -= Origin; return new Vec2((point.X * Y.Y - point.Y * Y.X) * inverse, (X.X * point.Y - X.Y * point.X) * inverse); }
    }
}
