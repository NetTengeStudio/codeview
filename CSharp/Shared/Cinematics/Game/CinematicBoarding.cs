using System;
using System.Collections.Generic;
using Barotrauma;
using ScpItdMod.Core;

namespace ScpItdMod.Cinematics {
    internal sealed class CinematicBoarding : IDisposable {
        private readonly List<Character> boarded = new List<Character>();
        private readonly BovCinematicAdapter bov;
        private readonly Item vehicle;
        private bool committed, disposed;

        internal CinematicBoarding(CinematicPlan plan, IReadOnlyDictionary<string, ushort> bindings) {
            if (!bindings.TryGetValue(plan.BindingAt(plan.Passengers.Vehicle).Id, out ushort vehicleId) || Entity.FindEntityByID(vehicleId) is not Item found || found.Removed)
                throw new CinematicBindingUnavailableException("Passenger vehicle is missing");
            vehicle = found;
            if (!BovCinematicAdapter.TryCreate(out bov, out string failure)) throw new InvalidOperationException(failure);
            try {
                int[] seats = plan.Passengers.Seats;
                int seat = 0;
                foreach (Character character in CinematicSubScenes.PassengerCandidates()) {
                    if (!CinematicActors.Available(character)) continue;
                    while (seat < seats.Length && bov.Occupant(vehicle, seats[seat]) != null) seat++;
                    if (seat >= seats.Length) break;
#if SERVER
                    CinematicActors.Detach(character);
#endif
                    int seatIndex = seats[seat];
                    bool accepted = bov.Actor(vehicle, character, seatIndex, 4);
                    bool occupiedByActor = ReferenceEquals(bov.Occupant(vehicle, seatIndex), character);
                    if (accepted && occupiedByActor) {
                        boarded.Add(character);
                        ModLog.Message("Cinematic " + plan.Id + ": " + character.Name + " confirmed in passenger seat " + seatIndex);
                        seat++;
                    }
                    else {
                        ModLog.Error("Cinematic " + plan.Id + ": passenger seat " + seatIndex + " rejected " + character.Name +
                            " (accepted=" + accepted + ", occupant=" + (bov.Occupant(vehicle, seatIndex)?.ID.ToString() ?? "empty") + ")");
#if SERVER
                        CinematicActors.ReturnDetached(character);
#endif
                    }
                }
            } catch { Dispose(); throw; }
        }

        internal void Commit() => committed = true;

        public void Dispose() {
            if (disposed) return;
            disposed = true;
            try {
                if (!committed && !vehicle.Removed) foreach (Character character in boarded) {
                    try { if (!character.Removed && !bov.Actor(vehicle, character, 0, 3)) ModLog.Error("Cinematic boarding rollback rejected for #" + character.ID); }
                    catch (Exception error) { ModLog.Error("Cinematic boarding rollback: " + error.Message); }
                }
            } finally {
#if SERVER
                CinematicActors.ReturnDetached();
#endif
            }
        }
    }
}
