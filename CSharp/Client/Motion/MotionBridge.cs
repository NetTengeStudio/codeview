using System;

namespace ScpItdMod.Motion {
    public static class MotionBridge {
        public const int Version = 1;
        private static MotionRuntime runtime;
        public static bool IsActive => runtime != null;
        internal static void Connect(MotionRuntime engine) => runtime = engine ?? throw new ArgumentNullException(nameof(engine));
        internal static void Disconnect() => runtime = null;
        private sealed class Lease : IDisposable {
            internal readonly MotionScope Scope;
            internal readonly MotionHandle<float>[] Values;
            internal readonly MotionSequence Reveal;
            internal MotionPlayback Playback;
            internal bool Visible;
            internal Lease(float[] initial) {
                Scope = runtime.Scope();
                Values = new MotionHandle<float>[initial.Length];
                for (int i = 0; i < initial.Length; i++) Values[i] = Scope.Scalar(initial[i]);
                Reveal = new MotionSequenceBuilder().To(Values[0], 1f, MotionTransition.Spring(22f, 1f)).Compile();
            }
            public void Dispose() => Scope.Dispose();
        }
        private static readonly MotionTransition Needle = MotionTransition.Spring(24f, 1f);
        private static readonly MotionTransition SlowNeedle = MotionTransition.Spring(12f, 1f);
        private static readonly MotionTransition Switch = MotionTransition.Spring(36f, 1f);
        public static object Open(float[] initial) {
            if (runtime == null) throw new InvalidOperationException("The motion engine is not attached.");
            if (initial == null || initial.Length < 1 || initial.Length > 64) throw new ArgumentException("Motion property count must be in [1,64].");
            for (int i = 0; i < initial.Length; i++) if (!float.IsFinite(initial[i])) throw new ArgumentException("Motion properties must be finite.");
            return new Lease(initial);
        }
        public static bool Alive(object lease) => lease is Lease value && value.Scope.Alive;
        public static void Target(object lease, int index, float target, byte profile) {
            Lease value = (Lease)lease;
            value.Values[index].To(target, profile == 1 ? SlowNeedle : profile == 2 ? Switch : Needle);
        }
        public static float Read(object lease, int index) => ((Lease)lease).Values[index].Value;
        public static void Impulse(object lease, int index, float velocity) => ((Lease)lease).Values[index].Impulse(velocity);
        public static void Show(object lease, bool visible) {
            Lease value = (Lease)lease;
            if (value.Visible == visible) return;
            value.Visible = visible;
            value.Playback?.Cancel();
            if (visible) value.Playback = value.Scope.Play(value.Reveal);
            else value.Values[0].To(0f, MotionPresets.Fade);
        }
        public static void Close(object lease) { if (lease is Lease value) value.Dispose(); }
    }
}
