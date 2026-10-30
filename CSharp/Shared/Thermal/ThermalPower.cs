using System;

namespace ScpItdMod.Thermal {
    public static class ThermalPower {
        public static float Draw(ref float condition, float watts, float joulesPerCondition, float dt) {
            if (!float.IsFinite(condition) || condition < 0 || !float.IsFinite(watts) || watts < 0 || !float.IsFinite(joulesPerCondition) || joulesPerCondition <= 0 || !float.IsFinite(dt) || dt <= 0) throw new ArgumentOutOfRangeException(nameof(condition));
            double available = condition * (double)joulesPerCondition;
            double requested = watts * (double)dt;
            double delivered = Math.Min(available, requested);
            condition = (float)Math.Max(0, (available - delivered) / joulesPerCondition);
            return (float)(delivered / dt);
        }
    }
}
