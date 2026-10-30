using Microsoft.Xna.Framework;
using ScpItdMod.Core;

namespace ScpItdMod.Cinematics {
    public static class CinematicBovFormat {
        public static bool Convert(Vector4 motion, Vector4 attitude, Vector4 limits, Vector4 geometry, uint flags, out CinematicFlightObservation observation) {
            observation = new CinematicFlightObservation {
                Position = new Vec2(motion.X, motion.Y), Velocity = new Vec2(motion.Z, motion.W), Tilt = attitude.X, Heading = attitude.Y,
                AngularRate = attitude.Z, RotorShare = attitude.W, MaximumTilt = limits.X, MaximumSpeed = limits.Y, ClimbRate = limits.Z,
                ResponseSeconds = limits.W, RotorRadius = geometry.X, InducedVelocity = geometry.Z, Scale = geometry.W,
                Grounded = (flags & 1) != 0, Ready = (flags & 2) != 0, Critical = (flags & 4) != 0, Master = (flags & 8) != 0, GearReady = (flags & 64) != 0
            };
            return observation.Finite;
        }
        public static CinematicFlightEnvelope Envelope(Vector4 envelope, Vector4 regulation) => new CinematicFlightEnvelope {
            HalfLength = envelope.X, HubHeight = envelope.Y, SuspensionTravel = envelope.Z, TouchdownRate = envelope.W, PositionGain = regulation.X, MinimumRotorShare = regulation.Z
        };
        public static CinematicOutwash Flow(Vector4 flow) => new CinematicOutwash { Speed = flow.X, Ground = flow.Y, Wake = flow.Z, Ratio = flow.W };
    }
}
