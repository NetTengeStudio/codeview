using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using ScpItdMod.Core;
#if SERVER
using Barotrauma.Networking;
#endif

namespace ScpItdMod.Cinematics {
    internal static class CinematicSubScenes {
        private static bool starting;
        internal static ulong CatalogHash { get; private set; }
        internal static bool Pending => pending.Count > 0;
        internal static readonly Dictionary<string, CinematicAuthoredScene> Scenes = new Dictionary<string, CinematicAuthoredScene>(StringComparer.Ordinal);
        private sealed class PendingScene {
            internal readonly CinematicAuthoredScene Scene;
            internal CinematicPreparation Preparation;
            internal PendingScene(CinematicAuthoredScene scene) => Scene = scene;
        }
        private static readonly List<PendingScene> pending = new List<PendingScene>();

        internal static List<CinematicAuthoredScene> Compile(Submarine submarine, bool editor) {
            var records = new List<CinematicMarkerRecord>();
            var entities = new List<CinematicEntityRecord>();
            var known = new HashSet<ushort>();
            BovCinematicAdapter.TryCreate(out BovCinematicAdapter bov, out string bovFailure);
            string vehicleCause = bov == null ? "Вертолёт не опознан: " + bovFailure : null;
            Span<int> seats = stackalloc int[16];
            foreach (ScpCineMarker marker in ScpCineMarker.All) {
                Item item = marker.Owner;
                if (item.Removed || !ReferenceEquals(item.Submarine, submarine)) continue;
                if (editor && marker.MarkerKind is CinematicMarkerKind.Camera or CinematicMarkerKind.Effect) marker.CaptureSubjectOffset();
                CinematicMarkerRecord record = marker.Record();
                records.Add(record);
                foreach (MapEntity linked in item.linkedTo) {
                    if (linked is not Item other || other.Removed || other.GetComponent<ScpCineMarker>() != null || !known.Add(other.ID)) continue;
                    var entity = new CinematicEntityRecord { Id = other.ID, Kind = CinematicEntityKind.Item, Name = other.Prefab.Identifier.Value };
                    if (bov != null && bov.IsRotorcraft(other)) {
                        entity.Kind = CinematicEntityKind.Rotorcraft;
                        int count = bov.PassengerSeats(other, seats);
                        entity.PassengerSeats = seats.Slice(0, count).ToArray();
                    } else if (bov != null && other.HasTag("bovvehicle".ToIdentifier())) vehicleCause ??= "Предмет " + other.Prefab.Identifier.Value + " #" + other.ID + " связан, но BOV не считает его вертолётом (нет компонента машины или данных ротора)";
                    entities.Add(entity);
                }
            }
            List<CinematicAuthoredScene> scenes = CinematicAuthoring.Compile(records, entities);
            if (vehicleCause != null) foreach (CinematicAuthoredScene scene in scenes) if (!scene.Valid) scene.Diagnostics.Insert(0, new CinematicDiagnostic(scene.SceneMarker, CinematicSeverity.Error, vehicleCause));
            return scenes;
        }

        internal static void RoundStart() {
            Reset(); Scenes.Clear(); CinematicHost.Catalog.Clear();
            CatalogHash = 0;
            foreach (Submarine submarine in Submarine.Loaded) {
                foreach (CinematicAuthoredScene scene in Compile(submarine, false)) {
                    foreach (CinematicDiagnostic d in scene.Diagnostics) {
                        string line = "Cinematic " + scene.Id + " in " + submarine.Info?.Name + ": " + d;
                        if (d.Severity == CinematicSeverity.Error) ModLog.Error(line); else ModLog.Message(line);
                    }
                    if (!scene.Valid) continue;
                    if (!CinematicHost.Catalog.TryAdd(scene.Plan.Id, scene.Plan)) { ModLog.Error("Cinematic " + scene.Id + " is defined in two loaded submarines; rename one scene"); continue; }
                    Scenes.Add(scene.Plan.Id, scene);
                    if (CinematicHost.Authority && scene.Trigger == CinematicSceneTrigger.RoundStart) pending.Add(new PendingScene(scene));
                }
            }
            if (Scenes.Count > 0) ModLog.Message("Cinematic scenes authored in loaded submarines: " + string.Join(", ", Scenes.Keys));
            CatalogHash = CinematicReadiness.Fingerprint(CinematicHost.Catalog);
#if CLIENT
            CinematicClient.CatalogReady();
#endif
        }

