using System;
using System.Runtime.CompilerServices;
using Barotrauma;
using ScpItdMod.Core;
using Microsoft.Xna.Framework;

namespace ScpItdMod.Stamina {
    [Flags]
    public enum StaminaFlags : byte {
        None = 0,
        Locked = 1,
        Exerting = 2
    }

    public enum StaminaActivity : byte {
        Ground = 0,
        Water = 1,
        Ladder = 2
    }

    public struct StaminaState {
        public float Value;
        public float Delay;
        public StaminaFlags Flags;

        public bool Locked => (Flags & StaminaFlags.Locked) != 0;

        public bool Exerting => (Flags & StaminaFlags.Exerting) != 0;

        public static StaminaState Full => new StaminaState { Value = 1.0f, Delay = 0.0f, Flags = StaminaFlags.None };
    }

    public static class StaminaSim {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Step(ref StaminaState state, StaminaActivity activity, bool exerting, float dt) =>
            Step(ref state.Value, ref state.Delay, ref state.Flags, activity, exerting, dt);

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Step(ref float value, ref float delay, ref StaminaFlags flags,
            StaminaActivity activity, bool exerting, float dt) {
            if (dt <= 0.0f) { return; }
            if (exerting && (flags & StaminaFlags.Locked) == 0) {
                value -= StaminaConfig.Drain * DrainScale(activity) * dt;
                delay = StaminaConfig.RegenDelay;
                flags |= StaminaFlags.Exerting;
            } else {
                flags &= ~StaminaFlags.Exerting;
                if (delay > 0.0f) { delay -= dt; }
                else { value += StaminaConfig.Regen * RegenScale(activity) * dt; }
            }
            if (value < 0.0f) { value = 0.0f; }
            else if (value > 1.0f) { value = 1.0f; }
            if ((flags & StaminaFlags.Locked) != 0) {
                if (value >= StaminaConfig.UnlockAt) { flags &= ~StaminaFlags.Locked; }
            } else if (value <= StaminaConfig.LockAt) {
                flags |= StaminaFlags.Locked;
                flags &= ~StaminaFlags.Exerting;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float DrainScale(StaminaActivity activity) => activity switch {
            StaminaActivity.Water => StaminaConfig.DrainSwim,
            StaminaActivity.Ladder => StaminaConfig.DrainClimb,
            _ => StaminaConfig.DrainRun
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float RegenScale(StaminaActivity activity) => activity switch {
            StaminaActivity.Water => StaminaConfig.RegenWater,
            StaminaActivity.Ladder => StaminaConfig.RegenClimb,
            _ => StaminaConfig.RegenLand
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Winded(float value) => value < StaminaConfig.FalloffStart;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Exhaustion(float value) =>
            Winded(value) ? 1.0f - Scalar.Smooth(value * (1.0f / StaminaConfig.FalloffStart)) : 0.0f;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Speed(StaminaState state) => Speed(state.Value, state.Flags);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Speed(float value, StaminaFlags flags) {
            float exhaustion = Exhaustion(value);
            if (exhaustion <= 0.0f) { return 1.0f; }
            float floor = (flags & StaminaFlags.Locked) != 0 ? StaminaConfig.ExhaustedWalk : StaminaConfig.SlowFloor;
            return 1.0f - (1.0f - floor) * exhaustion;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Eligible(Character character) =>
            character != null && !character.Removed && !character.IsDead && character.IsPlayer &&
            character.AllowInput && character.AnimController is HumanoidAnimController;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static StaminaActivity Classify(Character character) {
            AnimController anim = character.AnimController;
            if (anim == null) { return StaminaActivity.Ground; }
            if (anim.InWater || !anim.CanWalk) { return StaminaActivity.Water; }
            return anim.IsClimbing ? StaminaActivity.Ladder : StaminaActivity.Ground;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Crouching(Character character) =>
            character.AnimController is HumanoidAnimController human && human.Crouching;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static bool Exerting(Character character, StaminaActivity activity) {
            if (!character.ForceRun && !character.IsKeyDown(InputType.Run)) { return false; }
            if (!character.CanRun) { return false; }
            AnimController anim = character.AnimController;
            if (anim == null) { return false; }
            Vector2 movement = anim.TargetMovement;
            float epsilon = StaminaConfig.MovingEpsilon;
            return activity switch {
                StaminaActivity.Water => movement.LengthSquared() > epsilon * epsilon,
                StaminaActivity.Ladder => movement.Y > epsilon || movement.Y < -epsilon,
                _ => movement.X > epsilon || movement.X < -epsilon
            };
        }
    }
}
