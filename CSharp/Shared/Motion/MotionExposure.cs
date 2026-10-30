using System;
using ScpItdMod.Core;

namespace ScpItdMod.Motion {
    public readonly struct MotionPose2D {
        public readonly Vec2 Position, Size;
        public readonly float Angle, Opacity;
        public MotionPose2D(Vec2 position, Vec2 size, float angle = 0, float opacity = 1) { Position = position; Size = size; Angle = angle; Opacity = Math.Clamp(opacity, 0, 1); }
        public Affine2 Matrix(Vec2 origin) => Affine2.CreateTranslation(-origin.X, -origin.Y) * Affine2.CreateScale(Size) * Affine2.CreateRotation(Angle) * Affine2.CreateTranslation(Position);
    }
    public interface IMotionPath2D {
        bool Alive { get; }
        MotionPose2D Sample(double secondsAgo);
    }
    public readonly struct MotionShutter {
        public const int MaxSamples = 32;
        public readonly double Seconds;
        public readonly float MaxPixels;
        public readonly int Samples;
        // UI text and item descriptions read at a glance: a short exposure and a 6 px travel cap keep the trail to a hint of motion.
        public static readonly MotionShutter Default = new MotionShutter(1d / 240, 6, MaxSamples);
        public MotionShutter(double seconds, float maxPixels = 16, int samples = MaxSamples) {
            if (!double.IsFinite(seconds) || seconds < 0 || seconds > 1d / 30 || !float.IsFinite(maxPixels) || maxPixels <= 0 || maxPixels > 64 || samples < 2 || samples > MaxSamples || (samples & 1) != 0) { throw new ArgumentOutOfRangeException(nameof(seconds)); }
            Seconds = seconds; MaxPixels = maxPixels; Samples = samples;
        }
    }
    public readonly struct MotionExposureTap {
        public readonly Vec4 X, Y;
        public MotionExposureTap(Vec4 x, Vec4 y) { X = x; Y = y; }
    }
    public readonly struct MotionFootprint {
        public readonly float Left, Top, Right, Bottom, Travel;
        public readonly double Seconds;
        public readonly int Samples;
        internal MotionFootprint(float left, float top, float right, float bottom, float travel, double seconds, int samples) {
            Left = left; Top = top; Right = right; Bottom = bottom; Travel = travel; Seconds = seconds; Samples = samples;
        }
    }
    public static class MotionExposure {
        private static Vec2 Point(in Affine2 m, float x, float y) => new Vec2(x * m.M11 + y * m.M21 + m.M31, x * m.M12 + y * m.M22 + m.M32);
        private static float Travel(in Affine2 a, in Affine2 b) {
            float maximum = 0;
            for (int corner = 0; corner < 4; corner++) { maximum = MathF.Max(maximum, (Point(a, corner & 1, corner >> 1) - Point(b, corner & 1, corner >> 1)).Length()); }
            return maximum;
        }
        private static float Sweep<T>(in T path, double exposure, Vec2 origin, in Affine2 view) where T : IMotionPath2D {
            Affine2 previous = path.Sample(0).Matrix(origin) * view; float travel = 0;
            for (int i = 1; i <= 4; i++) { Affine2 next = path.Sample(exposure * (i * .25)).Matrix(origin) * view; travel += Travel(previous, next); previous = next; }
            return travel;
        }
        private static void Include(in Affine2 matrix, ref Vec2 low, ref Vec2 high) {
            for (int corner = 0; corner < 4; corner++) {
                Vec2 p = Point(matrix, corner & 1, corner >> 1);
                low = new Vec2(MathF.Min(low.X, p.X), MathF.Min(low.Y, p.Y)); high = new Vec2(MathF.Max(high.X, p.X), MathF.Max(high.Y, p.Y));
            }
        }
        public static MotionFootprint Build<T>(in T path, in MotionShutter shutter, Vec2 origin, in Affine2 view, Span<MotionExposureTap> taps) where T : IMotionPath2D {
            if (!path.Alive || shutter.Seconds == 0 || shutter.Samples == 0) { return default; }
            if (taps.Length < shutter.Samples) { throw new ArgumentException("The exposure scratch span is smaller than the shutter sample budget.", nameof(taps)); }
            double exposure = shutter.Seconds; float travel = Sweep(path, exposure, origin, view);
            for (int reduction = 0; travel > shutter.MaxPixels && reduction < 4; reduction++) {
                exposure *= Math.Min(.9, shutter.MaxPixels / travel); travel = Sweep(path, exposure, origin, view);
            }
            int samples = Math.Clamp((int)MathF.Ceiling(travel) * 2, 2, shutter.Samples);
            samples += samples & 1; int strata = samples / 2;
            Vec2 low = new Vec2(float.PositiveInfinity, float.PositiveInfinity), high = new Vec2(float.NegativeInfinity, float.NegativeInfinity);
            Include(path.Sample(0).Matrix(origin) * view, ref low, ref high);
            Include(path.Sample(exposure).Matrix(origin) * view, ref low, ref high);
            const double gauss = .28867513459481288225;
            for (int i = 0; i < samples; i++) {
                double phase = (i / 2 + .5 + ((i & 1) == 0 ? -gauss : gauss)) / strata;
                MotionPose2D pose = path.Sample(exposure * phase); Affine2 matrix = pose.Matrix(origin) * view;
                float determinant = matrix.GetDeterminant();
                if (!float.IsFinite(determinant) || MathF.Abs(determinant) < 1e-12f) { return default; }
                float a = matrix.M22 / determinant, b = -matrix.M12 / determinant, c = -matrix.M21 / determinant, d = matrix.M11 / determinant;
                taps[i] = new MotionExposureTap(new Vec4(a, c, -(matrix.M31 * a + matrix.M32 * c), 1f / samples),
                    new Vec4(b, d, -(matrix.M31 * b + matrix.M32 * d), pose.Opacity));
                Include(matrix, ref low, ref high);
            }
            float guard = 1 + travel / samples;
            return new MotionFootprint(low.X - guard, low.Y - guard, high.X + guard, high.Y + guard, travel, exposure, samples);
        }
    }
}
