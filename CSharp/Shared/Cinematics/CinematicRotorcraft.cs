using System;
using System.Collections.Generic;
using ScpItdMod.Core;

namespace ScpItdMod.Cinematics {
    public struct CinematicFlightEnvelope {
        public float HalfLength, HubHeight, SuspensionTravel, TouchdownRate, PositionGain, MinimumRotorShare;
    }

    public interface ICinematicRotorcraft {
        bool Observe(out CinematicFlightObservation observation);
        bool Envelope(out CinematicFlightEnvelope envelope);
        bool Acquire(ulong owner);
        bool WarmStart(ulong owner, Vec2 velocity);
        bool Command(ulong owner, Vec2 velocity, Vec2 acceleration, float heading, uint switches);
        void Release(ulong owner, bool completed);
        bool Surface(Vec2 site, out float height, out float normalY, out float mismatch);
        bool Trace(Vec2 from, Vec2 to);
        bool Outwash(Vec2 point, out CinematicOutwash flow);
    }

    public struct CinematicOutwash {
        public float Speed, Ground, Wake, Ratio;
    }

    public sealed class CinematicVehicleDirector : IDisposable {
        public readonly ICinematicRotorcraft Airframe;
        public readonly CinematicFlightController Controller = new CinematicFlightController();
        public CinematicFlightObservation Observation;
        public bool Master;
        private readonly ulong owner;
        private readonly CinematicFlightEnvelope envelope;
        private Vec2 holdingPosition;
        private float holdingHeading;
        private bool holdingSet;
        private readonly HashSet<string> passed = new HashSet<string>(StringComparer.Ordinal);
        private CinematicCue route;
        private uint reported;
        private bool acquired, completed;
        public CinematicCue Route => route;
        public CinematicVehicleDirector(ICinematicRotorcraft airframe, ulong owner) {
            Airframe = airframe ?? throw new ArgumentNullException(nameof(airframe)); this.owner = owner;
            if (!airframe.Observe(out Observation) || !airframe.Envelope(out envelope)) throw new InvalidOperationException("Rotorcraft telemetry unavailable");
            if (!airframe.Acquire(owner)) throw new InvalidOperationException("Rotorcraft control is already owned or this peer is not authoritative");
            acquired = true;
            Master = Observation.Master;
            holdingPosition = Observation.Position; holdingHeading = Observation.Heading;
        }
        public float UnitsPerMetre => Observation.Scale * 100f;
        public void Fly(CinematicCue cue, Vec2 anchor) { route = cue; reported = 0; Controller.SetRoute(cue, anchor, UnitsPerMetre); }
        public bool Passed(string waypoint) => passed.Contains(waypoint);
        public bool Passed(ReadOnlySpan<char> waypoint) { foreach (string point in passed) if (waypoint.SequenceEqual(point.AsSpan())) return true; return false; }
        public float VisibilityRadius => MathF.Max(envelope.HalfLength, envelope.HubHeight + Observation.RotorRadius);
        public bool Step(float dt, Vec2 anchor) {
            if (!Airframe.Observe(out Observation)) return false;
            Observation.SuspensionTravel = envelope.SuspensionTravel; Observation.TouchdownRate = envelope.TouchdownRate;
            Observation.PositionGain = envelope.PositionGain; Observation.MinimumRotorShare = envelope.MinimumRotorShare;
            if (route != null) {
                float units = UnitsPerMetre;
                Controller.SetOrigin(anchor, units);
                CinematicWaypoint goal = Controller.Goal;
                if (goal.Mode == CinematicFlightMode.Land) {
                    Vec2 site = (anchor + goal.Position) / units;
                    Observation.SupportAvailable = Airframe.Surface(site, out Observation.SurfaceHeight, out Observation.SurfaceNormalY, out Observation.SurfaceMismatch);
                }
                Observation.CorridorBlocked = Blocked(goal);
            }
            CinematicFlightCommand command = Controller.Step(Observation, dt);
            if (route != null && Controller.Passed != reported) {
                uint fresh = Controller.Passed & ~reported; reported = Controller.Passed;
                for (int i = 0; i < route.Route.Length; i++) if ((fresh >> i & 1) != 0 && route.Route[i].Id.Length > 0) passed.Add(route.Route[i].Id);
            }
            uint switches = 8u | (Master ? 2u : 0u) | (command.Landing ? 1u : 0u);
            return Airframe.Command(owner, command.Velocity, command.Acceleration, command.Heading, switches);
        }
        private bool Blocked(CinematicWaypoint goal) {
            Vec2 position = Observation.Position;
            float speed = Observation.Velocity.X, direction = MathF.Sign(speed);
            float stopping = speed * speed / (2 * MathF.Min(goal.Acceleration, 9.81f * MathF.Tan(Observation.MaximumTilt)));
            float look = MathF.Abs(speed) * Observation.ResponseSeconds + stopping;
            if (look > Observation.RotorRadius && !Observation.Grounded) {
                Vec2 forward = new Vec2(direction * (envelope.HalfLength + look), 0);
                Vec2 start = position + new Vec2(direction * envelope.HalfLength, 0);
                if (Airframe.Trace(start, position + forward) || Airframe.Trace(start + new Vec2(0, envelope.HubHeight), position + forward + new Vec2(0, envelope.HubHeight))) return true;
            }
            if (Observation.Velocity.Y > .5f) {
                float up = Observation.Velocity.Y * Observation.ResponseSeconds;
                for (int side = -1; side <= 1; side += 2) {
                    Vec2 top = position + new Vec2(side * Observation.RotorRadius, envelope.HubHeight);
                    if (Airframe.Trace(top, top + new Vec2(0, up))) return true;
                }
            }
            return false;
        }
        public bool Hold() {
            if (!Airframe.Observe(out Observation) || !Observation.Finite || Observation.Critical || !(envelope.PositionGain > 0) || !float.IsFinite(envelope.PositionGain)) return false;
            if (!holdingSet) { holdingSet = true; holdingPosition = Observation.Position; holdingHeading = Observation.Heading; }
            Vec2 error = holdingPosition - Observation.Position;
            Vec2 velocity = new Vec2(Math.Clamp(error.X * envelope.PositionGain, -Observation.MaximumSpeed, Observation.MaximumSpeed),
                Math.Clamp(error.Y * envelope.PositionGain, -Observation.ClimbRate, Observation.ClimbRate));
            return Airframe.Command(owner, velocity, Vec2.Zero, holdingHeading, 8u | (Master ? 2u : 0u) | (Observation.Grounded ? 1u : 0u));
        }
        public bool WarmStart(Vec2 velocity) => Airframe.WarmStart(owner, velocity);
        public void Complete() => completed = true;
        public void Dispose() { if (acquired) { acquired = false; Airframe.Release(owner, completed); } }
    }

