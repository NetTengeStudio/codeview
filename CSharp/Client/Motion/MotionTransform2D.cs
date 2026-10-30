using System;
using Vector2 = ScpItdMod.Core.Vec2;
using Matrix3x2 = ScpItdMod.Motion.Affine2;

namespace ScpItdMod.Motion {
    public sealed class MotionTransform2D {
        public readonly MotionHandle<Vector2> Translation;
        private readonly MotionHandle<Vector2> logScale;
        public Vector2 Scale { get { Vector2 value = logScale.Value; return new Vector2(MathF.Exp(value.X), MathF.Exp(value.Y)); } }
        public readonly MotionHandle<float> Rotation;
        public MotionTransform2D(MotionScope scope, Matrix3x2 initial) {
            if (scope == null) { throw new ArgumentNullException(nameof(scope)); }
            Decompose(initial, out Vector2 translation, out Vector2 scale, out float angle);
            Translation = scope.Vector(translation); logScale = scope.Vector(new Vector2(MathF.Log(scale.X), MathF.Log(scale.Y))); Rotation = scope.Angle(angle);
        }
        public Matrix3x2 Matrix => Matrix3x2.CreateScale(Scale) * Matrix3x2.CreateRotation(Rotation.Value) * Matrix3x2.CreateTranslation(Translation.Value);
        public void To(Matrix3x2 target, MotionTransition transition) {
            Decompose(target, out Vector2 translation, out Vector2 scale, out float angle);
            Translation.To(translation, transition); logScale.To(new Vector2(MathF.Log(scale.X), MathF.Log(scale.Y)), transition); Rotation.To(angle, transition);
        }
        private static void Decompose(Matrix3x2 matrix, out Vector2 translation, out Vector2 scale, out float angle) {
            var x = new Vector2(matrix.M11, matrix.M12); var y = new Vector2(matrix.M21, matrix.M22);
            translation = matrix.Translation; float sx = x.Length(), sy = y.Length();
            if (!float.IsFinite(sx) || !float.IsFinite(sy) || !float.IsFinite(translation.X) || !float.IsFinite(translation.Y) || sx < 0.0001f || sy < 0.0001f || matrix.GetDeterminant() <= 0 || MathF.Abs(Vector2.Dot(x / sx, y / sy)) > 0.0001f) {
                throw new ArgumentException("Motion transform requires finite positive scale and rotation without shear or reflection.", nameof(matrix));
            }
            scale = new Vector2(sx, sy); angle = MathF.Atan2(x.Y, x.X);
        }
    }
}
