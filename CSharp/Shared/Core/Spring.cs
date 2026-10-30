using System;
using System.Runtime.CompilerServices;

namespace ScpItdMod.Core {
    public struct Spring {
        public float Value;
        public float Velocity;

        public Spring(float value) {
            Value = value;
            Velocity = 0.0f;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Snap(float value) {
            Value = value;
            Velocity = 0.0f;
        }

        public void Step(float target, float omega, float zeta, float dt) {
            if (dt <= 0.0f) { return; }
            var matrix = new Motion.SpringMatrix(omega, zeta, dt);
            float displacement = Value - target, velocity = Velocity;
            Value = target + matrix.XX * displacement + matrix.XV * velocity;
            Velocity = matrix.VX * displacement + matrix.VV * velocity;
        }

        public void StepAngle(float target, float omega, float zeta, float dt) {
            Step(Value + WrapPi(target - Value), omega, zeta, dt);
            Value = WrapPi(Value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Resting(float target, float epsilon) =>
            MathF.Abs(Value - target) < epsilon && MathF.Abs(Velocity) < epsilon;

        public static float WrapPi(float angle) {
            const float Tau = MathF.PI * 2.0f;
            angle -= Tau * MathF.Floor((angle + MathF.PI) / Tau);
            return angle;
        }
    }
}
