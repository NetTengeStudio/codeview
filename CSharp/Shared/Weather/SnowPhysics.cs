using System;
using ScpItdMod.Core;

namespace ScpItdMod.Weather {
    public static class SnowPhysics {
        public const float Gravity = 9.81f, FreshDensity = 100, PackedDensity = 350, ActiveDepth = .03f;
        public const float MaximumHopHeight = 1.5f;
        public const float BootDensity = PackedDensity;
        public static float LandingSpeed(float speed, float depth, float compaction, float fresh) {
            if (!float.IsFinite(speed) || !float.IsFinite(depth) || !float.IsFinite(compaction) || !float.IsFinite(fresh))
                throw new ArgumentOutOfRangeException(nameof(speed));
            if (speed <= 0 || depth <= 0) return speed;
            float stroke = BootSinkage(depth, depth * Density(compaction), fresh);
            return MathF.Sqrt(MathF.Max(0, speed * speed - 2 * Gravity * stroke));
        }
        private const float VonKarman = .4f, ReferenceWindHeight = 10, RoughnessLength = .001f, SaltationCoefficient = 2.8f;
        private static readonly float ShearRatio = VonKarman / MathF.Log(ReferenceWindHeight / RoughnessLength);
        public static double ExcavationWorkDensity(float density, float cutDepth, float lift, float cohesion, float tanFriction) {
            if (!float.IsFinite(density) || !(density > 0) || !float.IsFinite(cutDepth) || cutDepth < 0 ||
                !float.IsFinite(lift) || lift < 0 || !float.IsFinite(cohesion) || cohesion < 0 || !float.IsFinite(tanFriction) || tanFriction < 0)
                throw new ArgumentOutOfRangeException(nameof(density));
            double friction = tanFriction, passiveRoot = Math.Sqrt(1 + friction * friction) + friction;
            return density * (double)Gravity * (lift + .5 * cutDepth * passiveRoot * passiveRoot) + 2 * cohesion * passiveRoot;
        }
        public static float HopHeight(float wind) {
            float shear = wind * ShearRatio;
            return Math.Clamp(shear * shear / (2 * Gravity), .0004f, MaximumHopHeight);
        }
        public static float CarryingMass(float wind, float threshold, float airDensity) {
            if (wind <= threshold) return 0;
            float shear = wind * ShearRatio, critical = threshold * ShearRatio;
            return SaltationCoefficient * airDensity * shear * (shear * shear - critical * critical) / (Gravity * wind);
        }
        public static float DriftCompaction(float wind) {
            float shear = wind * ShearRatio, energy = shear * shear;
            return .5f * energy / (energy + Gravity * ActiveDepth);
        }
        public static float Density(float compaction) => FreshDensity + (PackedDensity - FreshDensity) * compaction;
        public static float Compaction(float density) => Math.Clamp((density - FreshDensity) / (PackedDensity - FreshDensity), 0, 1);
        public static float PackingRate(float wind, float threshold, float depth, float baseRate) {
            float ratio = wind / threshold;
            return baseRate * MathF.Max(0, ratio * ratio - 1) * MathF.Min(1, ActiveDepth / MathF.Max(ActiveDepth, depth));
        }
        public static float SinteringRate(float baseRate, float temperature, float depth, float compaction) {
            float loose = 1 - compaction;
            return baseRate * MathF.Exp(.04f * (MathF.Min(0, temperature) + 15)) * loose * loose * (1 + depth);
        }
        public static float AvalancheMass(float excess, float fromDensity, float toDensity, float distance, float dt) {
            float speed = MathF.Sqrt(2 * Gravity * excess), travel = speed * dt / distance;
            return excess / (1 / fromDensity + 1 / toDensity) * (travel / (1 + travel));
        }
        public static float BaseDensity(float depth, float mass, float fresh) {
            float baseDepth = depth - fresh;
            return baseDepth <= 1e-4f ? PackedDensity : Math.Clamp((mass - fresh * FreshDensity) / baseDepth, FreshDensity, PackedDensity);
        }
        public static float BootSinkage(float depth, float mass, float fresh) {
            if (!(depth > 0) || !(mass > 0)) return 0;
            fresh = Math.Clamp(fresh, 0, depth);
            return fresh * (1 - FreshDensity / BootDensity) + (depth - fresh) * MathF.Max(0, 1 - BaseDensity(depth, mass, fresh) / BootDensity);
        }
        public static float Erodible(float depth, float density, float fresh, float compaction) {
            float soft = MathF.Min(MathF.Min(fresh, depth), ActiveDepth), bonded = 1 - compaction;
            return soft * FreshDensity + MathF.Max(0, MathF.Min(depth, ActiveDepth) - soft) * density * bonded * bonded;
        }
        public const float CrestScale = .5f, CrestMinimum = .5f, CrestMaximum = 1.6f;
        public static float CrestSpeedUp(float curvature) => Math.Clamp(1 - curvature * CrestScale, CrestMinimum, CrestMaximum);
        public const float GrainSettling = .3f, SuspensionCeiling = 3f;
        public static float SaltationTop(float wind) => MathF.Max(.05f, 2 * HopHeight(wind));
        public static float RouseNumber(float wind) => GrainSettling / MathF.Max(.05f, VonKarman * MathF.Abs(wind) * ShearRatio);
        public static float Suspension(float height, float wind) => height <= 0 ? 1 : MathF.Pow(1 + height / SaltationTop(wind), -RouseNumber(wind));
        public static float SuspendedHeight(float wind, float unit) {
            float rouse = RouseNumber(wind), top = SaltationTop(wind);
            if (rouse <= 1.05f) return unit * SuspensionCeiling;
            return MathF.Min(SuspensionCeiling, top * (MathF.Pow(1 - Math.Clamp(unit, 0, .999f), -1 / (rouse - 1)) - 1));
        }
        public static float FlakeFallSpeed(float diameterMillimetres) => .8f * MathF.Pow(MathF.Max(.05f, diameterMillimetres), .16f);

        public static void Advect(ref Vec2 position, ref Vec2 velocity, Vec2 air, float settling, float dt) {
            if (!(dt > 0)) return;
            float response = MathF.Max(.01f, settling / Gravity);
            float decay = MathF.Exp(-dt / response);
            Vec2 target = air - new Vec2(0, settling), difference = velocity - target;
            position += target * dt + difference * (response * (1 - decay));
            velocity = target + difference * decay;
        }

    }
}
