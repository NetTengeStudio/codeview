using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;
using ScpItdMod.Core;

namespace ScpItdMod.Cinematics {
    public enum CinematicBindingKind : byte { Anchor, Rotorcraft, Actor, Item }
    public enum CinematicAction : byte { Flight, Camera, Move, Face, Pose, Interact, Board, Disembark, Signal, Light, Particle, Sound, Engine, Wash, Letterbox, Fade, Title, Despawn }
    public enum CinematicFlightMode : byte { Cruise, Hover, Land }
    public enum CinematicCameraMode : byte { Establish, Follow, Fixed }

    public sealed class CinematicBinding {
        public readonly string Id;
        public readonly CinematicBindingKind Kind;
        public readonly bool Required;
        public readonly int Seat, Vehicle;
        internal CinematicBinding(string id, CinematicBindingKind kind, bool required, int seat, int vehicle) {
            Id = id; Kind = kind; Required = required; Seat = seat; Vehicle = vehicle;
        }
    }

    public sealed class CinematicWaypoint {
        public readonly string Id;
        public readonly Vec2 Position;
        public readonly CinematicFlightMode Mode;
        public readonly float Speed, TerminalSpeed, Radius, Acceleration, Jerk, Hold;
        public readonly float LandingRate, LandingSpeed, SettleSeconds, MaximumSlope, Clearance;
        public readonly bool HasHeading;
        public readonly float Heading, HeadingTolerance;
        internal static readonly HashSet<string> Attributes = new HashSet<string>("id x y mode speed terminalSpeed radius acceleration jerk hold landingRate landingSpeed settle maxSlopeDegrees clearance headingDegrees headingToleranceDegrees".Split(' '), StringComparer.Ordinal);
        internal CinematicWaypoint(XElement e) {
            foreach (XAttribute a in e.Attributes()) if (!Attributes.Contains(a.Name.LocalName)) throw CinematicCue.Error(e, "Unknown waypoint attribute " + a.Name);
            if (e.HasElements) throw CinematicCue.Error(e, "A waypoint cannot contain nested XML");
            Id = CinematicCue.Text(e, "id", "");
            if (Id.Length > 0) CinematicPlan.ValidateId(e, Id);
            if (Id is "ready" or "arrived" or "landed" or "unsafe" or "contact") throw CinematicCue.Error(e, "Waypoint id is reserved for a measured flight state: " + Id);
            Position = new Vec2(CinematicCue.Number(e, "x", 0, -1000000, 1000000), CinematicCue.Number(e, "y", 0, -1000000, 1000000));
            if (!Enum.TryParse(CinematicCue.Text(e, "mode", "Cruise"), false, out Mode) || !Enum.IsDefined(typeof(CinematicFlightMode), Mode)) throw CinematicCue.Error(e, "Unknown flight mode");
            Speed = CinematicCue.Number(e, "speed", 20, .1f, 150); TerminalSpeed = CinematicCue.Number(e, "terminalSpeed", 0, 0, Speed);
            Radius = CinematicCue.Number(e, "radius", 2, .05f, 100); Acceleration = CinematicCue.Number(e, "acceleration", 1.5f, .05f, 10);
            Jerk = CinematicCue.Number(e, "jerk", 1, .05f, 20); Hold = CinematicCue.Number(e, "hold", 0, 0, 600);
            LandingRate = CinematicCue.Number(e, "landingRate", .35f, .05f, 2); LandingSpeed = CinematicCue.Number(e, "landingSpeed", .75f, .05f, 3);
            SettleSeconds = CinematicCue.Number(e, "settle", 2, .2f, 10); MaximumSlope = CinematicCue.Number(e, "maxSlopeDegrees", 12, 0, 25) * (MathF.PI / 180f);
            Clearance = CinematicCue.Number(e, "clearance", 5, .5f, 50);
            HasHeading = e.Attribute("headingDegrees") != null; Heading = CinematicCue.Number(e, "headingDegrees", 0, -360, 360) * (MathF.PI / 180f);
            HeadingTolerance = CinematicCue.Number(e, "headingToleranceDegrees", 5, .5f, 30) * (MathF.PI / 180f);
            if (Mode == CinematicFlightMode.Cruise && Hold > 0) throw CinematicCue.Error(e, "A fly-through waypoint cannot hold; use Hover");
        }
    }

