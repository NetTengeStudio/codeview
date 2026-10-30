using System;
using System.Reflection;
using Barotrauma;
using Microsoft.Xna.Framework;
using ScpItdMod.Core;

namespace ScpItdMod.Cinematics {
    internal sealed class BovCinematicAdapter {
        internal const int RequiredVersion = 5;
        internal const string BridgeTypeName = "BlackOctoberVehicles.CinematicBridge";
        internal delegate bool AcquireCall(Item item, ulong owner);
        internal delegate bool WarmCall(Item item, ulong owner, Vector2 velocity);
        internal delegate bool CommandCall(Item item, ulong owner, Vector2 velocity, Vector2 acceleration, float heading, uint switches);
        internal delegate void ReleaseCall(Item item, ulong owner, bool completed);
        internal delegate bool ObserveCall(Item item, out Vector4 motion, out Vector4 attitude, out Vector4 limits, out Vector4 geometry, out uint flags);
        internal delegate bool SurfaceCall(Item item, Vector2 site, out Vector4 support);
        internal delegate bool EnvelopeCall(Item item, out Vector4 envelope, out Vector4 regulation);
        internal delegate bool TraceCall(Item item, Vector2 from, Vector2 to, out Vector2 point, out Vector2 normal);
        internal delegate bool OutwashCall(Item item, Vector2 point, out Vector4 flow);
        internal delegate Character OccupantCall(Item item, int seat);
        internal delegate bool IsRotorcraftCall(Item item);
        internal delegate int PassengerSeatsCall(Item item, Span<int> seats);
        internal delegate bool ActorCall(Item item, Character actor, int seat, byte operation);
        internal delegate bool SandboxAcquireCall(object sandbox, ulong owner);
        internal delegate bool SandboxWarmCall(object sandbox, ulong owner, Vector2 velocity);
        internal delegate bool SandboxCommandCall(object sandbox, ulong owner, Vector2 velocity, Vector2 acceleration, float heading, uint switches);
        internal delegate void SandboxReleaseCall(object sandbox, ulong owner, bool completed);
        internal delegate bool SandboxObserveCall(object sandbox, out Vector4 motion, out Vector4 attitude, out Vector4 limits, out Vector4 geometry, out uint flags);
        internal delegate bool SandboxSurfaceCall(object sandbox, Vector2 site, out Vector4 support);
        internal delegate bool SandboxEnvelopeCall(object sandbox, out Vector4 envelope, out Vector4 regulation);
        internal delegate bool SandboxTraceCall(object sandbox, Vector2 from, Vector2 to, out Vector2 point, out Vector2 normal);
        internal delegate bool SandboxOutwashCall(object sandbox, Vector2 point, out Vector4 flow);
        internal delegate object SandboxCreateCall(Item template, Vector2 originSim, uint seed);
        internal delegate void SandboxGroundCall(object sandbox, ReadOnlySpan<Vector2> polygon, float grip);
        internal delegate void SandboxConfigureCall(object sandbox, float payloadKg, Vector2 wind, bool gusts);
        internal delegate bool SandboxStepCall(object sandbox, float dt);
        internal delegate bool SandboxPoseCall(object sandbox, Span<float> pose);
#if CLIENT
        internal delegate void ListenerCall(ulong owner, Vector2 velocity, bool enabled);
        internal delegate object GhostCreateCall(object sandbox);
        internal delegate bool GhostPrepareCall(object ghost, Microsoft.Xna.Framework.Graphics.GraphicsDevice graphics, ReadOnlySpan<float> pose, float dt, float zoom);
        internal delegate bool GhostDrawCall(object ghost, Microsoft.Xna.Framework.Graphics.SpriteBatch batch, Color color, float depth);
        internal delegate void GhostDisposeCall(object ghost);
        internal readonly ListenerCall Listener;
        internal readonly Action<Item, float> PresentationGain;
        internal readonly GhostCreateCall GhostCreate;
        internal readonly GhostPrepareCall GhostPrepare;
        internal readonly GhostDrawCall GhostDraw;
        internal readonly GhostDisposeCall GhostDispose;
#endif
        internal readonly int PoseFloats;
        internal readonly Func<bool> Active;
        internal readonly AcquireCall Acquire;
        internal readonly WarmCall Warm;
        internal readonly CommandCall Command;
        internal readonly ReleaseCall Release;
        internal readonly ObserveCall Observe;
        internal readonly SurfaceCall Surface;
        internal readonly EnvelopeCall Envelope;
        internal readonly TraceCall Trace;
        internal readonly OutwashCall Outwash;
        internal readonly OccupantCall Occupant;
        internal readonly IsRotorcraftCall IsRotorcraft;
        internal readonly PassengerSeatsCall PassengerSeats;
        internal readonly ActorCall Actor;
        internal readonly Func<Character, string> Refusal, Ending;
        internal readonly SandboxAcquireCall SandboxAcquire;
        internal readonly SandboxWarmCall SandboxWarm;
        internal readonly SandboxCommandCall SandboxCommand;
        internal readonly SandboxReleaseCall SandboxRelease;
        internal readonly SandboxObserveCall SandboxObserve;
        internal readonly SandboxSurfaceCall SandboxSurface;
        internal readonly SandboxEnvelopeCall SandboxEnvelope;
        internal readonly SandboxTraceCall SandboxTrace;
        internal readonly SandboxOutwashCall SandboxOutwash;
        internal readonly SandboxCreateCall SandboxCreate;
        internal readonly SandboxGroundCall SandboxGround;
        internal readonly SandboxConfigureCall SandboxConfigure;
        internal readonly SandboxStepCall SandboxStep;
        internal readonly SandboxPoseCall SandboxPose;
        private static BovCinematicAdapter cached;
        private BovCinematicAdapter(Type type) {
            if (!Compatible(type)) throw new InvalidOperationException("BOVehicles CinematicBridge version " + RequiredVersion + " required");
            Active = Bind<Func<bool>>(type, "IsActive");
            PoseFloats = (int)(type.GetField("PoseFloats", BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue() ?? 0);
            if (PoseFloats <= 0 || PoseFloats > 256) throw new InvalidOperationException("BOVehicles CinematicBridge pose size is invalid");
            Acquire = Bind<AcquireCall>(type, "Acquire"); Warm = Bind<WarmCall>(type, "WarmStart"); Command = Bind<CommandCall>(type, "Command"); Release = Bind<ReleaseCall>(type, "Release");
            Observe = Bind<ObserveCall>(type, "Observe"); Surface = Bind<SurfaceCall>(type, "Surface"); Trace = Bind<TraceCall>(type, "Trace");
            Envelope = Bind<EnvelopeCall>(type, "Envelope"); Outwash = Bind<OutwashCall>(type, "Outwash");
            Occupant = Bind<OccupantCall>(type, "Occupant"); Actor = Bind<ActorCall>(type, "Actor");
            Refusal = BindOptional<Func<Character, string>>(type, "Refusal"); Ending = BindOptional<Func<Character, string>>(type, "Ending");
            IsRotorcraft = Bind<IsRotorcraftCall>(type, "IsRotorcraft"); PassengerSeats = Bind<PassengerSeatsCall>(type, "PassengerSeats");
            SandboxAcquire = Bind<SandboxAcquireCall>(type, "SandboxAcquire"); SandboxWarm = Bind<SandboxWarmCall>(type, "SandboxWarmStart"); SandboxCommand = Bind<SandboxCommandCall>(type, "SandboxCommand");
            SandboxRelease = Bind<SandboxReleaseCall>(type, "SandboxRelease"); SandboxObserve = Bind<SandboxObserveCall>(type, "SandboxObserve");
            SandboxSurface = Bind<SandboxSurfaceCall>(type, "SandboxSurface"); SandboxEnvelope = Bind<SandboxEnvelopeCall>(type, "SandboxEnvelope");
            SandboxTrace = Bind<SandboxTraceCall>(type, "SandboxTrace"); SandboxOutwash = Bind<SandboxOutwashCall>(type, "SandboxOutwash");
            SandboxCreate = Bind<SandboxCreateCall>(type, "SandboxCreate"); SandboxGround = Bind<SandboxGroundCall>(type, "SandboxGround");
            SandboxConfigure = Bind<SandboxConfigureCall>(type, "SandboxConfigure"); SandboxStep = Bind<SandboxStepCall>(type, "SandboxStep");
            SandboxPose = Bind<SandboxPoseCall>(type, "SandboxPose");
#if CLIENT
            Listener = Bind<ListenerCall>(type, "CameraListener");
            PresentationGain = Bind<Action<Item, float>>(type, "PresentationGain");
            GhostCreate = Bind<GhostCreateCall>(type, "GhostCreate"); GhostPrepare = Bind<GhostPrepareCall>(type, "GhostPrepare");
            GhostDraw = Bind<GhostDrawCall>(type, "GhostDraw"); GhostDispose = Bind<GhostDisposeCall>(type, "GhostDispose");
#endif
        }
        private static T Bind<T>(Type type, string name) where T : Delegate => (T)(type.GetMethod(name, BindingFlags.Public | BindingFlags.Static) ?? throw new MissingMethodException(type.FullName, name)).CreateDelegate(typeof(T));
        private static T BindOptional<T>(Type type, string name) where T : Delegate => type.GetMethod(name, BindingFlags.Public | BindingFlags.Static) is { } method ? (T)method.CreateDelegate(typeof(T)) : (T)(Delegate)new Func<Character, string>(_ => "");
        private static bool Compatible(Type type) =>
            (int)(type.GetField("Version", BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue() ?? 0) == RequiredVersion && type.GetMethod("IsActive", BindingFlags.Public | BindingFlags.Static) != null;
        private static bool Running(Type type) {
            try { return (bool)type.GetMethod("IsActive", BindingFlags.Public | BindingFlags.Static).Invoke(null, null); }
            catch (Exception) { return false; }
        }
        internal static bool TryCreate(out BovCinematicAdapter adapter, out string failure) {
            failure = "";
            if (cached != null && cached.Active()) { adapter = cached; return true; }
            cached = null; adapter = null;
            var types = new System.Collections.Generic.List<Type>();
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()) {
                Type type;
                try { type = assembly.GetType(BridgeTypeName, false); } catch (Exception) { continue; }
                if (type != null) types.Add(type);
            }
            Span<CinematicBridgeCandidate> states = stackalloc CinematicBridgeCandidate[Math.Max(1, types.Count)];
            for (int i = 0; i < types.Count; i++) { bool compatible = Compatible(types[i]); states[i] = new CinematicBridgeCandidate(compatible, compatible && Running(types[i])); }
            CinematicBridgeVerdict verdict = CinematicBridgeChoice.Pick(states.Slice(0, types.Count), out int index);
            if (verdict != CinematicBridgeVerdict.Chosen) { failure = CinematicBridgeChoice.Explain(verdict, types.Count, RequiredVersion); return false; }
            try { adapter = cached = new BovCinematicAdapter(types[index]); return true; }
            catch (Exception error) { failure = "Incompatible BOVehicles: " + error.Message; return false; }
        }
        internal bool Read(Item item, out CinematicFlightObservation observation) {
            observation = default;
            return item is { Removed: false } && Observe(item, out Vector4 motion, out Vector4 attitude, out Vector4 limits, out Vector4 geometry, out uint flags) && CinematicBovFormat.Convert(motion, attitude, limits, geometry, flags, out observation);
        }
    }

