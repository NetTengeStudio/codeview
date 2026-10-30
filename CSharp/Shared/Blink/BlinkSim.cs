using System;
using System.Runtime.CompilerServices;
using Barotrauma;
using ScpItdMod.Core;

namespace ScpItdMod.Blink {
    public enum BlinkDifficulty : byte {
        Easy = 0,
        Normal = 1,
        Hard = 2
    }

    public enum BlinkDrops : byte {
        Plain = 0,
        Fine = 1,
        Super = 2
    }

    public struct BlinkState {
        public float Timer;
        public float Frequency;
        public float Irritation;
        public float Stuck;
        public float Effect;
        public float EffectTimer;

        public bool EyesOpen => BlinkSim.EyesOpen(Timer);

        public bool Irritated => Irritation > 0.0f;

        public float Fill => Frequency > 0.0f ? Scalar.Clamp01(Timer / Frequency) : 0.0f;
    }

    public static class BlinkSim {
        private const float DegreesToRadians = (float)(Math.PI / 180.0);

        public static BlinkState Rested(BlinkDifficulty difficulty) {
            BlinkState state = default;
            Rest(ref state.Timer, ref state.Frequency, ref state.Irritation, ref state.Stuck,
                ref state.Effect, ref state.EffectTimer, difficulty);
            return state;
        }

        public static void Rest(ref float timer, ref float frequency, ref float irritation, ref float stuck,
            ref float effect, ref float effectTimer, BlinkDifficulty difficulty) {
            frequency = Frequency(difficulty);
            timer = frequency;
            irritation = 0.0f;
            stuck = 0.0f;
            effect = 1.0f;
            effectTimer = 0.0f;
        }

        public static float Frequency(BlinkDifficulty difficulty) => difficulty switch {
            BlinkDifficulty.Easy => Rand.Range(BlinkConfig.EasyMin, BlinkConfig.EasyMax, Rand.RandSync.Unsynced),
            BlinkDifficulty.Hard => Rand.Range(BlinkConfig.HardMin, BlinkConfig.HardMax, Rand.RandSync.Unsynced),
            _ => Rand.Range(BlinkConfig.NormalMin, BlinkConfig.NormalMax, Rand.RandSync.Unsynced)
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Eligible(Character character) =>
            character != null && !character.Removed && !character.IsDead && character.IsPlayer;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static BlinkDifficulty Grade(float levelDifficulty) =>
            levelDifficulty < BlinkConfig.EasyBelow ? BlinkDifficulty.Easy :
            levelDifficulty < BlinkConfig.NormalBelow ? BlinkDifficulty.Normal : BlinkDifficulty.Hard;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool EyesOpen(float timer) =>
            timer < BlinkConfig.EyesOpenBelow || timer > BlinkConfig.EyesOpenAbove;

        public static float Advance(ref BlinkState state, BlinkDifficulty difficulty, float dt) =>
            Advance(ref state.Timer, ref state.Frequency, ref state.Irritation, ref state.Stuck,
                ref state.Effect, ref state.EffectTimer, difficulty, dt);

        public static float Advance(ref float timer, ref float frequency, ref float irritation, ref float stuck,
            ref float effect, ref float effectTimer, BlinkDifficulty difficulty, float dt) {
            float dark = Step(ref timer, ref frequency, ref irritation, ref stuck, ref effect, ref effectTimer,
                dt, out bool cycleEnded);
            if (!cycleEnded) { return dark; }
            frequency = Frequency(difficulty);
            timer = frequency - Math.Min(dt, BlinkConfig.StepCeiling);
            return dark;
        }

        public static float Follow(ref BlinkState state, float dt) =>
            Step(ref state.Timer, ref state.Frequency, ref state.Irritation, ref state.Stuck,
                ref state.Effect, ref state.EffectTimer, dt, out _);

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static float Step(ref float timer, ref float frequency, ref float irritation, ref float stuck,
            ref float effect, ref float effectTimer, float dt, out bool cycleEnded) {
            cycleEnded = false;
            if (dt > BlinkConfig.StepCeiling) { dt = BlinkConfig.StepCeiling; }
            float dark = 0.0f;

            if (stuck > 0.0f) {
                timer = frequency;
                stuck = Math.Max(stuck - dt, 0.0f);
                if (stuck < BlinkConfig.StuckTearAt) {
                    dark = Math.Min((BlinkConfig.StuckTearAt - stuck) / BlinkConfig.StuckTearSpan, 1.0f);
                }
            }

            if (timer < 0.0f) {
                dark = Math.Max(dark, Darkness(timer));
                cycleEnded = timer <= BlinkConfig.CycleEnd;
                if (!cycleEnded) { timer -= dt; }
            } else {
                timer -= dt * BlinkConfig.OpenRate * effect;
                if (irritation > 0.0f) {
                    timer -= Math.Min(
                        irritation * BlinkConfig.IrritationRateScale + BlinkConfig.IrritationRateBase,
                        BlinkConfig.IrritationRateCeiling) * dt;
                }
            }

            irritation = Math.Max(0.0f, irritation - dt);
            if (effectTimer > 0.0f) { effectTimer -= dt; }
            else { effect = 1.0f; }
            return dark;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Force(ref BlinkState state, bool hit, bool held) => Force(ref state.Timer, hit, held);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Force(ref float timer, bool hit, bool held) {
            if (hit) { timer = 0.0f; }
            if (held && timer < BlinkConfig.HoldFloor) { timer = BlinkConfig.HoldFloor; }
        }

        public static void Soothe(ref BlinkState state, BlinkDrops drops) {
            switch (drops) {
                case BlinkDrops.Fine:
                    state.Effect = BlinkConfig.FineDropsEffect;
                    state.EffectTimer = Rand.Range(BlinkConfig.FineDropsSecondsMin, BlinkConfig.FineDropsSecondsMax,
                        Rand.RandSync.Unsynced);
                    break;
                case BlinkDrops.Super:
                    state.Effect = BlinkConfig.SuperDropsEffect;
                    state.EffectTimer = BlinkConfig.SuperDropsSeconds;
                    state.Stuck = BlinkConfig.StuckSeconds;
                    break;
                default:
                    state.Effect = BlinkConfig.DropsEffect;
                    state.EffectTimer = Rand.Range(BlinkConfig.DropsSecondsMin, BlinkConfig.DropsSecondsMax,
                        Rand.RandSync.Unsynced);
                    break;
            }
        }

        public static void Irritate(ref BlinkState state, float seconds) {
            if (seconds <= 0.0f) { return; }
            state.Irritation += seconds * BlinkConfig.IrritationPerSecond;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Darkness(float timer) {
            if (timer > BlinkConfig.FullDarkEnter) {
                return Sine(Math.Abs(timer * BlinkConfig.ClosePhaseDegrees));
            }
            if (timer > BlinkConfig.FullDarkLeave) { return 1.0f; }
            return Math.Abs(Sine(timer * BlinkConfig.ClosePhaseDegrees));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Sine(float degrees) => (float)Math.Sin(degrees * DegreesToRadians);
    }
}
