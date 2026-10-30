using System;
using System.Collections.Generic;
using System.IO;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;
using ScpItdMod.Core;

namespace ScpItdMod.Cinematics {
    internal sealed class CinematicBindingUnavailableException : InvalidOperationException {
        internal CinematicBindingUnavailableException(string message) : base(message) { }
    }
    internal sealed class CinematicBindings {
        internal readonly CinematicPlan Plan;
        internal readonly Entity[] Entities;
        internal readonly BovCinematicAdapter Bov;
        internal readonly float UnitsPerMetre;
        internal uint Removed { get; private set; }
        internal CinematicBindings(CinematicPlan plan, IReadOnlyDictionary<string, ushort> ids, bool resolveSeats = true, uint removed = 0, float unitsPerMetre = 100f) {
            Plan = plan; Removed = removed; Entities = new Entity[plan.BindingCount];
            foreach (var pair in ids) {
                bool found = false;
                for (int i = 0; i < plan.BindingCount; i++) if (plan.Bindings[i].Id == pair.Key) { found = true; break; }
                if (!found) throw new InvalidOperationException("Unknown cinematic binding " + pair.Key);
            }
            bool needsVehicles = false;
            for (int i = 0; i < Entities.Length; i++) if (plan.Bindings[i].Kind == CinematicBindingKind.Rotorcraft) needsVehicles = true;
            if (needsVehicles && !BovCinematicAdapter.TryCreate(out Bov, out string failure)) throw new InvalidOperationException(failure);
            UnitsPerMetre = unitsPerMetre;
            for (int i = 0; i < Entities.Length; i++) {
                CinematicBinding binding = plan.Bindings[i];
                if ((removed & (1u << i)) != 0) continue;
                if (ids.TryGetValue(binding.Id, out ushort id) && id != 0) Entities[i] = Entity.FindEntityByID(id);
                Entity entity = Entities[i];
                if (entity == null) continue;
                if (entity.Removed || binding.Kind == CinematicBindingKind.Actor && entity is not Character || binding.Kind != CinematicBindingKind.Actor && entity is not Item)
                    throw new InvalidOperationException("Binding has the wrong entity type: " + binding.Id);
                if (binding.Kind == CinematicBindingKind.Rotorcraft) {
                    if (!Bov.Read((Item)entity, out CinematicFlightObservation observation)) throw new CinematicBindingUnavailableException("Binding is not a live BOVehicles rotorcraft: " + binding.Id);
                    if (observation.Critical) throw new InvalidOperationException(FormattableString.Invariant($"Rotorcraft has critical damage or unavailable flight control: {binding.Id} item={entity.ID} condition={((Item)entity).Condition:0.##}/{((Item)entity).MaxCondition:0.##} position={observation.Position} velocity={observation.Velocity} grounded={observation.Grounded} rotor={observation.RotorShare:0.###} ready={observation.Ready}"));
                    UnitsPerMetre = observation.Scale * 100f;
                }
            }
            for (int i = 0; i < Entities.Length; i++) {
                CinematicBinding binding = plan.Bindings[i];
                if ((removed & (1u << i)) != 0) continue;
                if (resolveSeats && Entities[i] == null && binding.Kind == CinematicBindingKind.Actor && binding.Seat >= 0 && Entities[binding.Vehicle] is Item vehicle)
                    Entities[i] = Bov.Occupant(vehicle, binding.Seat);
                if (Entities[i] == null && binding.Required) throw new CinematicBindingUnavailableException("Required binding is missing: " + binding.Id);
                if (Entities[i] == null) continue;
                for (int j = 0; j < i; j++) if (ReferenceEquals(Entities[i], Entities[j])) throw new InvalidOperationException("Bindings must name distinct entities: " + binding.Id);
            }
            for (int s = 0; s < plan.StageCount; s++) foreach (CinematicCue cue in plan.Stages[s].Cues) {
                if (cue.Target >= 0 && Entities[cue.Target] == null) continue;
                if (cue.Action == CinematicAction.Light && Entities[cue.Target] is Item light && light.GetComponent<LightComponent>() == null) throw new InvalidOperationException("Light component is missing: " + plan.Bindings[cue.Target].Id);
                if (cue.Action == CinematicAction.Sound && !File.Exists(ModAssets.Resolve(cue.Resource))) throw new FileNotFoundException("Cinematic sound is missing", cue.Resource);
                if (cue.Action == CinematicAction.Pose && cue.Value != "Stand" && cue.Value != "Crouch" && cue.Value != "Aim") throw new InvalidOperationException("Unsupported actor pose: " + cue.Value);
                if (cue.Action == CinematicAction.Interact && (cue.RelativeTo < 0 || Entities[cue.RelativeTo] is not Item)) throw new InvalidOperationException("Interaction requires an item in relativeTo");
            }
        }
        internal Vector2 Position(int index) => index < 0 ? Vector2.Zero : Entities[index] switch {
            Item item when !item.Removed => item.WorldPosition,
            Character actor when !actor.Removed => actor.WorldPosition,
            _ => Vector2.Zero
        };
        internal Vector2 Point(CinematicCue cue) => Position(cue.RelativeTo) + new Vector2(cue.Position.X, cue.Position.Y);
        internal Vec2 Anchor(int index) { Vector2 at = Position(index); return new Vec2(at.X, at.Y); }
        internal ushort Id(int index) => (Removed & (1u << index)) == 0 ? Entities[index]?.ID ?? 0 : (ushort)0;
        internal void Retire(uint mask) {
            Removed |= mask;
            for (int i = 0; i < Entities.Length; i++) if ((Removed & (1u << i)) != 0) Entities[i] = null;
        }
        internal bool Alive {
            get {
                for (int i = 0; i < Entities.Length; i++) if ((Removed & (1u << i)) == 0 && Plan.Bindings[i].Required && (Entities[i] == null || Entities[i].Removed)) return false;
                return true;
            }
        }
    }
}