    internal sealed class LiveRotorcraft : ICinematicRotorcraft {
        private readonly BovCinematicAdapter bov;
        internal readonly Item Item;
        internal LiveRotorcraft(BovCinematicAdapter bov, Item item) { this.bov = bov; Item = item; }
        public bool Observe(out CinematicFlightObservation observation) => bov.Read(Item, out observation);
        public bool Envelope(out CinematicFlightEnvelope envelope) {
            envelope = default;
            if (!bov.Envelope(Item, out Vector4 e, out Vector4 r)) return false;
            envelope = CinematicBovFormat.Envelope(e, r); return true;
        }
        public bool Acquire(ulong owner) => bov.Acquire(Item, owner);
        public bool WarmStart(ulong owner, Vec2 velocity) => bov.Warm(Item, owner, new Vector2(velocity.X, velocity.Y));
        public bool Command(ulong owner, Vec2 velocity, Vec2 acceleration, float heading, uint switches) => bov.Command(Item, owner, new Vector2(velocity.X, velocity.Y), new Vector2(acceleration.X, acceleration.Y), heading, switches);
        public void Release(ulong owner, bool completed) => bov.Release(Item, owner, completed);
        public bool Surface(Vec2 site, out float height, out float normalY, out float mismatch) {
            bool found = bov.Surface(Item, new Vector2(site.X, site.Y), out Vector4 support);
            height = support.X; normalY = support.Y; mismatch = support.Z; return found;
        }
        public bool Trace(Vec2 from, Vec2 to) => bov.Trace(Item, new Vector2(from.X, from.Y), new Vector2(to.X, to.Y), out _, out _);
        public bool Outwash(Vec2 point, out CinematicOutwash flow) {
            bool found = bov.Outwash(Item, new Vector2(point.X, point.Y), out Vector4 raw);
            flow = CinematicBovFormat.Flow(raw); return found;
        }
    }