    public sealed class CinematicCue {
        public readonly string Id, Resource, Value;
        public readonly CinematicAction Action;
        public readonly int Target, RelativeTo, Seat;
        public readonly double At, Duration;
        public readonly Vec2 Position, End, Velocity;
        public readonly float Speed, Radius, Zoom, LookAhead, FrameX, FrameY, Blend, Intensity, Impulse;
        public readonly bool Cut, Enabled, MatchVelocity;
        public readonly CinematicCameraMode CameraMode;
        public readonly CinematicWaypoint[] Route;
        internal readonly string[] Dependencies;
        internal readonly int SourceOrder;
        private static readonly HashSet<string> attributes = new HashSet<string>("id resource value target relativeTo seat at duration x y endX endY vx vy speed radius zoom lookAhead frameX frameY blend intensity cut enabled mode after matchVelocity impulse".Split(' '), StringComparer.Ordinal);
        internal CinematicCue(XElement e, Dictionary<string, int> bindings, double offset, int order) {
            foreach (XAttribute a in e.Attributes()) if (!attributes.Contains(a.Name.LocalName)) throw Error(e, "Unknown cue attribute " + a.Name);
            SourceOrder = order;
            if (!Enum.TryParse(e.Name.LocalName, false, out Action)) throw Error(e, "Unknown action " + e.Name);
            Id = Text(e, "id", ""); Resource = Text(e, "resource", ""); Value = Text(e, "value", "");
            Target = Binding(e, "target", bindings); RelativeTo = Binding(e, "relativeTo", bindings);
            Seat = Integer(e, "seat", 0, 0, 15);
            At = offset + Number(e, "at", 0, 0, 3600); Duration = Number(e, "duration", 0, 0, 3600);
            Position = new Vec2(Number(e, "x", 0, -1000000, 1000000), Number(e, "y", 0, -1000000, 1000000));
            End = new Vec2(Number(e, "endX", Position.X, -1000000, 1000000), Number(e, "endY", Position.Y, -1000000, 1000000));
            Velocity = new Vec2(Number(e, "vx", 0, -300, 300), Number(e, "vy", 0, -50, 50));
            Speed = Number(e, "speed", 1.5f, .1f, 150); Radius = Number(e, "radius", 2, .05f, 1000);
            Zoom = Number(e, "zoom", 1, .1f, 2);
            LookAhead = Number(e, "lookAhead", .8f, 0, 5); FrameX = Number(e, "frameX", .5f, .1f, .9f); FrameY = Number(e, "frameY", .55f, .1f, .9f);
            Blend = Number(e, "blend", 2, .01f, 30); Intensity = Number(e, "intensity", 1, 0, 10);
            Impulse = Number(e, "impulse", 0, 0, 1); MatchVelocity = Boolean(e, "matchVelocity", false); Cut = Boolean(e, "cut", false); Enabled = Boolean(e, "enabled", true);
            if ((!Enum.TryParse(Text(e, "mode", "Establish"), out CameraMode) || !Enum.IsDefined(typeof(CinematicCameraMode), CameraMode)) && Action == CinematicAction.Camera) throw Error(e, "Unknown camera mode");
            if (Action == CinematicAction.Flight) {
                var route = new List<CinematicWaypoint>();
                foreach (XElement child in e.Elements()) {
                    if (child.Name.Namespace != XNamespace.None || child.Name.LocalName != "Waypoint") throw Error(child, "A flight contains only Waypoint elements");
                    route.Add(new CinematicWaypoint(child));
                }
                if (route.Count == 0 || route.Count > CinematicPlan.MaximumWaypoints) throw Error(e, "A flight needs 1.." + CinematicPlan.MaximumWaypoints + " waypoints");
                for (int i = 0; i < route.Count - 1; i++) if (route[i].Mode == CinematicFlightMode.Land) throw Error(e, "Only the last waypoint of a route can land");
                Route = route.ToArray();
            } else {
                if (e.HasElements) throw Error(e, "Cue actions cannot contain nested XML");
                Route = Array.Empty<CinematicWaypoint>();
            }
            Dependencies = Text(e, "after", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        internal static float Number(XElement e, string name, float fallback, float min, float max) {
            XAttribute a = e.Attribute(name);
            if (a == null) return fallback;
            if (!float.TryParse(a.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value) || value < min || value > max)
                throw Error(e, name + " must be finite in [" + min.ToString(CultureInfo.InvariantCulture) + ", " + max.ToString(CultureInfo.InvariantCulture) + "]");
            return value;
        }
        internal static string Text(XElement e, string name, string fallback) => e.Attribute(name)?.Value ?? fallback;
        internal static int Integer(XElement e, string name, int fallback, int min, int max) {
            float value = Number(e, name, fallback, min, max);
            if (value != MathF.Truncate(value)) throw Error(e, "Expected an integer: " + name);
            return (int)value;
        }
        internal static bool Boolean(XElement e, string name, bool fallback) {
            XAttribute a = e.Attribute(name);
            if (a == null) return fallback;
            if (bool.TryParse(a.Value, out bool value)) return value;
            throw Error(e, "Invalid boolean " + name);
        }
        private static int Binding(XElement e, string name, Dictionary<string, int> bindings) {
            string id = Text(e, name, "");
            if (id.Length == 0) return -1;
            if (bindings.TryGetValue(id, out int index)) return index;
            throw Error(e, "Missing binding " + id);
        }
        internal static InvalidDataException Error(XElement e, string message) {
            var line = (IXmlLineInfo)e;
            return new InvalidDataException(message + (line.HasLineInfo() ? " at line " + line.LineNumber : ""));
        }
    }

    public readonly struct CinematicTransition {
        public readonly string Condition;
        public readonly int Stage;
        public readonly bool Negate;
        internal CinematicTransition(string condition, int stage) {
            Negate = condition.StartsWith("!", StringComparison.Ordinal);
            Condition = Negate ? condition.Substring(1) : condition; Stage = stage;
        }
    }

    public sealed class CinematicStage {
        public readonly string Id;
        public readonly double Minimum, Maximum;
        public readonly int Timeout;
        internal readonly CinematicCue[] Cues;
        internal readonly CinematicTransition[] Transitions;
        internal readonly ulong[] Dependencies;
        public int CueCount => Cues.Length;
        public CinematicCue CueAt(int index) => Cues[index];
        public int TransitionCount => Transitions.Length;
        public CinematicTransition TransitionAt(int index) => Transitions[index];
        internal CinematicStage(string id, double minimum, double maximum, int timeout, CinematicCue[] cues, CinematicTransition[] transitions, ulong[] dependencies) {
            Id = id; Minimum = minimum; Maximum = maximum; Timeout = timeout; Cues = cues; Transitions = transitions; Dependencies = dependencies;
        }
    }

    public sealed class CinematicPassengers {
        public readonly int Vehicle;
        public readonly int[] Seats;
        internal CinematicPassengers(int vehicle, int[] seats) { Vehicle = vehicle; Seats = seats; }
    }

    public sealed class CinematicPlan {
        public const int MaximumTitle = 200, MaximumBindings = 32, MaximumStages = 64, MaximumCues = 64, MaximumWaypoints = 32, Version = 2;
        public readonly string Id;
        public readonly ulong Hash;
        public readonly double MaximumSeconds;
        public readonly CinematicPassengers Passengers;
        internal readonly CinematicBinding[] Bindings;
        internal readonly CinematicStage[] Stages;
        private readonly HashSet<string> signals;
        public int BindingCount => Bindings.Length;
        public int StageCount => Stages.Length;
        public CinematicBinding BindingAt(int index) => Bindings[index];
        public CinematicStage StageAt(int index) => Stages[index];
        public int BindingIndex(string id) => BindingIndex(id.AsSpan());
        public int BindingIndex(ReadOnlySpan<char> id) {
            for (int i = 0; i < Bindings.Length; i++) if (id.SequenceEqual(Bindings[i].Id.AsSpan())) return i;
            return -1;
        }
        private CinematicPlan(string id, ulong hash, double maximumSeconds, CinematicBinding[] bindings, CinematicStage[] stages, HashSet<string> signals, CinematicPassengers passengers) {
            Id = id; Hash = hash; MaximumSeconds = maximumSeconds; Bindings = bindings; Stages = stages; this.signals = signals; Passengers = passengers;
        }
        public bool AcceptsSignal(string signal) => signal != null && signals.Contains(signal);
        public static string WaypointSignal(string binding, string waypoint) => "flight." + binding + "." + waypoint;
        public CinematicCue Shot(int stageIndex, double age, ulong authoritative) {
            CinematicStage stage = Stages[stageIndex];
            ulong fired = VisualMask(stageIndex, age, authoritative);
            CinematicCue shot = null;
            for (int i = 0; i < stage.Cues.Length; i++) if (stage.Cues[i].Action == CinematicAction.Camera && (fired & (1UL << i)) != 0 && stage.Cues[i].At <= age) shot = stage.Cues[i];
            return shot;
        }
        public ulong VisualMask(int stageIndex, double age, ulong authoritative) {
            CinematicStage stage = Stages[stageIndex];
            ulong fired = authoritative;
            for (int i = 0; i < stage.Cues.Length; i++) if (stage.Cues[i].At <= age && (fired & stage.Dependencies[i]) == stage.Dependencies[i]) fired |= 1UL << i;
            return fired;
        }
        public static CinematicPlan Load(string path) {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 524288 };
            using XmlReader reader = XmlReader.Create(path, settings);
            return Parse(XDocument.Load(reader, LoadOptions.SetLineInfo));
        }
        public static CinematicPlan Parse(XDocument document) {
            XElement root = document.Root ?? throw new InvalidDataException("Missing cinematic root");
            if (root.Name != "Cinematic" || CinematicCue.Text(root, "version", "") != Version.ToString(CultureInfo.InvariantCulture)) throw CinematicCue.Error(root, "Expected Cinematic version=" + Version);
            Shape(root, "id version maxSeconds", "Bindings Passengers Sequence Stage Events");
            int bindingBlocks = 0, eventBlocks = 0, passengerBlocks = 0;
            foreach (XElement child in root.Elements()) {
                if (child.Name == "Bindings") { if (++bindingBlocks > 1) throw CinematicCue.Error(child, "Duplicate Bindings block"); Shape(child, "", "Bind"); }
                if (child.Name == "Events") { if (++eventBlocks > 1) throw CinematicCue.Error(child, "Duplicate Events block"); Shape(child, "", "Event"); }
                if (child.Name == "Passengers") { if (++passengerBlocks > 1) throw CinematicCue.Error(child, "Duplicate Passengers block"); Shape(child, "vehicle seats", ""); }
            }
            string id = RequireId(root);
            var bindingIds = new Dictionary<string, int>(StringComparer.Ordinal);
            var bindingNodes = new List<XElement>(root.Element("Bindings")?.Elements("Bind") ?? Array.Empty<XElement>());
            if (bindingNodes.Count > MaximumBindings) throw CinematicCue.Error(root, "Too many bindings");
            for (int i = 0; i < bindingNodes.Count; i++) if (!bindingIds.TryAdd(RequireId(bindingNodes[i]), i)) throw CinematicCue.Error(bindingNodes[i], "Duplicate binding");
            var bindings = new CinematicBinding[bindingNodes.Count];
            for (int i = 0; i < bindings.Length; i++) {
                XElement e = bindingNodes[i];
                Shape(e, "id kind required seat vehicle", "");
                if (!Enum.TryParse(CinematicCue.Text(e, "kind", ""), out CinematicBindingKind kind) || !Enum.IsDefined(typeof(CinematicBindingKind), kind)) throw CinematicCue.Error(e, "Invalid binding kind");
                string vehicle = CinematicCue.Text(e, "vehicle", ""); int vehicleIndex = -1;
                if (vehicle.Length != 0 && !bindingIds.TryGetValue(vehicle, out vehicleIndex)) throw CinematicCue.Error(e, "Missing vehicle binding");
                int seat = CinematicCue.Integer(e, "seat", -1, -1, 15);
                bindings[i] = new CinematicBinding(RequireId(e), kind, CinematicCue.Boolean(e, "required", true), seat, vehicleIndex);
            }
            for (int i = 0; i < bindings.Length; i++) {
                CinematicBinding b = bindings[i];
                if (b.Seat >= 0 && (b.Kind != CinematicBindingKind.Actor || b.Vehicle < 0 || bindings[b.Vehicle].Kind != CinematicBindingKind.Rotorcraft)) throw CinematicCue.Error(bindingNodes[i], "A seat actor needs a rotorcraft binding");
            }
            CinematicPassengers passengers = null;
            if (root.Element("Passengers") is XElement passengerNode) {
                if (!bindingIds.TryGetValue(CinematicCue.Text(passengerNode, "vehicle", ""), out int vehicle) || bindings[vehicle].Kind != CinematicBindingKind.Rotorcraft) throw CinematicCue.Error(passengerNode, "Passengers need a rotorcraft binding");
                string[] parts = CinematicCue.Text(passengerNode, "seats", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 0 || parts.Length > 16) throw CinematicCue.Error(passengerNode, "Passengers need 1..16 seats");
                var seats = new int[parts.Length];
                for (int i = 0; i < parts.Length; i++) {
                    if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out seats[i]) || seats[i] > 15) throw CinematicCue.Error(passengerNode, "Invalid passenger seat " + parts[i]);
                    for (int j = 0; j < i; j++) if (seats[j] == seats[i]) throw CinematicCue.Error(passengerNode, "Duplicate passenger seat " + parts[i]);
                }
                passengers = new CinematicPassengers(vehicle, seats);
            }
            var sequenceNodes = new Dictionary<string, XElement>(StringComparer.Ordinal);
            foreach (XElement e in root.Elements("Sequence")) { Shape(e, "id", null); if (!sequenceNodes.TryAdd(RequireId(e), e)) throw CinematicCue.Error(e, "Duplicate sequence"); }
            var stageNodes = new List<XElement>(root.Elements("Stage"));
            if (stageNodes.Count == 0 || stageNodes.Count > MaximumStages) throw CinematicCue.Error(root, "Expected 1..64 stages");
            var stageIds = new Dictionary<string, int>(StringComparer.Ordinal) { ["end"] = -1, ["abort"] = -2 };
            for (int i = 0; i < stageNodes.Count; i++) if (!stageIds.TryAdd(RequireId(stageNodes[i]), i)) throw CinematicCue.Error(stageNodes[i], "Duplicate or reserved stage id");
            var stages = new CinematicStage[stageNodes.Count];
            var signals = new HashSet<string>(StringComparer.Ordinal) { "always", "flight.ready", "flight.arrived", "flight.landed", "flight.unsafe", "flight.contact", "actors.done", "despawn.done" };
            var external = new HashSet<string>(StringComparer.Ordinal);
            foreach (CinematicBinding binding in bindings) if (binding.Kind == CinematicBindingKind.Rotorcraft) foreach (string state in new[] { "ready", "arrived", "landed", "unsafe", "contact" }) signals.Add("flight." + binding.Id + "." + state);
            foreach (XElement e in root.Element("Events")?.Elements("Event") ?? Array.Empty<XElement>()) {
                Shape(e, "id", "");
                string key = RequireId(e);
                if (Reserved(key) || !external.Add(key) || external.Count > 128) throw CinematicCue.Error(e, "Duplicate, reserved or excessive external event");
                signals.Add(key);
            }
            for (int i = 0; i < stages.Length; i++) {
                XElement e = stageNodes[i];
                Shape(e, "id min max timeout", null);
                double maximum = CinematicCue.Number(e, "max", 60, .01f, 3600), minimum = CinematicCue.Number(e, "min", 0, 0, (float)maximum);
                int timeout = ResolveStage(e, "timeout", "abort", stageIds);
                var cues = new List<CinematicCue>();
                Expand(e, 0, 0, sequenceNodes, bindingIds, new HashSet<string>(StringComparer.Ordinal), cues);
                cues.Sort((a, b) => { int time = a.At.CompareTo(b.At); return time != 0 ? time : a.SourceOrder.CompareTo(b.SourceOrder); });
                if (cues.Count > MaximumCues) throw CinematicCue.Error(e, "Too many cues after sequence expansion");
                var cueIds = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int c = 0; c < cues.Count; c++) {
                    CinematicCue cue = cues[c];
                    if (cue.Id.Length > 0 && !cueIds.TryAdd(cue.Id, c)) throw CinematicCue.Error(e, "Duplicate cue id " + cue.Id);
                    if (cue.At >= maximum) throw CinematicCue.Error(e, "Cue lies outside stage " + cue.Id);
                    ValidateCue(e, cue, bindings);
                    if (cue.Action == CinematicAction.Signal) signals.Add(cue.Value);
                    if (cue.Action == CinematicAction.Flight) {
                        var waypointIds = new HashSet<string>(StringComparer.Ordinal);
                        foreach (CinematicWaypoint point in cue.Route) {
                            if (point.Id.Length == 0) continue;
                            if (!waypointIds.Add(point.Id)) throw CinematicCue.Error(e, "Duplicate waypoint " + point.Id);
                            string key = WaypointSignal(bindings[cue.Target].Id, point.Id);
                            if (key.Length > 128) throw CinematicCue.Error(e, "Waypoint signal is too long: " + key);
                            signals.Add(key);
                        }
                    }
                    for (int p = 0; p < c; p++) if (Conflicts(cue, cues[p])) throw CinematicCue.Error(e, "Conflicting cues " + cue.Id + " / " + cues[p].Id);
                    if (cue.Action == CinematicAction.Sound) {
                        int simultaneous = 1;
                        for (int p = 0; p < c; p++) if (cues[p].Action == CinematicAction.Sound && cues[p].At + cues[p].Duration > cue.At) simultaneous++;
                        if (simultaneous > 8) throw CinematicCue.Error(e, "Cinematic sound channel budget exceeds 8");
                    }
                }
                var dependencies = new ulong[cues.Count];
                for (int c = 0; c < cues.Count; c++) foreach (string dependency in cues[c].Dependencies) {
                    if (!cueIds.TryGetValue(dependency, out int previous) || previous >= c) throw CinematicCue.Error(e, "Cue dependency must refer to an earlier cue: " + dependency);
                    dependencies[c] |= 1UL << previous;
                }
                var transitions = new List<CinematicTransition>();
                foreach (XElement t in e.Elements("Transition")) { Shape(t, "when to", ""); transitions.Add(new CinematicTransition(CinematicCue.Text(t, "when", "always"), ResolveStage(t, "to", "", stageIds))); }
                if (transitions.Count == 0 && timeout == -2) throw CinematicCue.Error(e, "Stage has no completion transition");
                stages[i] = new CinematicStage(RequireId(e), minimum, maximum, timeout, cues.ToArray(), transitions.ToArray(), dependencies);
            }
            for (int i = 0; i < stages.Length; i++) foreach (CinematicCue cue in stages[i].Cues)
                if (cue.Action == CinematicAction.Despawn && (!signals.Contains(cue.Value) || !cue.Value.StartsWith("flight." + bindings[cue.Target].Id + ".", StringComparison.Ordinal)))
                    throw CinematicCue.Error(stageNodes[i], "Despawn requires a measured flight condition for its own rotorcraft");
            for (int i = 0; i < stages.Length; i++) foreach (CinematicTransition transition in stages[i].Transitions) {
                string condition = transition.Condition;
                if (!signals.Contains(condition)) throw CinematicCue.Error(stageNodes[i], "Undeclared condition " + condition);
            }
            var reached = new bool[stages.Length];
            Visit(0, stages, reached);
            for (int i = 0; i < reached.Length; i++) if (!reached[i]) throw CinematicCue.Error(stageNodes[i], "Unreachable stage");
            ulong hash = 14695981039346656037UL;
            foreach (char c in root.ToString(SaveOptions.DisableFormatting)) { hash ^= c; hash *= 1099511628211UL; }
            return new CinematicPlan(id, hash, CinematicCue.Number(root, "maxSeconds", 600, .1f, 3600), bindings, stages, external, passengers);
        }
        private static bool Reserved(string key) => key == "always" || key == "actors.done" || key.StartsWith("despawn.", StringComparison.Ordinal) || key.StartsWith("flight.", StringComparison.Ordinal);
        private static void Shape(XElement e, string attributes, string children) {
            var allowed = new HashSet<string>(attributes.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
            foreach (XAttribute a in e.Attributes()) if (a.Name.Namespace != XNamespace.None || !allowed.Contains(a.Name.LocalName)) throw CinematicCue.Error(e, "Unknown attribute " + a.Name);
            if (children == null) return;
            allowed.Clear(); foreach (string child in children.Split(' ', StringSplitOptions.RemoveEmptyEntries)) allowed.Add(child);
            foreach (XElement child in e.Elements()) if (child.Name.Namespace != XNamespace.None || !allowed.Contains(child.Name.LocalName)) throw CinematicCue.Error(child, "Unknown child element " + child.Name);
        }
        private static void Visit(int index, CinematicStage[] stages, bool[] reached) {
            if (index < 0 || reached[index]) return;
            reached[index] = true;
            Visit(stages[index].Timeout, stages, reached);
            foreach (CinematicTransition t in stages[index].Transitions) Visit(t.Stage, stages, reached);
        }
        private static int ResolveStage(XElement e, string attribute, string fallback, Dictionary<string, int> stages) =>
            stages.TryGetValue(CinematicCue.Text(e, attribute, fallback), out int index) ? index : throw CinematicCue.Error(e, "Missing stage " + CinematicCue.Text(e, attribute, fallback));
        private static string RequireId(XElement e) {
            string id = CinematicCue.Text(e, "id", "");
            ValidateId(e, id);
            return id;
        }
        internal static void ValidateId(XElement e, string id) {
            if (!IsId(id)) throw CinematicCue.Error(e, "Expected an id of 1..64 characters from letters, digits, '_', '.' and '-': " + id);
        }
        public static bool IsId(string id) {
            if (string.IsNullOrEmpty(id) || id.Length > 64) return false;
            foreach (char c in id) if (!char.IsAsciiLetterOrDigit(c) && c != '_' && c != '.' && c != '-') return false;
            return true;
        }
        private static void Expand(XElement parent, double offset, int depth, Dictionary<string, XElement> sequences, Dictionary<string, int> bindings, HashSet<string> stack, List<CinematicCue> cues) {
            if (depth > 16) throw CinematicCue.Error(parent, "Sequence nesting exceeds 16");
            foreach (XElement e in parent.Elements()) {
                if (e.Name == "Transition") continue;
                if (e.Name == "Insert") {
                    Shape(e, "sequence at", "");
                    string id = CinematicCue.Text(e, "sequence", "");
                    if (!sequences.TryGetValue(id, out XElement sequence) || !stack.Add(id)) throw CinematicCue.Error(e, "Missing or cyclic sequence " + id);
                    Expand(sequence, offset + CinematicCue.Number(e, "at", 0, 0, 3600), depth + 1, sequences, bindings, stack, cues);
                    stack.Remove(id);
                } else cues.Add(new CinematicCue(e, bindings, offset, cues.Count));
                if (cues.Count > MaximumCues) throw CinematicCue.Error(parent, "Sequence cue budget exceeded");
            }
        }
        // A walk cued at or after the person's exit is not a conflict: the session holds it until the climb ends.
        private static bool QueuedBehind(CinematicCue walk, CinematicCue exit) => walk.Action == CinematicAction.Move && exit.Action == CinematicAction.Disembark && walk.Target == exit.Target && walk.At >= exit.At;
        private static bool Conflicts(CinematicCue a, CinematicCue b) {
            if (QueuedBehind(a, b) || QueuedBehind(b, a)) return false;
            bool actorA = a.Action is CinematicAction.Move or CinematicAction.Board or CinematicAction.Disembark;
            bool actorB = b.Action is CinematicAction.Move or CinematicAction.Board or CinematicAction.Disembark;
            bool same = a.Action == CinematicAction.Camera && b.Action == CinematicAction.Camera || a.Target == b.Target && (actorA && actorB || a.Action == b.Action && a.Action is CinematicAction.Flight or CinematicAction.Light or CinematicAction.Pose);
            return same && (Math.Abs(a.At - b.At) < 1e-7 || actorA && actorB && a.At < b.At + b.Duration && b.At < a.At + a.Duration);
        }
        private static void ValidateCue(XElement e, CinematicCue cue, CinematicBinding[] bindings) {
            if (cue.Action == CinematicAction.Signal) {
                if (!IsId(cue.Value)) throw CinematicCue.Error(e, "Signal needs an id of 1..64 characters");
                if (Reserved(cue.Value)) throw CinematicCue.Error(e, "A marker cannot override a measured world condition");
                return;
            }
            if (cue.Action is CinematicAction.Letterbox or CinematicAction.Fade or CinematicAction.Title) {
                if (cue.Action == CinematicAction.Title && (cue.Value.Trim().Length == 0 || cue.Value.Length > MaximumTitle)) throw CinematicCue.Error(e, "A title needs text of 1.." + MaximumTitle + " characters");
                if (cue.Action == CinematicAction.Title && (cue.Duration <= 0 || cue.Zoom > 2 || cue.Blend * 2 > cue.Duration)) throw CinematicCue.Error(e, "A title needs positive duration, scale up to 2 and fades that fit twice into it");
                if (cue.Action == CinematicAction.Letterbox && (cue.Intensity < 1 || cue.Intensity > 4)) throw CinematicCue.Error(e, "Letterbox aspect ratio must lie in [1, 4]");
                if (cue.Action == CinematicAction.Fade && cue.Intensity > 1) throw CinematicCue.Error(e, "Fade opacity must lie in [0, 1]");
                return;
            }
            if (cue.Action == CinematicAction.Camera) {
                if (cue.Target < 0 && cue.RelativeTo < 0) throw CinematicCue.Error(e, "A camera needs a subject or an anchor");
                if (cue.CameraMode == CinematicCameraMode.Follow && cue.Target < 0) throw CinematicCue.Error(e, "A following camera needs a subject");
                return;
            }
            if (cue.Target < 0) throw CinematicCue.Error(e, "Action requires target: " + cue.Action);
            CinematicBindingKind kind = bindings[cue.Target].Kind;
            if (cue.Action is CinematicAction.Flight or CinematicAction.Engine or CinematicAction.Wash or CinematicAction.Despawn && kind != CinematicBindingKind.Rotorcraft) throw CinematicCue.Error(e, "Flight, engine and rotor wash actions require rotorcraft");
            if (cue.Action is CinematicAction.Move or CinematicAction.Face or CinematicAction.Pose or CinematicAction.Board or CinematicAction.Disembark or CinematicAction.Interact && kind != CinematicBindingKind.Actor) throw CinematicCue.Error(e, "Actor action requires actor binding");
            if (cue.Action is CinematicAction.Board or CinematicAction.Disembark && (cue.RelativeTo < 0 || bindings[cue.RelativeTo].Kind != CinematicBindingKind.Rotorcraft)) throw CinematicCue.Error(e, "Boarding requires relativeTo rotorcraft");
            if (cue.Action is CinematicAction.Light && kind != CinematicBindingKind.Item) throw CinematicCue.Error(e, "Light action requires item binding");
            if (cue.Action == CinematicAction.Despawn && (cue.Duration <= 0 || cue.Duration > 30 || cue.Value.Length == 0)) throw CinematicCue.Error(e, "Despawn needs a flight condition and fade duration in (0, 30]");
            if (cue.Action == CinematicAction.Pose && cue.Value != "Stand" && cue.Value != "Crouch" && cue.Value != "Aim") throw CinematicCue.Error(e, "Unsupported actor pose");
            if (cue.Action == CinematicAction.Sound && cue.Value.Length != 0 && cue.Value != "loop") throw CinematicCue.Error(e, "Sound value must be empty or loop");
            if (cue.Action is CinematicAction.Sound or CinematicAction.Particle or CinematicAction.Wash && (cue.Resource.Length == 0 || Path.IsPathRooted(cue.Resource) || cue.Resource.Contains("..", StringComparison.Ordinal))) throw CinematicCue.Error(e, "Invalid effect resource");
            if (cue.Action is CinematicAction.Move or CinematicAction.Particle or CinematicAction.Light or CinematicAction.Sound or CinematicAction.Wash && cue.Duration <= 0) throw CinematicCue.Error(e, "Action requires positive duration");
        }
    }
}
