using System.Diagnostics;

namespace ScpItdMod.Hud {
    public static class HudClock {
        private static readonly double Tick = 1.0 / Stopwatch.Frequency;

        public static double Now => Stopwatch.GetTimestamp() * Tick;
    }
}
