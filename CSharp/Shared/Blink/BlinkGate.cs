using Barotrauma;
using ScpItdMod.Core;

namespace ScpItdMod.Blink {
    public static class BlinkGate {
        public static bool Forced { get; set; }

        public static bool Active(Entity target) => Forced || Watchers.Any(target);
    }
}
