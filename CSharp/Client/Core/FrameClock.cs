using Barotrauma;

namespace ScpItdMod.Core {
    // The one visual timeline of the mod. It is ticked once per frame by the motion client with the same monotonic
    // clock the motion engine integrates on, so HUD fades, animations and springs advance on a single shared timeline.
    // A paused frame has a zero step. A long stall advances by at most MaxStep, so an integrator never jumps and never freezes.
    public static class FrameClock {
        public const float MaxStep = 0.25f;

        private static double last = double.NaN;

        public static float Step { get; private set; }

        public static uint Frame { get; private set; }

        public static void Tick(double now, bool paused) {
            if (double.IsNaN(last)) {
                last = now; Step = 0;
            } else {
                double delta = now - last;
                last = now;
                Step = paused || !(delta > 0) ? 0f : (float)System.Math.Min(delta, MaxStep);
            }
            Frame++;
        }
    }
}
