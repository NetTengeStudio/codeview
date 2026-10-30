using System;

namespace ScpItdMod.Thermal {
    public struct ThermalTraumaState {
        public float WetColdSeconds, FreezeDose, ThawedDose, ThawBurden, Sensitivity;
        public float PreviousFrozen, NerveFunction, Pain;
        public static ThermalTraumaState Healthy => new ThermalTraumaState { NerveFunction = 1 };
    }

    public static class ThermalTrauma {
        public const float WetColdLatency = 7200, WetColdDamageSeconds = 28800;
        public const float FreezingDamageSeconds = 900, ReperfusionSeconds = 600, ScarRecoverySeconds = 86400;
        public static void Step(ref ThermalTraumaState s, ref float injury, float temperature, float frozen, float wetness, float circulation, float dt) {
            if (!float.IsFinite(temperature + frozen + wetness + circulation + dt) || dt <= 0 || dt > 1 ||
                frozen < 0 || frozen > 1 || wetness < 0 || wetness > 1 || circulation < 0 || circulation > 1)
                throw new ArgumentOutOfRangeException(nameof(dt));
            float thawed = MathF.Max(0, s.PreviousFrozen - frozen);
            float refrozen = MathF.Max(0, frozen - s.PreviousFrozen);
            s.ThawedDose = Math.Clamp(s.ThawedDose * MathF.Exp(-dt / 7200) + thawed * s.FreezeDose, 0, 1);
            s.ThawBurden = Math.Clamp(s.ThawBurden * MathF.Exp(-dt / ReperfusionSeconds) + thawed * s.FreezeDose, 0, 1);
            float damage = frozen * (.2f + .8f * frozen) * dt / FreezingDamageSeconds;
            damage = damage * (1 + 2 * s.ThawedDose + s.Sensitivity) + refrozen * s.ThawedDose * .12f;
            s.FreezeDose = Math.Clamp(s.FreezeDose + frozen * dt / FreezingDamageSeconds - (frozen < .01f ? dt / 7200 : 0), 0, 1);
            float wetCold = temperature > 0 && temperature < 15 ? wetness * wetness * (15 - temperature) / 15 * (1 - .5f * circulation) : 0;
            float drying = wetness < .2f && temperature > 18 ? 1 : 0;
            s.WetColdSeconds = Math.Clamp(s.WetColdSeconds + (wetCold - drying) * dt, 0, WetColdLatency * 6);
            damage += Math.Clamp((s.WetColdSeconds - WetColdLatency) / WetColdLatency, 0, 2) * wetCold * dt / WetColdDamageSeconds;
            if (injury < .15f && temperature > 20 && wetness < .2f && s.ThawBurden < .05f) damage -= dt / 3600;
            injury = Math.Clamp(injury + damage, 0, 1);
            s.Sensitivity = Math.Clamp(MathF.Max(s.Sensitivity - dt / ScarRecoverySeconds, injury * MathF.Max(s.ThawedDose, .25f)), 0, 1);
            float pain = Math.Clamp(s.ThawBurden * 2 + (temperature > 10 ? injury * .4f : 0), 0, 1);
            s.Pain += (pain - s.Pain) * (1 - MathF.Exp(-dt / 15));
            float target = Math.Clamp((temperature - 3) / 17, .15f, 1) * (1 - .5f * injury) * (1 - .2f * s.Pain);
            float recovery = target < s.NerveFunction ? 20 : 40;
            s.NerveFunction = Math.Clamp(s.NerveFunction + (target - s.NerveFunction) * (1 - MathF.Exp(-dt / recovery)), .05f, 1);
            s.PreviousFrozen = frozen;
        }
    }
}
