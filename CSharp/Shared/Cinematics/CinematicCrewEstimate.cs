using System;
using ScpItdMod.Core;

namespace ScpItdMod.Cinematics {
    public enum CinematicCrewPhase : byte { Seated, Climbing, Standing, Walking }

    public struct CinematicCrewMember {
        public CinematicCrewPhase Phase;
        public Vec2 Position, Target;
        public float Remaining, Speed, Radius;
        // A walk cued while the person is still climbing out; it starts when the climb ends.
        public bool Queued; public Vec2 QueuedTarget; public float QueuedSpeed, QueuedRadius, QueuedSeconds;
    }

    public sealed class CinematicCrewEstimate {
        public const float ClimbSeconds = 3f;
        // Door exit of the Mi-8 body animation (BOVehicles ExitDuration, measured in MotionLab).
        public const float ExitSeconds = 15.5f;
        // One person on the rail at a time, from the first grip to the feet on the ground (MotionLab asserts both constants against the baked exit).
        public const float RailSeconds = 3.5f;
        public readonly CinematicCrewMember[] Members;
        public CinematicCrewEstimate(int bindings) => Members = new CinematicCrewMember[bindings];
        public void Seat(int binding) => Members[binding] = new CinematicCrewMember { Phase = CinematicCrewPhase.Seated };
        public void Disembark(int binding, Vec2 ground) => Members[binding] = new CinematicCrewMember { Phase = CinematicCrewPhase.Climbing, Position = ground, Remaining = ExitSeconds };
        public void Board(int binding) => Members[binding] = new CinematicCrewMember { Phase = CinematicCrewPhase.Climbing, Position = Members[binding].Position, Remaining = ClimbSeconds, Target = new Vec2(float.NaN, float.NaN) };
        public bool Move(int binding, Vec2 target, float speedUnits, float radiusUnits, float seconds) {
            ref CinematicCrewMember member = ref Members[binding];
            if (member.Phase == CinematicCrewPhase.Climbing && !float.IsNaN(member.Target.X)) {
                member.Queued = true; member.QueuedTarget = target; member.QueuedSpeed = speedUnits; member.QueuedRadius = radiusUnits; member.QueuedSeconds = seconds;
                return true;
            }
            if (member.Phase is CinematicCrewPhase.Climbing or CinematicCrewPhase.Seated) return false;
            member.Phase = CinematicCrewPhase.Walking; member.Target = new Vec2(target.X, member.Position.Y); member.Speed = speedUnits; member.Radius = radiusUnits; member.Remaining = seconds;
            return true;
        }
        public string Step(float dt) {
            for (int i = 0; i < Members.Length; i++) {
                ref CinematicCrewMember member = ref Members[i];
                switch (member.Phase) {
                    case CinematicCrewPhase.Climbing:
                        if ((member.Remaining -= dt) <= 0) {
                            member.Phase = float.IsNaN(member.Target.X) ? CinematicCrewPhase.Seated : CinematicCrewPhase.Standing;
                            if (member.Queued) {
                                member.Queued = false; member.Phase = CinematicCrewPhase.Walking; member.Target = new Vec2(member.QueuedTarget.X, member.Position.Y);
                                member.Speed = member.QueuedSpeed; member.Radius = member.QueuedRadius; member.Remaining = member.QueuedSeconds;
                            }
                        }
                        break;
                    case CinematicCrewPhase.Walking:
                        float delta = member.Target.X - member.Position.X;
                        if (MathF.Abs(delta) <= member.Radius) { member.Phase = CinematicCrewPhase.Standing; break; }
                        if ((member.Remaining -= dt) <= 0) return "Actor movement timed out";
                        member.Position = new Vec2(member.Position.X + MathF.Sign(delta) * MathF.Min(MathF.Abs(delta), member.Speed * dt), member.Position.Y);
                        break;
                }
            }
            return null;
        }
        public bool Done {
            get {
                foreach (CinematicCrewMember member in Members) if (member.Phase is CinematicCrewPhase.Climbing or CinematicCrewPhase.Walking) return false;
                return true;
            }
        }
    }
}
