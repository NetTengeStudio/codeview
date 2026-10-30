using System;

namespace ScpItdMod.Motion {
    public enum MotionKind : byte { Spring, Hermite, Smooth, Smoother, Linear }
    public readonly struct MotionTransition : IEquatable<MotionTransition> {
        public readonly MotionKind Kind;
        public readonly float Duration, Omega, Damping;
        public readonly int Priority;
        private MotionTransition(MotionKind kind, float duration, float omega, float damping, int priority) {
            Kind = kind; Duration = duration; Omega = omega; Damping = damping; Priority = priority;
        }
        public static MotionTransition Spring(float omega = 20, float damping = 1, int priority = 0) {
            if (!float.IsFinite(omega) || omega <= 0 || omega > 10000 || !float.IsFinite(damping) || damping < 0 || damping > 1000) { throw new ArgumentOutOfRangeException(nameof(omega)); }
            return new MotionTransition(MotionKind.Spring, 0, omega, damping, priority);
        }
        public static MotionTransition Tween(float seconds, MotionKind kind = MotionKind.Hermite, int priority = 0) {
            if (!float.IsFinite(seconds) || seconds <= 0 || kind == MotionKind.Spring || kind > MotionKind.Linear) { throw new ArgumentOutOfRangeException(nameof(seconds)); }
            return new MotionTransition(kind, seconds, 0, 0, priority);
        }
        internal MotionTransition AtSpeed(float speed) => Kind == MotionKind.Spring ? Spring(Omega * speed, Damping, Priority) : Tween(Duration / speed, Kind, Priority);
        internal bool Valid => Kind == MotionKind.Spring ? Omega > 0 : Duration > 0;
        public bool Equals(MotionTransition other) => Kind == other.Kind && Duration == other.Duration && Omega == other.Omega && Damping == other.Damping && Priority == other.Priority;
        public override bool Equals(object other) => other is MotionTransition transition && Equals(transition);
        public override int GetHashCode() => HashCode.Combine(Kind, Duration, Omega, Damping, Priority);
    }
    public static class MotionPresets {
        public static readonly MotionTransition Layout = MotionTransition.Spring(30, 1);
        public static readonly MotionTransition Card = MotionTransition.Spring(26, 0.86f);
        public static readonly MotionTransition Hover = MotionTransition.Spring(42, 0.9f);
        public static readonly MotionTransition Press = MotionTransition.Spring(45, 1, 1);
        public static readonly MotionTransition Pop = MotionTransition.Spring(28, 0.66f);
        public static readonly MotionTransition Window = MotionTransition.Spring(32, 1);
        public static readonly MotionTransition Paper = MotionTransition.Spring(21, 0.88f);
        public static readonly MotionTransition Fade = MotionTransition.Tween(0.18f, MotionKind.Smoother);
        public static readonly MotionTransition Notification = MotionTransition.Spring(24, 1);
        public static double Stagger(int index, double interval = 0.045) {
            if (index < 0 || !double.IsFinite(interval) || interval < 0) { throw new ArgumentOutOfRangeException(nameof(index)); }
            return index * interval;
        }
    }
}
