using System;
using System.Collections.Generic;
using ScpItdMod.Core;

namespace ScpItdMod.Cinematics {
    internal sealed class CinematicPreparation : IDisposable {
        internal readonly ulong Owner;
        private readonly CinematicVehicleDirector[] vehicles;
        internal CinematicPreparation(CinematicPlan plan, ulong owner, Func<int, ICinematicRotorcraft> resolve) {
            Owner = owner;
            vehicles = new CinematicVehicleDirector[plan.BindingCount];
            try {
                CinematicStage stage = plan.StageAt(0);
                var airframes = new ICinematicRotorcraft[plan.BindingCount];
                uint considered = 0;
                for (int i = 0; i < stage.CueCount; i++) {
                    CinematicCue cue = stage.CueAt(i);
                    if (cue.Action != CinematicAction.Engine || cue.Target < 0) continue;
                    uint bit = 1u << cue.Target;
                    if ((considered & bit) != 0) continue;
                    considered |= bit;
                    if (cue.At != 0 || !cue.Enabled || cue.Value != "warm" || stage.Dependencies[i] != 0) continue;
                    airframes[cue.Target] = resolve(cue.Target) ?? throw new InvalidOperationException("Preparation airframe is missing: " + plan.Bindings[cue.Target].Id);
                }
                for (int i = 0; i < airframes.Length; i++) {
                    if (airframes[i] == null) continue;
                    var director = new CinematicVehicleDirector(airframes[i], owner);
                    vehicles[i] = director;
                    if (director.Observation.Critical || !director.WarmStart(Vec2.Zero)) throw new InvalidOperationException("Cinematic airframe preparation rejected: " + plan.Bindings[i].Id);
                    director.Master = true;
                    if (!director.Hold()) throw new InvalidOperationException("Cinematic airframe hold rejected: " + plan.Bindings[i].Id);
                }
            } catch (Exception error) {
                try { Dispose(); }
                catch (Exception recovery) { throw new AggregateException("Cinematic preparation and recovery failed", error, recovery); }
                throw;
            }
        }
        internal bool Step() {
            for (int i = 0; i < vehicles.Length; i++) if (vehicles[i] != null && !vehicles[i].Hold()) return false;
            return true;
        }
        internal CinematicVehicleDirector Take(int binding) {
            CinematicVehicleDirector director = vehicles[binding];
            vehicles[binding] = null;
            return director;
        }
        public void Dispose() {
            List<Exception> failures = null;
            for (int i = vehicles.Length - 1; i >= 0; i--) {
                CinematicVehicleDirector director = Take(i);
                try { director?.Dispose(); }
                catch (Exception error) { (failures ??= new List<Exception>()).Add(error); }
            }
            if (failures != null) throw new AggregateException("Cinematic preparation recovery failed", failures);
        }
    }
}
