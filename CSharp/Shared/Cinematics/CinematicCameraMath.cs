using System;
using ScpItdMod.Core;

namespace ScpItdMod.Cinematics {
    public static class CinematicCameraMath {
        public static Vec2 Compose(Vec2 subject, Vec2 velocity, Vec2 offset, float prediction, float frameX, float frameY, float zoom, Vec2 resolution) {
            if (!(zoom > 0) || !float.IsFinite(zoom)) throw new ArgumentOutOfRangeException(nameof(zoom));
            return subject + velocity * prediction + offset - new Vec2((frameX - .5f) * resolution.X / zoom, (.5f - frameY) * resolution.Y / zoom);
        }
        public const float MaximumShakeFraction = .03f, RollPerFrameWidth = .25f, RotorTremorHertz = 13f, RotorTremorShare = .3f;
        public static Vec2 Flyby(Vec2 listener, Vec2 source, Vec2 relativeVelocity, float radius, float induced, double seconds, uint seed, float fraction, float frameHeight, float sampleSeconds = 1f / 240f) {
            float r = MathF.Max(1f, radius), distance2 = (source - listener).LengthSquared();
            float speed2 = relativeVelocity.LengthSquared();
            float proximity = r * r / (r * r + distance2);
            float approach = MathF.Abs(Vec2.Dot(Vec2.Normalize(source - listener), Vec2.Normalize(relativeVelocity)));
            float energy = Math.Clamp((speed2 + induced * induced) / (9.81f * r), 0f, 1f);
            float amplitude = Math.Clamp(fraction, 0f, MaximumShakeFraction) * MathF.Max(0f, frameHeight);
            float envelope = amplitude * proximity * energy * (.5f + .5f * approach);
            float x = OperatorNoise(seconds, seed, sampleSeconds), y = OperatorNoise(seconds, seed ^ 0x9e3779b9u, sampleSeconds);
            double phase = seconds * Math.Tau * RotorTremorHertz;
            x = x * (1f - RotorTremorShare) + (float)Math.Sin(phase) * RotorTremorShare; y = y * (1f - RotorTremorShare) + (float)Math.Sin(phase * 1.31 + 1.7) * RotorTremorShare;
            return new Vec2(x, y) * envelope;
        }
        public static float Roll(Vec2 shake, float frameWidth) => frameWidth > 0 ? Math.Clamp(shake.X / frameWidth * RollPerFrameWidth, -CinematicCameraProfile.MaximumRoll, CinematicCameraProfile.MaximumRoll) : 0f;
        public static float Noise(double seconds, uint seed, float sampleSeconds) => Synthesize(seconds, seed, sampleSeconds, false);
        public static float OperatorNoise(double seconds, uint seed, float sampleSeconds) => Synthesize(seconds, seed, sampleSeconds, true);
        private static float Synthesize(double seconds, uint seed, float sampleSeconds, bool damped) {
            float result = 0, frequency = CinematicCameraProfile.NoiseBaseHertz;
            double nyquist = .5 / Math.Max(1e-5, sampleSeconds);
            for (int i = 0; i < CinematicCameraProfile.NoiseOctaves; i++, frequency *= 2) {
                double phase = seconds * frequency;
                int cell = (int)Math.Floor(phase);
                float u = (float)(phase - cell), smooth = u * u * u * (u * (u * 6 - 15) + 10);
                float a = Hash(unchecked((uint)cell) ^ seed), b = Hash(unchecked((uint)(cell + 1)) ^ seed);
                float normalized = damped ? frequency / CinematicCameraProfile.OperatorCutoffHertz : 0;
                result += (a + (b - a) * smooth) * (float)Band(frequency, nyquist) / (1f + normalized * normalized);
                seed = unchecked(seed * 1664525u + 1013904223u);
            }
            return result / CinematicCameraProfile.NoiseOctaves;
        }
        private static float Hash(uint value) {
            value ^= value >> 16; value *= 0x7feb352du; value ^= value >> 15; value *= 0x846ca68bu; value ^= value >> 16;
            return (value >> 8) * (2f / 16777215f) - 1f;
        }
        private static double Band(double hz, double nyquist) => Math.Clamp((nyquist - hz) / (nyquist * .2), 0, 1);
    }
}
