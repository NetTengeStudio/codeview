using System;
using System.Runtime.CompilerServices;

namespace ScpItdMod.Motion {
    internal static class MotionCurve<T, TOps> where T : struct, IEquatable<T> where TOps : struct, IMotionOps<T> {
        private static readonly TOps Ops = default;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Spring(in T from, in T target, in T initialVelocity, in SpringMatrix matrix, out T value, out T velocity) {
            T displacement = Ops.Subtract(from, target);
            value = Ops.Add(target, Ops.Add(Ops.Scale(displacement, matrix.XX), Ops.Scale(initialVelocity, matrix.XV)));
            velocity = Ops.Add(Ops.Scale(displacement, matrix.VX), Ops.Scale(initialVelocity, matrix.VV));
        }
        internal static void Evaluate(in T from, in T target, in T initialVelocity, in MotionTransition transition,
            double age, in SpringMatrix matrix, out T value, out T velocity) {
            if (!transition.Valid) { value = target; velocity = default; return; }
            if (age <= 0) { value = from; velocity = initialVelocity; return; }
            if (transition.Kind == MotionKind.Spring) {
                Spring(from, target, initialVelocity, matrix, out value, out velocity);
                return;
            }
            if (age >= transition.Duration) { value = target; velocity = default; return; }
            float t = (float)(age / transition.Duration), t2 = t * t, progress, derivative;
            switch (transition.Kind) {
                case MotionKind.Smoother: progress = MotionMath.Smoother(t); derivative = 30 * t2 * (t - 1) * (t - 1); break;
                case MotionKind.Linear: progress = t; derivative = 1; break;
                default: progress = MotionMath.Smooth(t); derivative = 6 * t * (1 - t); break;
            }
            T delta = Ops.Subtract(target, from);
            value = Ops.Add(from, Ops.Scale(delta, progress)); velocity = Ops.Scale(delta, derivative / transition.Duration);
            if (transition.Kind == MotionKind.Hermite) {
                value = Ops.Add(value, Ops.Scale(initialVelocity, transition.Duration * (t * t2 - 2 * t2 + t)));
                velocity = Ops.Add(velocity, Ops.Scale(initialVelocity, 3 * t2 - 4 * t + 1));
            }
        }
    }

    internal readonly struct MotionRecord<T> where T : struct, IEquatable<T> {
        internal readonly T From, Target, Velocity;
        internal readonly MotionTransition Transition;
        internal readonly double Started, Ended;
        internal MotionRecord(in T from, in T target, in T velocity, in MotionTransition transition, double started, double ended) {
            From = from; Target = target; Velocity = velocity; Transition = transition; Started = started; Ended = ended;
        }
    }

    internal sealed class MotionHistory<T> where T : struct, IEquatable<T> {
        internal const int Capacity = 8;
        private readonly MotionRecord<T>[] records = new MotionRecord<T>[Capacity];
        private int cursor, count;
        internal bool Empty => count == 0;
        internal void Clear() { cursor = count = 0; }
        internal void Record(in MotionRecord<T> record) {
            if (record.Ended <= record.Started) { return; }
            records[cursor] = record; cursor = (cursor + 1) & (Capacity - 1); count = Math.Min(count + 1, Capacity);
        }
        internal bool Find(double time, out MotionRecord<T> record) {
            record = default;
            for (int i = 0; i < count; i++) {
                record = records[(cursor - i - 1 + Capacity) & (Capacity - 1)];
                if (time >= record.Started) { return true; }
            }
            return count != 0;
        }
    }
}
