using System;
using Vector2 = ScpItdMod.Core.Vec2;
using Vector3 = ScpItdMod.Motion.Vec3;
using Vector4 = ScpItdMod.Motion.Vec4;
using Quaternion = ScpItdMod.Motion.Rotation3;

namespace ScpItdMod.Motion {
    public readonly struct SpringMatrix {
        public readonly float XX, XV, VX, VV;
        public SpringMatrix(double omega, double damping, double seconds) {
            if (!double.IsFinite(omega) || omega <= 0 || omega > 10000 || !double.IsFinite(damping) || damping < 0 || damping > 1000 || !double.IsFinite(seconds) || seconds < 0) {
                throw new ArgumentOutOfRangeException(nameof(omega));
            }
            double a = damping * omega, discriminant = omega * omega * (damping * damping - 1);
            double c, s;
            if (seconds == 0) { XX = VV = 1; XV = VX = 0; return; }
            if (discriminant <= 0) {
                double w = Math.Sqrt(-discriminant), x = w * seconds, e = Math.Exp(-a * seconds);
                c = e * Math.Cos(x);
                s = e == 0 ? 0 : e * seconds * Sinc(x);
            } else {
                double w = Math.Sqrt(discriminant), x = w * seconds;
                if (x < 0.001) {
                    double e = Math.Exp(-a * seconds), x2 = x * x;
                    c = e * (1 + x2 * (0.5 + x2 / 24));
                    s = e * seconds * (1 + x2 * (1.0 / 6 + x2 / 120));
                } else {
                    double q = damping + Math.Sqrt(damping * damping - 1);
                    double slow = Math.Exp(-omega / q * seconds), fast = Math.Exp(-omega * q * seconds);
                    c = (slow + fast) * 0.5; s = (slow - fast) / (2 * w);
                }
            }
            XX = (float)(c + a * s); XV = (float)s;
            VX = (float)(-omega * omega * s); VV = (float)(c - a * s);
        }
        private static double Sinc(double x) {
            if (Math.Abs(x) > 0.001) { return Math.Sin(x) / x; }
            double x2 = x * x; return 1 + x2 * (-1.0 / 6 + x2 / 120);
        }
    }

    public static class MotionMath {
        public static float Smooth(float t) => t * t * (3 - 2 * t);
        public static float Smoother(float t) => t * t * t * (t * (6 * t - 15) + 10);
        public static float Angle(float from, float to, float t) => from + MathF.IEEERemainder(to - from, MathF.Tau) * t;
        public static float Hermite(float a, float va, float b, float vb, float t) {
            float t2 = t * t, t3 = t2 * t;
            return (2 * t3 - 3 * t2 + 1) * a + (t3 - 2 * t2 + t) * va + (-2 * t3 + 3 * t2) * b + (t3 - t2) * vb;
        }
        public static float Bezier(float a, float b, float c, float d, float t) {
            float s = 1 - t; return s * s * s * a + 3 * s * s * t * b + 3 * s * t * t * c + t * t * t * d;
        }
        public static float CatmullRom(float a, float b, float c, float d, float t) =>
            Hermite(b, (c - a) * 0.5f, c, (d - b) * 0.5f, t);
        public static float Monotone(float a, float b, float slopeA, float slopeB, float t) {
            double delta = (double)b - a;
            if (delta == 0) { return a; }
            double x = Math.Max(0, slopeA / delta), y = Math.Max(0, slopeB / delta);
            double length = Math.Sqrt(x * x + y * y);
            if (length > 3) { x *= 3 / length; y *= 3 / length; }
            return Hermite(a, (float)(x * delta), b, (float)(y * delta), t);
        }
        public static Quaternion Integrate(Quaternion orientation, Vector3 worldVelocity, double seconds) {
            double length = worldVelocity.Length(), angle = length * seconds;
            if (length < 1e-12 || seconds == 0) { return orientation; }
            var delta = Quaternion.CreateFromAxisAngle(worldVelocity / (float)length, (float)Math.IEEERemainder(angle, Math.Tau));
            return Quaternion.Normalize(delta * orientation);
        }
        public static Vector4 LinearColor(byte r, byte g, byte b, byte a = 255) => new Vector4(Linear(r), Linear(g), Linear(b), a / 255f);
        private static float Linear(byte value) {
            float c = value / 255f; return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }
    }

    public interface IMotionOps<T> where T : struct, IEquatable<T> {
        T Add(T a, T b);
        T Subtract(T a, T b);
        T Scale(T a, float scale);
        bool Finite(T value);
        float Magnitude(T value);
    }
    public readonly struct ScalarOps : IMotionOps<float> {
        public float Add(float a, float b) => a + b;
        public float Subtract(float a, float b) => a - b;
        public float Scale(float a, float scale) => a * scale;
        public bool Finite(float value) => float.IsFinite(value);
        public float Magnitude(float value) => MathF.Abs(value);
    }
    public readonly struct AngleOps : IMotionOps<float> {
        public float Add(float a, float b) => a + b;
        public float Subtract(float a, float b) => MathF.IEEERemainder(a - b, MathF.Tau);
        public float Scale(float a, float scale) => a * scale;
        public bool Finite(float value) => float.IsFinite(value);
        public float Magnitude(float value) => MathF.Abs(value);
    }
    public readonly struct VectorOps : IMotionOps<Vector2> {
        public Vector2 Add(Vector2 a, Vector2 b) => a + b;
        public Vector2 Subtract(Vector2 a, Vector2 b) => a - b;
        public Vector2 Scale(Vector2 a, float scale) => a * scale;
        public bool Finite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
        public float Magnitude(Vector2 value) => MathF.Max(MathF.Abs(value.X), MathF.Abs(value.Y));
    }
    public readonly struct ColorOps : IMotionOps<Vector4> {
        public Vector4 Add(Vector4 a, Vector4 b) => a + b;
        public Vector4 Subtract(Vector4 a, Vector4 b) => a - b;
        public Vector4 Scale(Vector4 a, float scale) => a * scale;
        public bool Finite(Vector4 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
        public float Magnitude(Vector4 value) => MathF.Max(MathF.Max(MathF.Abs(value.X), MathF.Abs(value.Y)), MathF.Max(MathF.Abs(value.Z), MathF.Abs(value.W)));
    }
    public static class MotionBlend<T, TOps> where T : struct, IEquatable<T> where TOps : struct, IMotionOps<T> {
        private static readonly TOps Ops = default;
        public static T Override(T value, T layer, float weight) => Ops.Add(value, Ops.Scale(Ops.Subtract(layer, value), Math.Clamp(weight, 0, 1)));
        public static T Add(T value, T layer, float weight = 1) => Ops.Add(value, Ops.Scale(layer, weight));
    }
}
