using System;
using ScpItdMod.Core;

namespace ScpItdMod.Cinematics {
    public struct CinematicFlightObservation {
        public Vec2 Position, Velocity;
        public float Tilt, Heading, AngularRate, RotorShare, MaximumTilt, MaximumSpeed, ClimbRate, ResponseSeconds, RotorRadius, InducedVelocity, Scale;
        public float SurfaceHeight, SurfaceNormalY, SurfaceMismatch, SuspensionTravel, TouchdownRate, PositionGain, MinimumRotorShare;
        public bool Grounded, Ready, Critical, SupportAvailable, CorridorBlocked, Master, GearReady;
        public bool Finite => Valid(Position) && Valid(Velocity) && float.IsFinite(Tilt) && float.IsFinite(AngularRate) && float.IsFinite(Heading) &&
            float.IsFinite(RotorShare) && MaximumTilt > 0 && MaximumTilt < 1.5f && float.IsFinite(MaximumTilt) &&
            MaximumSpeed > 0 && float.IsFinite(MaximumSpeed) && ClimbRate > 0 && float.IsFinite(ClimbRate) &&
            ResponseSeconds > 0 && float.IsFinite(ResponseSeconds) && RotorRadius > 0 && float.IsFinite(RotorRadius) && float.IsFinite(InducedVelocity) && Scale > 0 && float.IsFinite(Scale);
        private static bool Valid(Vec2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y);
    }

    public readonly struct CinematicFlightCommand {
        public readonly Vec2 Velocity, Acceleration;
        public readonly float Heading;
        public readonly bool Landing;
        internal CinematicFlightCommand(Vec2 velocity, Vec2 acceleration, float heading, bool landing) { Velocity = velocity; Acceleration = acceleration; Heading = heading; Landing = landing; }
    }

