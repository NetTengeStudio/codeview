using System;
using Vector3 = ScpItdMod.Motion.Vec3;
using Quaternion = ScpItdMod.Motion.Rotation3;
using ScpItdMod.Motion;

namespace ScpItdMod.Pool {
    public static class BallRotation {
        private static readonly Quaternion Reference = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2);
        public static Quaternion Advance(Quaternion orientation, in AngularVelocity velocity, double seconds) =>
            MotionMath.Integrate(Unit(orientation), new Vector3(velocity.X, velocity.Y, velocity.Z), seconds);
        public static Quaternion Unit(Quaternion orientation) => orientation.LengthSquared() > 0.0001f ? Quaternion.Normalize(orientation) : Quaternion.Identity;
        public static Quaternion ToRender(Quaternion physical) {
            physical = Unit(physical);
            return new Quaternion(-physical.X, physical.Y, -physical.Z, physical.W) * Reference;
        }
        public static Quaternion Interpolate(in Ball a, in Ball b, float seconds, float t) {
            if (seconds <= 0 || t >= 1) { return Unit(b.Orientation); }
            if (t <= 0) { return Unit(a.Orientation); }
            AngularVelocity start = a.Spin, end = b.Spin;
            var partial = new AngularVelocity(start.X + (end.X - start.X) * t * 0.5f, start.Y + (end.Y - start.Y) * t * 0.5f, start.Z + (end.Z - start.Z) * t * 0.5f);
            var average = new AngularVelocity((start.X + end.X) * 0.5f, (start.Y + end.Y) * 0.5f, (start.Z + end.Z) * 0.5f);
            Quaternion predicted = Advance(a.Orientation, partial, seconds * t), predictedEnd = Advance(a.Orientation, average, seconds);
            Quaternion correction = Unit(b.Orientation) * Quaternion.Conjugate(predictedEnd);
            return Quaternion.Normalize(Quaternion.Slerp(Quaternion.Identity, correction, MotionMath.Smoother(t)) * predicted);
        }
        public static bool Changed(in Ball a, in Ball b) => a.Position != b.Position || a.Velocity != b.Velocity || a.Orientation != b.Orientation ||
            a.Spin.X != b.Spin.X || a.Spin.Y != b.Spin.Y || a.Spin.Z != b.Spin.Z || a.Pocket != b.Pocket;
    }
}