    internal sealed class SandboxRotorcraft : ICinematicRotorcraft {
        private readonly BovCinematicAdapter bov;
        internal readonly object Sandbox;
        internal SandboxRotorcraft(BovCinematicAdapter bov, object sandbox) { this.bov = bov; Sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox)); }
        public bool Observe(out CinematicFlightObservation observation) {
            observation = default;
            return bov.SandboxObserve(Sandbox, out Vector4 motion, out Vector4 attitude, out Vector4 limits, out Vector4 geometry, out uint flags) &&
                CinematicBovFormat.Convert(motion, attitude, limits, geometry, flags, out observation);
        }
        public bool Envelope(out CinematicFlightEnvelope envelope) {
            envelope = default;
            if (!bov.SandboxEnvelope(Sandbox, out Vector4 e, out Vector4 r)) return false;
            envelope = CinematicBovFormat.Envelope(e, r); return true;
        }
        public bool Acquire(ulong owner) => bov.SandboxAcquire(Sandbox, owner);
        public bool WarmStart(ulong owner, Vec2 velocity) => bov.SandboxWarm(Sandbox, owner, new Vector2(velocity.X, velocity.Y));
        public bool Command(ulong owner, Vec2 velocity, Vec2 acceleration, float heading, uint switches) => bov.SandboxCommand(Sandbox, owner, new Vector2(velocity.X, velocity.Y), new Vector2(acceleration.X, acceleration.Y), heading, switches);
        public void Release(ulong owner, bool completed) => bov.SandboxRelease(Sandbox, owner, completed);
        public bool Surface(Vec2 site, out float height, out float normalY, out float mismatch) {
            bool found = bov.SandboxSurface(Sandbox, new Vector2(site.X, site.Y), out Vector4 support);
            height = support.X; normalY = support.Y; mismatch = support.Z; return found;
        }
        public bool Trace(Vec2 from, Vec2 to) => bov.SandboxTrace(Sandbox, new Vector2(from.X, from.Y), new Vector2(to.X, to.Y), out _, out _);
        public bool Outwash(Vec2 point, out CinematicOutwash flow) {
            bool found = bov.SandboxOutwash(Sandbox, new Vector2(point.X, point.Y), out Vector4 raw);
            flow = CinematicBovFormat.Flow(raw); return found;
        }
    }
}