        internal static void Reset() {
            for (int i = pending.Count - 1; i >= 0; i--) {
                try { pending[i].Preparation?.Dispose(); }
                catch (Exception error) { ModLog.Error("Cinematic preparation recovery: " + error.Message); }
            }
            pending.Clear();
        }

        internal static void BeforeSimulation() {
            if (!Pending || !CinematicHost.Authority || starting) return;
            starting = true;
            try {
                for (int i = 0; i < pending.Count;) {
                    PendingScene entry = pending[i];
                    CinematicAuthoredScene scene = entry.Scene;
                    try {
                        if (entry.Preparation == null) {
                            BovCinematicAdapter bov = null;
                            entry.Preparation = new CinematicPreparation(scene.Plan, CinematicHost.ReserveId(), binding => {
                                if (bov == null && !BovCinematicAdapter.TryCreate(out bov, out string failure)) throw new CinematicBindingUnavailableException(failure);
                                string name = scene.Plan.Bindings[binding].Id;
                                if (!scene.Bindings.TryGetValue(name, out ushort id) || Entity.FindEntityByID(id) is not Item { Removed: false } item)
                                    throw new CinematicBindingUnavailableException("Preparation vehicle is missing: " + name);
                                if (!bov.Read(item, out _)) throw new CinematicBindingUnavailableException("Preparation vehicle is not live: " + name);
                                return new LiveRotorcraft(bov, item);
                            });
                        }
                        if (!entry.Preparation.Step()) throw new InvalidOperationException("Cinematic prepared airframe became unavailable");
                        if (scene.Plan.Passengers != null && !PassengersReady()) { i++; continue; }
                        CinematicHost.Start(scene.Plan.Id, scene.Bindings, null, entry.Preparation);
                    } catch (CinematicBindingUnavailableException) { i++; continue; }
                    catch (Exception error) { ModLog.Error("Cinematic " + scene.Id + " did not start: " + error.Message); }
                    try { entry.Preparation?.Dispose(); }
                    catch (Exception error) { ModLog.Error("Cinematic preparation recovery: " + error.Message); }
                    pending.RemoveAt(i);
                }
            } finally { starting = false; }
        }

        private static bool PassengersReady() {
#if SERVER
            if (GameMain.Server == null || GameMain.Server.ConnectedClients.Count == 0) return false;
            foreach (Client client in GameMain.Server.ConnectedClients) {
                if (client.Connection == null || client.SpectateOnly) continue;
                if (!client.InGame || !CinematicServer.Ready(client)) return false;
                if (!client.HasSpawned && client.CharacterInfo?.PermanentlyDead != true) return false;
            }
            return true;
#else
            return Character.Controlled is { IsDead: false, Removed: false };
#endif
        }
        internal static void Signal(string signal) {
            if (!CinematicHost.Authority || string.IsNullOrEmpty(signal)) return;
            foreach (CinematicAuthoredScene scene in Scenes.Values) if (scene.Trigger == CinematicSceneTrigger.Signal && scene.TriggerSignal == signal) Begin(scene);
        }

        internal static bool Begin(CinematicAuthoredScene scene) {
            try { CinematicHost.Start(scene.Plan.Id, scene.Bindings); return true; }
            catch (Exception error) { ModLog.Error("Cinematic " + scene.Id + " did not start: " + error.Message); return false; }
        }

        internal static List<Character> PassengerCandidates() {
            var result = new List<Character>();
#if SERVER
            if (GameMain.Server != null)
                foreach (Client client in GameMain.Server.ConnectedClients)
                    if (client.Character is { IsDead: false, Removed: false } character && !result.Contains(character)) result.Add(character);
#else
            if (Character.Controlled is { IsDead: false, Removed: false } controlled) result.Add(controlled);
#endif
            var crew = new List<Character>();
            foreach (Character character in Character.CharacterList)
                if (character is { IsDead: false, Removed: false, IsHuman: true } && character.TeamID == CharacterTeamType.Team1 && !result.Contains(character)) crew.Add(character);
            crew.Sort((a, b) => a.ID.CompareTo(b.ID));
            result.AddRange(crew);
            return result;
        }
    }
}