    public sealed class CinematicFlightController {
        private CinematicWaypoint[] route = Array.Empty<CinematicWaypoint>();
        private CinematicWaypoint goal;
        private Vec2 velocity, acceleration, origin;
        public const float AttitudePatienceSeconds = 3f;
        private float previousX, heading, desiredHeading, stable, held, patience, units = 100f;
        private bool initialized, goalKnown, fromAbove, descending, contacted, ready, aligning, headingReleased;
        public int Waypoint { get; private set; }
        public uint Passed { get; private set; }
        public CinematicWaypoint Goal => goal;
        public bool Arrived { get; private set; }
        public bool Landed { get; private set; }
        public bool Unsafe { get; private set; }
        public string Failure { get; private set; } = "";
        public Vec2 Error { get; private set; }
        public float HeadingError { get; private set; }
        public bool AligningHeading => aligning;
        public bool SettlingContact => contacted;
        public float StableSeconds => stable;
        public CinematicFlightCommand Last { get; private set; }
        public void SetOrigin(Vec2 anchor, float unitsPerMetre) {
            if (!(unitsPerMetre > 0) || !float.IsFinite(unitsPerMetre)) throw new ArgumentOutOfRangeException(nameof(unitsPerMetre));
            origin = anchor; units = unitsPerMetre;
        }
        private Vec2 Metres(CinematicWaypoint point) => (origin + point.Position) / units;
        public void SetRoute(CinematicCue cue, Vec2 anchor, float unitsPerMetre) {
            if (cue == null || cue.Action != CinematicAction.Flight || cue.Route.Length == 0) throw new ArgumentException("A flight cue with a route is required", nameof(cue));
            SetOrigin(anchor, unitsPerMetre);
            route = cue.Route; Waypoint = 0; Passed = 0;
            initialized = false;
            Arrived = Landed = Unsafe = false; Failure = "";
            Begin(route[0]);
        }
        private void Begin(CinematicWaypoint next) {
            goal = next; goalKnown = false; descending = contacted = false; stable = 0; held = 0; patience = 0;
        }
        private void Reach(float dt) {
            if (goal.Mode == CinematicFlightMode.Hover && goal.Hold > 0 && (held += dt) < goal.Hold) return;
            Passed |= 1u << Waypoint;
            if (Waypoint + 1 < route.Length) { Waypoint++; Begin(route[Waypoint]); return; }
            Arrived = true;
        }
        public CinematicFlightCommand Step(in CinematicFlightObservation state, float dt) {
            if (!(dt > 0f) || dt > .25f || !float.IsFinite(dt)) throw new ArgumentOutOfRangeException(nameof(dt));
            if (!state.Finite) { Reject("Nonfinite or invalid flight telemetry"); return Last; }
            if (!initialized) { initialized = true; velocity = state.Velocity; acceleration = Vec2.Zero; heading = state.Heading; }
            ready |= state.Ready;
            if (state.Grounded && state.RotorShare < state.MinimumRotorShare) ready = false;
            if (state.Critical) Reject("Airframe or flight control unavailable");
            if (ready && !state.Grounded && state.RotorShare < state.MinimumRotorShare) Reject("Insufficient rotor speed for flight augmentation");
            if (goal == null || !ready && state.Grounded) {
                heading = state.Heading;
                Last = new CinematicFlightCommand(Vec2.Zero, Vec2.Zero, heading, state.Grounded);
                return Last;
            }
            if (!(state.PositionGain > 0) || !float.IsFinite(state.PositionGain) || !(state.MinimumRotorShare >= 0) || state.MinimumRotorShare >= 1) {
                Reject("Invalid regulation envelope"); return Last;
            }
            Vec2 target = Metres(goal);
            bool final = Waypoint == route.Length - 1;
            if (!goalKnown) {
                previousX = target.X - state.Position.X; goalKnown = true;
                desiredHeading = goal.HasHeading ? goal.Heading : MathF.Abs(previousX) > goal.Radius ? previousX > 0 ? MathF.PI : 0 : state.Heading;
                aligning = MathF.Abs(MathF.IEEERemainder(desiredHeading - state.Heading, MathF.Tau)) > goal.HeadingTolerance;
                headingReleased = false; heading = aligning ? state.Heading : desiredHeading;
                fromAbove = goal.Mode == CinematicFlightMode.Hover && state.Position.Y > target.Y + goal.Radius;
            }
            if (aligning && !headingReleased) {
                aligning = MathF.Abs(MathF.IEEERemainder(desiredHeading - state.Heading, MathF.Tau)) > goal.HeadingTolerance;
                heading = aligning ? state.Heading : desiredHeading;
            }
            float horizontalAuthority = 9.81f * MathF.Tan(state.MaximumTilt);
            HeadingError = MathF.IEEERemainder(desiredHeading - state.Heading, MathF.Tau);
            float maxAcceleration = MathF.Min(goal.Acceleration, horizontalAuthority), lag = MathF.Max(.1f, state.ResponseSeconds);
            float verticalAuthority = MathF.Min(goal.Acceleration, 9.81f * .5f);
            if (state.CorridorBlocked) Reject("Flight corridor intersects world geometry");
            bool landing = goal.Mode == CinematicFlightMode.Land;
            if (landing) {
                if (!state.SupportAvailable || !float.IsFinite(state.SurfaceHeight) || !float.IsFinite(state.SurfaceNormalY) || !float.IsFinite(state.SurfaceMismatch) ||
                    !(state.SuspensionTravel > 0) || !(state.TouchdownRate > 0)) { Reject("Landing site has no valid support beneath every gear leg"); return Last; }
                else if (state.SurfaceNormalY < MathF.Cos(goal.MaximumSlope) || state.SurfaceMismatch > state.SuspensionTravel) Reject("Landing surface exceeds slope or suspension limits");
                target = new Vec2(target.X, state.SurfaceHeight);
            }
            Error = target - state.Position;
            float speedLimit = MathF.Min(goal.Speed, state.MaximumSpeed);
            float gain = state.PositionGain;
            float terminal = landing ? 0 : goal.TerminalSpeed > 0 ? goal.TerminalSpeed : !final && goal.Mode == CinematicFlightMode.Cruise ? CornerSpeed(target, speedLimit, maxAcceleration) : 0;
            float desiredX = BrakingSpeed(Error.X, maxAcceleration, lag, gain, terminal, speedLimit, landing);
            float verticalLimit = goal.Mode == CinematicFlightMode.Hover && goal.Speed > 0 ? MathF.Min(state.ClimbRate, goal.Speed) : state.ClimbRate;
            float desiredY = BrakingSpeed(Error.Y, verticalAuthority, lag, gain, 0, verticalLimit, landing);
            if (aligning) {
                if (MathF.Abs(state.Velocity.X) <= goal.LandingSpeed && MathF.Abs(velocity.X) <= goal.LandingSpeed) headingReleased = true;
                if (headingReleased) heading = desiredHeading;
                if (headingReleased && MathF.Abs(HeadingError) <= goal.HeadingTolerance) aligning = false;
                else { desiredX = 0; desiredY = MathF.Max(0, desiredY); }
            }
            bool contact = false;
            if (landing && !Unsafe) {
                float stopping = MathF.Abs(state.Velocity.X) * lag + state.Velocity.X * state.Velocity.X / (2f * maxAcceleration);
                bool positioned = !aligning && state.GearReady && MathF.Abs(Error.X) + stopping <= goal.Radius && MathF.Abs(state.Velocity.X) <= goal.LandingSpeed;
                bool steady = MathF.Abs(state.Tilt) <= goal.MaximumSlope && MathF.Abs(state.AngularRate) < goal.LandingSpeed / state.RotorRadius;
                patience = positioned && !steady && !contacted ? patience + dt : positioned ? patience : 0f;
                bool aligned = positioned && (steady || patience >= AttitudePatienceSeconds);
                contacted |= state.Grounded;
                if (!descending && aligned && MathF.Abs(state.Velocity.Y) < goal.LandingSpeed) descending = true;
                if (descending && !aligned && !contacted) descending = false;
                if (state.Grounded && MathF.Abs(Error.X) > goal.Radius) Reject("Touchdown is outside the approved landing envelope");
                if (contacted && !state.Grounded && state.Position.Y - target.Y > goal.Clearance) Reject("Landing contact was lost above the clearance envelope");
                if (!descending && !contacted) desiredY = BrakingSpeed(target.Y + goal.Clearance - state.Position.Y, verticalAuthority, lag, gain, 0, state.ClimbRate, true);
                else {
                    float touchdown = MathF.Min(goal.LandingRate, state.TouchdownRate);
                    desiredY = -MathF.Min(state.ClimbRate, touchdown + BrakingSpeed(MathF.Max(0, state.Position.Y - target.Y), verticalAuthority, lag, gain, 0, state.ClimbRate, true));
                    contact = contacted;
                }
                bool settled = contact && state.Grounded && aligned && MathF.Abs(state.Velocity.Y) < goal.LandingRate;
                stable = settled ? stable + dt : 0;
                Landed = stable >= goal.SettleSeconds;
                if (Landed && !Arrived) Reach(dt);
            } else if (!Unsafe && !(final && Arrived)) {
                bool passing = goal.TerminalSpeed > 0 || !final && goal.Mode == CinematicFlightMode.Cruise;
                bool crossing = passing && previousX * Error.X <= 0;
                bool near = MathF.Abs(Error.X) <= goal.Radius && (passing || MathF.Abs(state.Velocity.X) <= goal.LandingSpeed);
                bool through = fromAbove && Error.Y >= 0 && MathF.Abs(Error.X) <= goal.Radius;
                if (!state.Grounded && !aligning && (crossing || near || through) && (MathF.Abs(Error.Y) <= goal.Radius || through) && (passing || through || MathF.Abs(state.Velocity.Y) <= goal.LandingSpeed)) Reach(dt);
                else held = 0;
            }
            previousX = Error.X;
            if (Unsafe) { desiredX = 0; desiredY = state.Grounded ? 0 : MathF.Min(state.ClimbRate, MathF.Max(0f, state.Velocity.Y)); }
            Vec2 previousAcceleration = acceleration;
            AdvanceAxis(ref velocity, ref acceleration, new Vec2(desiredX, desiredY), maxAcceleration, verticalAuthority, goal.Jerk, lag, dt);
            Vec2 feedforward = (previousAcceleration + acceleration) * .5f;
            Last = new CinematicFlightCommand(velocity, feedforward, heading, contact);
            return Last;
        }
        private float CornerSpeed(Vec2 target, float speedLimit, float maxAcceleration) {
            CinematicWaypoint next = route[Waypoint + 1];
            float leg = Metres(next).X - target.X;
            if (leg * Error.X <= 0) return 0;
            float carry = MathF.Sqrt(next.TerminalSpeed * next.TerminalSpeed + 2f * maxAcceleration * MathF.Abs(leg));
            return MathF.Min(MathF.Min(speedLimit, next.Speed), carry);
        }
        private void Reject(string reason) { if (!Unsafe) { Unsafe = true; Failure = reason; } }
        private static float BrakingSpeed(float error, float acceleration, float lag, float positionGain, float terminal, float maximum, bool capture) {
            float latency = acceleration * lag;
            float braking = MathF.Sqrt(latency * latency + terminal * terminal + 2f * acceleration * MathF.Abs(error)) - latency;
            float proportional = MathF.Abs(error) * positionGain;
            return MathF.Sign(error) * MathF.Min(maximum, terminal > 0 ? MathF.Max(terminal, braking) : capture ? MathF.Min(braking, proportional) : braking);
        }
        private static void AdvanceAxis(ref Vec2 speed, ref Vec2 rate, Vec2 target, float horizontal, float vertical, float jerk, float response, float dt) {
            float x = speed.X, y = speed.Y, ax = rate.X, ay = rate.Y;
            Axis(ref x, ref ax, target.X, horizontal, jerk, response, dt);
            Axis(ref y, ref ay, target.Y, vertical, jerk, response, dt);
            speed = new Vec2(x, y); rate = new Vec2(ax, ay);
        }
        private static void Axis(ref float velocity, ref float acceleration, float desired, float maximum, float jerk, float response, float dt) {
            float error = desired - velocity;
            float target = Math.Clamp(error * (1f - MathF.Exp(-dt / response)) / dt, -maximum, maximum);
            float previous = acceleration;
            acceleration += Math.Clamp(target - acceleration, -jerk * dt, jerk * dt);
            acceleration = Math.Clamp(acceleration, -maximum, maximum);
            velocity += (previous + acceleration) * (.5f * dt);
        }
    }
}
