using System;
using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework;
using Barotrauma.Items.Components;
using ScpItdMod.Core;

namespace ScpItdMod.Cinematics {
    internal sealed class CinematicSession : ICinematicWorld, ICinematicRemovalWorld, IDisposable {
        internal readonly ulong Id;
        internal readonly CinematicBindings Bindings;
        internal readonly CinematicScenePlayer Player;
        internal readonly ushort[] Audience;
#if SERVER
        internal readonly ulong[] AudienceKeys;
#endif
        private readonly CinematicActorLease[] actors;
        private readonly Item[] traversals;
        private readonly CinematicCue[] deferredMoves, pendingExits;
        private readonly double[] pendingSince;
        private const double ExitPatience = 8;
        private double clock;
        private readonly CinematicLightLease[] lights;
        private bool disposed;
        internal CinematicSequencer Sequencer => Player.Sequencer;
        internal CinematicVehicleDirector[] Vehicles => Player.Vehicles;
        internal CinematicSession(ulong id, CinematicBindings bindings, ushort[] audience, ulong[] audienceKeys, CinematicPreparation preparation = null) {
            Id = id; Bindings = bindings; Audience = audience;
#if SERVER
            AudienceKeys = audienceKeys;
#endif
            int count = bindings.Entities.Length;
            var vehicles = new CinematicVehicleDirector[count];
            actors = new CinematicActorLease[count]; traversals = new Item[count]; deferredMoves = new CinematicCue[count]; pendingExits = new CinematicCue[count]; pendingSince = new double[count]; lights = new CinematicLightLease[count];
            Player = new CinematicScenePlayer(bindings.Plan, vehicles, this);
            try {
                for (int i = 0; i < count; i++) {
                    Entity entity = bindings.Entities[i];
                    if (entity is Item vehicle && bindings.Plan.Bindings[i].Kind == CinematicBindingKind.Rotorcraft)
                        vehicles[i] = preparation?.Take(i) ?? new CinematicVehicleDirector(new LiveRotorcraft(bindings.Bov, vehicle), id);
                    else if (entity is Character character) actors[i] = CinematicActors.Acquire(character, id);
                }
            } catch { Dispose(); throw; }
        }
        internal void Advance(float dt) {
            if (!Bindings.Alive) { Player.Fail("A required scene entity was removed"); return; }
            Player.Step(dt);
            if (Sequencer.Status == CinematicStatus.Completed) ReleasePeople();
        }
        public Vec2 Anchor(int binding) => Bindings.Anchor(binding);
        private const double TraceSeconds = 1;
        private double nextTrace;
        private string FlightReport(int binding) {
            CinematicVehicleDirector director = Vehicles[binding];
            if (director == null) return "";
            CinematicFlightController c = director.Controller;
            CinematicFlightObservation o = director.Observation;
            return $" | wp={c.Waypoint} err=({c.Error.X:0.00},{c.Error.Y:0.00}) m, command=({c.Last.Velocity.X:0.00},{c.Last.Velocity.Y:0.00}) m/s, vy={o.Velocity.Y:0.00} grounded={o.Grounded} unsafe={c.Unsafe}";
        }
        private bool CrewBusy() {
            for (int i = 0; i < actors.Length; i++) if (traversals[i] != null || pendingExits[i] != null || deferredMoves[i] != null) return true;
            return false;
        }
        public string Step(float dt) {
            clock += dt;
            if (clock >= nextTrace) {
                nextTrace = clock + TraceSeconds;
                for (int i = 0; i < Bindings.Entities.Length; i++)
                    if (Bindings.Plan.BindingAt(i).Kind == CinematicBindingKind.Rotorcraft && Bindings.Entities[i] is Item { Removed: false, body: { } airframe } craft)
                        ModLog.Message($"Cinematic {Id} t={clock:0.0} stage={Bindings.Plan.StageAt(Player.Sequencer.Stage).Id} {craft.Name}#{craft.ID} at ({craft.WorldPosition.X:0},{craft.WorldPosition.Y:0}) body speed {airframe.LinearVelocity.Length():0.00}{FlightReport(i)}{(CrewBusy() ? ActorReport() : "")}");
            }
            for (int i = 0; i < actors.Length; i++) {
                actors[i]?.Step(dt);
                if (pendingExits[i] != null && actors[i]?.Failed != true) {
                    CinematicCue cue = pendingExits[i];
                    Item vehicle = (Item)Bindings.Entities[cue.RelativeTo];
                    if (vehicle == null || vehicle.Removed) pendingExits[i] = null;
                    else if (Bindings.Bov.Actor(vehicle, actors[i].Character, cue.Seat, 1)) { pendingExits[i] = null; traversals[i] = vehicle; }
                    else if (clock - pendingSince[i] > ExitPatience) { pendingExits[i] = null; return RejectedReport(cue, vehicle); }
                }
                if (actors[i]?.Failed == true) return "Actor " + Bindings.Plan.Bindings[i].Id + ": " + actors[i].Failure;
                if (traversals[i] != null && (traversals[i].Removed || Bindings.Bov.Actor(traversals[i], actors[i].Character, 0, 2))) {
                    string ending = traversals[i].Removed ? "" : Bindings.Bov.Ending(actors[i].Character);
                    if (!string.IsNullOrEmpty(ending)) ModLog.Error("Cinematic " + Id + ": " + actors[i].Character.Name + "#" + actors[i].Character.ID + " door traversal ended early: " + ending);
                    traversals[i] = null;
                }
                if (deferredMoves[i] != null && traversals[i] == null && pendingExits[i] == null) { actors[i].Move(deferredMoves[i], LineTarget(deferredMoves[i]), Bindings.UnitsPerMetre); deferredMoves[i] = null; }
                if (lights[i] != null && (lights[i].Remaining -= dt) <= 0) { lights[i].Dispose(); lights[i] = null; }
            }
            return null;
        }
        // People who leave the same door and walk to the same line are placed by the order they come out: the first takes the far end, so that nobody crosses a person already standing.
        private readonly Dictionary<CinematicCue, Vector2> lineTargets = new Dictionary<CinematicCue, Vector2>();
        private int lineStage = -1;
        private Vector2 LineTarget(CinematicCue cue) {
            int stage = Sequencer.Stage;
            if (lineStage != stage) { lineStage = stage; lineTargets.Clear(); BuildLine(stage); }
            return lineTargets.TryGetValue(cue, out Vector2 placed) ? placed : Bindings.Point(cue);
        }
        private void BuildLine(int stage) {
            CinematicStage shot = Bindings.Plan.StageAt(stage);
            var moves = new List<CinematicCue>(); Item door = null;
            for (int i = 0; i < shot.CueCount; i++) {
                CinematicCue cue = shot.CueAt(i);
                if (cue.Action == CinematicAction.Move) moves.Add(cue);
                else if (cue.Action == CinematicAction.Disembark && cue.RelativeTo >= 0) door ??= Bindings.Entities[cue.RelativeTo] as Item;
            }
            if (moves.Count < 2 || door == null || door.Removed) return;
            moves.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.SourceOrder.CompareTo(b.SourceOrder));
            var slots = new float[moves.Count]; var order = new int[moves.Count];
            for (int i = 0; i < slots.Length; i++) slots[i] = Bindings.Point(moves[i]).X;
            CinematicFormation.Assign(slots, door.WorldPosition.X, order);
            for (int n = 0; n < moves.Count; n++) {
                Vector2 own = Bindings.Point(moves[n]);
                lineTargets[moves[n]] = new Vector2(slots[order[n]], own.Y);
            }
        }
        public bool Execute(CinematicCue cue) {
            if (cue.Target >= 0 && Bindings.Entities[cue.Target]?.Removed != false) return true;
            switch (cue.Action) {
                case CinematicAction.Move:
                    if (traversals[cue.Target] != null || pendingExits[cue.Target] != null) { deferredMoves[cue.Target] = cue; return true; }
                    actors[cue.Target].Move(cue, LineTarget(cue), Bindings.UnitsPerMetre); return true;
                case CinematicAction.Face: actors[cue.Target].Face(Bindings.Point(cue)); return true;
                case CinematicAction.Pose: return actors[cue.Target].Pose(cue.Value);
                case CinematicAction.Interact: return ((Item)Bindings.Entities[cue.RelativeTo]).TryInteract(actors[cue.Target].Character, forceSelectKey: true, forceUseKey: true);
                case CinematicAction.Board:
                case CinematicAction.Disembark:
                    Item vehicle = (Item)Bindings.Entities[cue.RelativeTo];
                    if (cue.Action == CinematicAction.Disembark) {
                        if (Bindings.Bov.Actor(vehicle, actors[cue.Target].Character, cue.Seat, 1)) traversals[cue.Target] = vehicle;
                        else { pendingExits[cue.Target] = cue; pendingSince[cue.Target] = clock; }
                        return true;
                    }
                    if (!Bindings.Bov.Actor(vehicle, actors[cue.Target].Character, cue.Seat, 0)) { ModLog.Error(RejectedReport(cue, vehicle)); return false; }
                    traversals[cue.Target] = vehicle; return true;
                case CinematicAction.Light:
                    lights[cue.Target] ??= new CinematicLightLease(((Item)Bindings.Entities[cue.Target]).GetComponent<LightComponent>());
                    lights[cue.Target].Apply(cue); return true;
                case CinematicAction.Signal: CinematicHost.Publish(Id, cue.Value); return true;
                case CinematicAction.Camera:
                case CinematicAction.Particle:
                case CinematicAction.Wash:
                case CinematicAction.Letterbox:
                case CinematicAction.Fade:
                case CinematicAction.Title:
                case CinematicAction.Sound: return true;
                default: return false;
            }
        }
        // Why the helicopter refused a person: not in the seat any more, too far from it, or the wheels not carrying weight yet.
        private string RejectedReport(CinematicCue cue, Item vehicle) {
            Character c = actors[cue.Target].Character;
            bool known = Bindings.Bov.Read(vehicle, out CinematicFlightObservation o);
            return $"Cinematic {Id}: {cue.Action} of {c.Name}#{c.ID} was refused: seated={(Bindings.Bov.Occupant(vehicle, cue.Seat) == c)} distance={(c.WorldPosition - vehicle.WorldPosition).Length():0} units, character at ({c.WorldPosition.X:0},{c.WorldPosition.Y:0}) helicopter at ({vehicle.WorldPosition.X:0},{vehicle.WorldPosition.Y:0}) canInteract={c.CanInteract} stun={c.Stun:0.0} grounded={(known ? o.Grounded.ToString() : "?")} gearReady={(known ? o.GearReady.ToString() : "?")} reason=\"{Bindings.Bov.Refusal(c)}\"";
        }
        public bool Test(string condition) {
            if (condition != "actors.done") return false;
            for (int i = 0; i < actors.Length; i++) if (actors[i] != null && (!actors[i].Done || traversals[i] != null || deferredMoves[i] != null || pendingExits[i] != null)) return false;
            return true;
        }
        public void EnterStage(int stage) {
            for (int i = 0; i < lights.Length; i++) if (lights[i] != null) { lights[i].Dispose(); lights[i] = null; }
            ModLog.Message("Cinematic " + Id + " stage=" + Bindings.Plan.StageAt(stage).Id + ActorReport());
        }
        // One log line while the scene is over and the helicopter is still flying off or waiting to be removed, so a helicopter that stays can be explained.
        internal string DepartureReport() {
            string text = "Cinematic " + Id + " departure:";
            for (int i = 0; i < Vehicles.Length; i++) {
                CinematicVehicleDirector v = Vehicles[i];
                if (v == null || Bindings.Entities[i] is not Item { Removed: false } item) continue;
                text += $" {item.Prefab.Identifier}#{item.ID} at ({item.WorldPosition.X:0},{item.WorldPosition.Y:0}) waypoint {v.Controller.Goal?.Id}#{v.Controller.Waypoint} speed {v.Observation.Velocity.Length():0.0}";
                if (v.Controller.Unsafe) text += " UNSAFE " + v.Controller.Failure;
#if SERVER
                text += " removal waits for: [" + CinematicServer.RemovalBlockers(new Vec2(item.WorldPosition.X, item.WorldPosition.Y), v.VisibilityRadius * Bindings.UnitsPerMetre, 0) + "]";
#endif
            }
            return text;
        }
        public bool CanRemove(int binding, float radiusMetres, float distanceMetres) {
            if (!CinematicHost.Authority || Entity.Spawner == null || Bindings.Entities[binding] is not Item { Removed: false } vehicle) return false;
            Vec2 at = new Vec2(vehicle.WorldPosition.X, vehicle.WorldPosition.Y);
            float units = Bindings.UnitsPerMetre;
#if SERVER
            return CinematicServer.ClearForRemoval(at, radiusMetres * units, distanceMetres * units);
#else
            Camera camera = GameMain.GameScreen?.Cam;
            if (camera == null) return false;
            float halfDiagonal = camera.Resolution.ToVector2().Length() / (2f * camera.Zoom);
            var viewport = new CinematicViewport(new Vec2(camera.Position.X, camera.Position.Y), new Vec2(halfDiagonal, halfDiagonal));
            if (!viewport.Clear(at, radiusMetres * units, distanceMetres * units)) return false;
            Character observer = Character.Controlled;
            return observer == null || observer.Removed || (at - new Vec2(observer.WorldPosition.X, observer.WorldPosition.Y)).LengthSquared() > distanceMetres * distanceMetres * units * units;
#endif
        }
        public void Remove(int binding) {
            if (!CinematicHost.Authority) throw new InvalidOperationException("Only authority can remove cinematic entities");
            if ((Bindings.Removed & (1u << binding)) != 0) return;
            Item vehicle = (Item)Bindings.Entities[binding];
            CinematicHost.Removals.Record(vehicle.ID);
            Bindings.Retire(1u << binding);
            Entity.Spawner.AddEntityToRemoveQueue(vehicle);
        }
        // Where every leased person is and in what state, for the log: a crew that cannot be seen, or is far from the pad, is explained by these lines.
        internal string ActorReport() {
            string text = "";
            for (int i = 0; i < actors.Length; i++) {
                Character c = actors[i]?.Character;
                if (c == null) continue;
                CinematicBinding binding = Bindings.Plan.BindingAt(i);
                string seatState = "";
                if (binding.Seat >= 0 && binding.Vehicle >= 0 && Bindings.Entities[binding.Vehicle] is Item vehicle) {
                    Character occupant = Bindings.Bov.Occupant(vehicle, binding.Seat);
                    seatState = $" seat={binding.Seat} seatOwner={(occupant == null ? "empty" : occupant.ID.ToString())}";
                }
                text += $"{Environment.NewLine}  actor {binding.Id} {c.Name}#{c.ID} at ({c.WorldPosition.X:0},{c.WorldPosition.Y:0}){seatState} enabled={c.Enabled} ragdolled={c.IsRagdolled} stun={c.Stun:0.0} sub={c.Submarine?.Info?.Name ?? "-"} hull={(c.CurrentHull != null ? "yes" : "no")} traversal={(traversals[i] != null ? "yes" : "no")} done={actors[i].Done}";
            }
            return text;
        }
        private bool peopleReleased;
        private void ReleasePeople() {
            if (peopleReleased) return;
            peopleReleased = true;
            ModLog.Message("Cinematic " + Id + " releases the people" + ActorReport());
            for (int i = actors.Length - 1; i >= 0; i--) {
                if (traversals[i] is { Removed: false } vehicle && actors[i] != null) Bindings.Bov.Actor(vehicle, actors[i].Character, 0, 3);
                traversals[i] = null; deferredMoves[i] = null;
                actors[i]?.Dispose(); actors[i] = null;
                lights[i]?.Dispose(); lights[i] = null;
            }
        }
        public void Dispose() {
            if (disposed) return;
            disposed = true;
            for (int i = actors.Length - 1; i >= 0; i--) {
                try { lights[i]?.Dispose(); }
                catch (Exception error) { ModLog.Error("Cinematic light recovery: " + error.Message); }
                try { if (traversals[i] is { Removed: false } vehicle && actors[i] != null) Bindings.Bov.Actor(vehicle, actors[i].Character, 0, 3); }
                catch (Exception error) { ModLog.Error("Cinematic traversal recovery: " + error.Message); }
                try { actors[i]?.Dispose(); }
                catch (Exception error) { ModLog.Error("Cinematic actor recovery: " + error.Message); }
                try {
                    if (Sequencer.Status == CinematicStatus.Completed) Vehicles[i]?.Complete();
                    Vehicles[i]?.Dispose();
                } catch (Exception error) { ModLog.Error("Cinematic vehicle recovery: " + error.Message); }
            }
        }
    }
}