    public static class CinematicVehicleConditions {
        public static bool Test(CinematicPlan plan, CinematicVehicleDirector[] vehicles, string condition, out bool known) {
            known = true;
            if (condition.StartsWith("flight.", StringComparison.Ordinal)) {
                int split = condition.IndexOf('.', 7);
                if (split > 7) {
                    int binding = plan.BindingIndex(condition.AsSpan(7, split - 7));
                    if (binding >= 0 && vehicles[binding] is CinematicVehicleDirector v) {
                        ReadOnlySpan<char> state = condition.AsSpan(split + 1);
                        switch (state) {
                            case "ready": return v.Observation.Ready;
                            case "arrived": return v.Controller.Arrived;
                            case "landed": return v.Controller.Landed;
                            case "contact": return v.Observation.Grounded;
                            case "unsafe": return v.Controller.Unsafe;
                            default: return v.Passed(state);
                        }
                    }
                    if (binding >= 0) return false;
                }
                bool found = false, unsafeFlight = condition == "flight.unsafe";
                for (int i = 0; i < vehicles.Length; i++) {
                    CinematicVehicleDirector vehicle = vehicles[i];
                    if (vehicle == null) continue;
                    found = true;
                    bool matches = condition switch {
                        "flight.ready" => vehicle.Observation.Ready,
                        "flight.arrived" => vehicle.Controller.Arrived,
                        "flight.landed" => vehicle.Controller.Landed,
                        "flight.contact" => vehicle.Observation.Grounded,
                        "flight.unsafe" => vehicle.Controller.Unsafe,
                        _ => false
                    };
                    if (unsafeFlight && matches) return true;
                    if (!matches && !unsafeFlight) return false;
                }
                return found && !unsafeFlight;
            }
            known = false;
            return false;
        }
    }
}
