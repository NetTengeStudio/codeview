using System;

namespace ScpItdMod.Thermal {
    public static class ThermalBreath {
        public static float Condensation(float air, float humidity) {
            if (!float.IsFinite(air) || !float.IsFinite(humidity) || air < -90 || air > 150 || humidity < 0 || humidity > 1) throw new ArgumentOutOfRangeException(nameof(air));
            const float Exhaled = 34;
            if (air >= Exhaled) return 0;
            float ambient = humidity * ThermalBody.Saturation(air), exhaled = ThermalBody.Saturation(Exhaled), excess = 0;
            for (int i = 1; i < 8; i++) {
                float share = i / 8f;
                float mixed = air + (Exhaled - air) * share;
                excess = MathF.Max(excess, ambient + (exhaled - ambient) * share - ThermalBody.Saturation(mixed));
            }
            return Math.Clamp(excess / exhaled, 0, 1);
        }
    }
}
