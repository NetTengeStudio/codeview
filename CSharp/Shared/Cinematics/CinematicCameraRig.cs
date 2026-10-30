using System;
using ScpItdMod.Core;

namespace ScpItdMod.Cinematics {
    public struct CinematicCameraSubject {
        public Vec2 Position, Velocity;
        public bool Rotorcraft;
        public float RotorRadius, InducedVelocity, UnitsPerMetre;
        public static CinematicCameraSubject FromFlight(in CinematicFlightObservation observation) {
            float units = observation.Scale * 100f;
            return new CinematicCameraSubject { Position = observation.Position * units, Velocity = observation.Velocity * units, Rotorcraft = true,
                RotorRadius = observation.RotorRadius, InducedVelocity = observation.InducedVelocity, UnitsPerMetre = units };
        }
    }

    public static class CinematicCameraProfile {
        public const float BreathFraction = .0008f, HandFraction = .0025f, RotorFraction = .012f, ImpulseFraction = .008f;
        public const float BreathHertz = .23f, ImpulseHertz = 2.3f, ImpulseDecaySeconds = .65f;
        public const float MaximumRoll = .01f, NoiseBaseHertz = .2f, OperatorCutoffHertz = 8f, MaximumPredictionFraction = .18f;
        public const int NoiseOctaves = 8;
    }

    public sealed class CinematicCameraRig {
        private Vec2 position, velocity;
        private float zoom, zoomVelocity;
        private CinematicCue shot;
        public Vec2 Velocity => velocity;
        public Vec2 Listener { get; private set; }
        public CinematicCameraRig(Vec2 start, float startZoom) { position = start; zoom = startZoom; }
        public void Evaluate(CinematicCue current, double shotAge, in CinematicCameraSubject subject, Vec2 anchor, Vec2 resolution, bool snap, bool reducedMotion,
            double clock, uint seed, float frameSeconds, out Vec2 camera, out float cameraZoom, out float rotation) {
            float age = (float)Math.Max(0, shotAge), duration = (float)current.Duration;
            float u = duration > 0 ? Math.Clamp(age / duration, 0, 1) : 1;
            float ease = u * u * u * (u * (u * 6 - 15) + 10);
            Vec2 offset = Vec2.Lerp(current.Position, current.End, ease);
            Vec2 desiredVelocity = duration > 0 ? (current.End - current.Position) * (30 * u * u * (u - 1) * (u - 1) / duration) : Vec2.Zero;
            Vec2 desired;
            if (current.CameraMode == CinematicCameraMode.Follow) {
                Vec2 prediction = subject.Velocity * current.LookAhead;
                float limit = resolution.Y / current.Zoom * CinematicCameraProfile.MaximumPredictionFraction;
                float length = prediction.Length();
                if (length > limit) prediction *= limit / length;
                desired = CinematicCameraMath.Compose(subject.Position + prediction, Vec2.Zero, offset, 0, current.FrameX, current.FrameY, current.Zoom, resolution);
                desiredVelocity += subject.Velocity;
            } else desired = anchor + offset;
            bool first = shot == null, changed = !ReferenceEquals(shot, current);
            shot = current;
            if (snap || changed && current.Cut) {
                position = desired;
                velocity = !current.MatchVelocity ? Vec2.Zero : snap || first ? desiredVelocity : subject.Velocity - (subject.Velocity - velocity) * (zoom / current.Zoom);
                zoom = current.Zoom; zoomVelocity = 0;
            } else {
                float dt = Math.Clamp(frameSeconds, 0, .1f), omega = 6f / current.Blend, decay = MathF.Exp(-omega * dt);
                Vec2 error = position - desired, relativeVelocity = velocity - desiredVelocity;
                Vec2 j = (relativeVelocity + error * omega) * dt;
                position = desired + (error + j) * decay;
                velocity = desiredVelocity + (relativeVelocity - j * omega) * decay;
                float z = zoom - current.Zoom, jz = (zoomVelocity + omega * z) * dt;
                zoom = current.Zoom + (z + jz) * decay; zoomVelocity = (zoomVelocity - omega * jz) * decay;
            }
            Listener = position;
            Vec2 fraction = default;
            if (!reducedMotion) {
                float intensity = Math.Clamp(current.Intensity, 0, 10);
                fraction = new Vec2((float)Math.Sin(clock * Math.Tau * CinematicCameraProfile.BreathHertz), (float)Math.Cos(clock * Math.Tau * CinematicCameraProfile.BreathHertz)) * CinematicCameraProfile.BreathFraction;
                fraction += new Vec2(CinematicCameraMath.OperatorNoise(clock, seed, frameSeconds), CinematicCameraMath.OperatorNoise(clock, seed ^ 0x9e3779b9u, frameSeconds)) * (CinematicCameraProfile.HandFraction * intensity);
                float trauma = current.Impulse * MathF.Exp(-age / CinematicCameraProfile.ImpulseDecaySeconds);
                fraction += new Vec2(.4f, 1) * (CinematicCameraProfile.ImpulseFraction * trauma * trauma * MathF.Sin(age * MathF.Tau * CinematicCameraProfile.ImpulseHertz));
                if (subject.Rotorcraft && subject.UnitsPerMetre > 0)
                    fraction += CinematicCameraMath.Flyby(position / subject.UnitsPerMetre, subject.Position / subject.UnitsPerMetre, (subject.Velocity - velocity) / subject.UnitsPerMetre,
                        subject.RotorRadius, subject.InducedVelocity, clock, seed, intensity * CinematicCameraProfile.RotorFraction, 1, frameSeconds);
                float magnitude = fraction.Length();
                if (magnitude > CinematicCameraMath.MaximumShakeFraction) fraction *= CinematicCameraMath.MaximumShakeFraction / magnitude;
            }
            Vec2 shake = fraction * (resolution.Y / zoom);
            camera = position + shake; cameraZoom = zoom;
            rotation = Math.Clamp(CinematicCameraMath.Roll(shake, resolution.X / zoom), -CinematicCameraProfile.MaximumRoll, CinematicCameraProfile.MaximumRoll);
        }
    }
}
