using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;
using ScpItdMod.Core;

namespace ScpItdMod.Cinematics {
    public enum CinematicMarkerKind : byte { Scene, Shot, Camera, Waypoint, Actor, Effect }
    public enum CinematicSceneTrigger : byte { RoundStart, Signal, Manual }
    public enum CinematicShotAdvance : byte { AfterTime, VehicleReady, RouteDone, Landed, Waypoint, ActorsDone, Signal, Despawned, Contact }
    public enum CinematicShotBranch : byte { None, VehicleUnsafe, Waypoint, Landed, RouteDone, Signal }
    public enum CinematicCameraKind : byte { Fixed, Dolly, Follow }
    public enum CinematicWaypointKind : byte { FlyThrough, Hover, Land }
    public enum CinematicHeadingKind : byte { Auto, FaceLeft, FaceRight }
    public enum CinematicActorKind : byte { Disembark, MoveTo, Face, Pose, Board }
    public enum CinematicActorGroup : byte { AllPassengers, Seat }
    public enum CinematicPoseKind : byte { Stand, Crouch, Aim }
    public enum CinematicEffectKind : byte { Sound, Particles, RotorWash, Light, Signal, EngineOn, EngineOff, Letterbox, Fade, Title, Despawn }
    public enum CinematicEntityKind : byte { Rotorcraft, Item }
    public enum CinematicSeverity : byte { Info, Warning, Error }

    public sealed class CinematicSceneSettings {
        public string Name = "scene";
        public CinematicSceneTrigger Trigger = CinematicSceneTrigger.RoundStart;
        public string TriggerSignal = "";
        public float MaxSeconds = 600;
        public bool BoardPassengers = true;
        public string PassengerSeats = "";
        public float PreviewCrewMassKg = 80;
    }

    public sealed class CinematicShotSettings {
        public string Name = "";
        public float MinSeconds, MaxSeconds = 60;
        public CinematicShotAdvance Advance = CinematicShotAdvance.AfterTime;
        public string AdvanceValue = "";
        public string Next = "";
        public CinematicShotBranch Branch = CinematicShotBranch.None;
        public string BranchValue = "";
        public string BranchTo = "";
        public string TimeoutTo = "abort";
    }

    public sealed class CinematicCameraSettings {
        public CinematicCameraKind Kind = CinematicCameraKind.Fixed;
        public float StartAt, Duration = 8, Zoom = 1, Blend = 2, Shake = .2f, FrameX = .5f, FrameY = .55f, LookAhead = .6f;
        public bool Cut, MatchVelocity;
        public float Impulse;
    }

    public sealed class CinematicWaypointSettings {
        public string Name = "";
        public CinematicWaypointKind Kind = CinematicWaypointKind.FlyThrough;
        public float Speed = 25, PassSpeed, Acceleration = 1.5f, Jerk = 1, Radius = 4, Hold;
        public CinematicHeadingKind Heading = CinematicHeadingKind.Auto;
        public float LandingRate = .35f, SettleSeconds = 2, MaxSlopeDegrees = 10, Clearance = 5;
    }

    public sealed class CinematicActorSettings {
        public CinematicActorKind Action = CinematicActorKind.Disembark;
        public CinematicActorGroup Who = CinematicActorGroup.AllPassengers;
        public int Seat;
        public float StartAt, Interval = 1.5f, Speed = 1.5f, Radius = 1, Timeout = 30, Spacing = 80;
        public CinematicPoseKind Pose = CinematicPoseKind.Stand;
    }

    public sealed class CinematicEffectSettings {
        public CinematicEffectKind Effect = CinematicEffectKind.Sound;
        public string Resource = "";
        public float StartAt, Duration = 5, Intensity = 1, Range = 30, Direction = 90, Speed;
        public bool Loop, Enabled = true;
        public string Signal = "", Text = "";
        public float Fade = 1, Place = .5f;
        public string RemovalWaypoint = "";
    }

    public sealed class CinematicMarkerRecord {
        public ushort Id;
        public CinematicMarkerKind Kind;
        public Vec2 Position, SubjectOffset;
        public ushort[] Links = Array.Empty<ushort>();
        public CinematicSceneSettings Scene;
        public CinematicShotSettings Shot;
        public CinematicCameraSettings Camera;
        public CinematicWaypointSettings Waypoint;
        public CinematicActorSettings Actor;
        public CinematicEffectSettings Effect;
    }

    public sealed class CinematicEntityRecord {
        public ushort Id;
        public CinematicEntityKind Kind;
        public int[] PassengerSeats = Array.Empty<int>();
        public string Name = "";
    }

    public readonly struct CinematicDiagnostic {
        public readonly ushort Marker;
        public readonly CinematicSeverity Severity;
        public readonly string Message;
        public CinematicDiagnostic(ushort marker, CinematicSeverity severity, string message) { Marker = marker; Severity = severity; Message = message; }
        public override string ToString() => Severity + " #" + Marker + ": " + Message;
    }

    public sealed class CinematicAuthoredScene {
        public string Id;
        public ushort SceneMarker;
        public CinematicSceneTrigger Trigger;
        public string TriggerSignal;
        public XDocument Document;
        public CinematicPlan Plan;
        public readonly Dictionary<string, ushort> Bindings = new Dictionary<string, ushort>(StringComparer.Ordinal);
        public readonly List<ushort> Shots = new List<ushort>();
        public readonly List<CinematicDiagnostic> Diagnostics = new List<CinematicDiagnostic>();
        public HashSet<ushort> Used;
        public float PreviewPayloadKg;
        public bool Valid => Plan != null;
    }

    public static class CinematicAuthoring {
        public const string AnchorBinding = "scene";
        private static readonly CultureInfo invariant = CultureInfo.InvariantCulture;

        public static List<CinematicAuthoredScene> Compile(IReadOnlyList<CinematicMarkerRecord> markers, IReadOnlyList<CinematicEntityRecord> entities) {
            var byId = new Dictionary<ushort, CinematicMarkerRecord>();
            foreach (CinematicMarkerRecord marker in markers) byId[marker.Id] = marker;
            var things = new Dictionary<ushort, CinematicEntityRecord>();
            foreach (CinematicEntityRecord entity in entities) things[entity.Id] = entity;
            var scenes = new List<CinematicAuthoredScene>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var sorted = new List<CinematicMarkerRecord>(markers);
            sorted.Sort((a, b) => a.Id.CompareTo(b.Id));
            foreach (CinematicMarkerRecord marker in sorted) {
                if (marker.Kind != CinematicMarkerKind.Scene) continue;
                var scene = new CinematicAuthoredScene { Id = marker.Scene.Name, SceneMarker = marker.Id, Trigger = marker.Scene.Trigger, TriggerSignal = marker.Scene.TriggerSignal };
                scenes.Add(scene);
                if (!CinematicPlan.IsId(scene.Id)) { scene.Diagnostics.Add(new CinematicDiagnostic(marker.Id, CinematicSeverity.Error, "Имя сцены: 1..64 латинских букв, цифр, '_', '.' или '-'")); continue; }
                if (!names.Add(scene.Id)) { scene.Diagnostics.Add(new CinematicDiagnostic(marker.Id, CinematicSeverity.Error, "Сцена с именем " + scene.Id + " уже есть в этой подлодке")); continue; }
                try { new Builder(marker, byId, things, scene).Build(); }
                catch (AuthoringException error) { scene.Diagnostics.Add(new CinematicDiagnostic(error.Marker, CinematicSeverity.Error, error.Message)); scene.Document = null; }
                if (scene.Document == null) continue;
                try { scene.Plan = CinematicPlan.Parse(scene.Document); }
                catch (Exception error) { scene.Diagnostics.Add(new CinematicDiagnostic(marker.Id, CinematicSeverity.Error, "План не прошёл проверку: " + error.Message)); }
            }
            foreach (CinematicMarkerRecord marker in sorted) {
                if (marker.Kind == CinematicMarkerKind.Scene) continue;
                bool owned = false;
                foreach (CinematicAuthoredScene scene in scenes) if (scene.Bindings.ContainsValue(marker.Id) || Reaches(scene, marker.Id)) { owned = true; break; }
                if (!owned && scenes.Count > 0) scenes[0].Diagnostics.Add(new CinematicDiagnostic(marker.Id, CinematicSeverity.Warning, "Маркер " + marker.Kind + " не связан ни с одной сценой и не исполняется"));
            }
            return scenes;
        }

        private static bool Reaches(CinematicAuthoredScene scene, ushort marker) => scene.Used != null && scene.Used.Contains(marker);

        private sealed class AuthoringException : Exception {
            internal readonly ushort Marker;
            internal AuthoringException(ushort marker, string message) : base(message) { Marker = marker; }
        }

        private sealed class Route {
            internal readonly List<CinematicMarkerRecord> Points = new List<CinematicMarkerRecord>();
            internal CinematicEntityRecord Vehicle;
            internal CinematicMarkerRecord Shot;
        }

        private sealed class Builder {
            private readonly CinematicMarkerRecord sceneMarker;
            private readonly Dictionary<ushort, CinematicMarkerRecord> markers;
            private readonly Dictionary<ushort, CinematicEntityRecord> entities;
            private readonly CinematicAuthoredScene scene;
            private readonly List<CinematicMarkerRecord> shots = new List<CinematicMarkerRecord>();
            private readonly Dictionary<string, CinematicMarkerRecord> shotNames = new Dictionary<string, CinematicMarkerRecord>(StringComparer.Ordinal);
            private readonly Dictionary<ushort, string> stageIds = new Dictionary<ushort, string>();
            private readonly Dictionary<ushort, string> successors = new Dictionary<ushort, string>();
            private readonly List<Route> routes = new List<Route>();
            private readonly List<CinematicEntityRecord> vehicles = new List<CinematicEntityRecord>();
            private readonly Dictionary<ushort, string> bindingOf = new Dictionary<ushort, string>();
            private readonly HashSet<string> producedSignals = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<string> awaitedSignals = new HashSet<string>(StringComparer.Ordinal);
            private readonly List<string> passengerBindings = new List<string>();
            private CinematicEntityRecord passengerVehicle;

            internal Builder(CinematicMarkerRecord sceneMarker, Dictionary<ushort, CinematicMarkerRecord> markers, Dictionary<ushort, CinematicEntityRecord> entities, CinematicAuthoredScene scene) {
                this.sceneMarker = sceneMarker; this.markers = markers; this.entities = entities; this.scene = scene;
                scene.Used = new HashSet<ushort> { sceneMarker.Id };
            }

            private IEnumerable<CinematicMarkerRecord> Linked(CinematicMarkerRecord marker, CinematicMarkerKind kind) {
                foreach (ushort id in marker.Links) if (markers.TryGetValue(id, out CinematicMarkerRecord other) && other.Kind == kind) yield return other;
                foreach (CinematicMarkerRecord other in markers.Values) if (other.Kind == kind && Array.IndexOf(other.Links, marker.Id) >= 0 && Array.IndexOf(marker.Links, other.Id) < 0) yield return other;
            }

            private IEnumerable<CinematicEntityRecord> LinkedEntities(CinematicMarkerRecord marker, CinematicEntityKind kind) {
                foreach (ushort id in marker.Links) if (entities.TryGetValue(id, out CinematicEntityRecord entity) && entity.Kind == kind) yield return entity;
            }

            private static List<CinematicMarkerRecord> Sorted(IEnumerable<CinematicMarkerRecord> source) {
                var list = new List<CinematicMarkerRecord>(source);
                list.Sort((a, b) => a.Id.CompareTo(b.Id));
                return list;
            }

            private Vec2 Local(Vec2 position) => position - sceneMarker.Position;

            internal void Build() {
                CollectVehicles();
                CollectShots();
                CollectRoutes();
                var root = new XElement("Cinematic", new XAttribute("id", scene.Id), new XAttribute("version", CinematicPlan.Version), new XAttribute("maxSeconds", Number(sceneMarker.Scene.MaxSeconds)));
                var bindings = new XElement("Bindings", new XElement("Bind", new XAttribute("id", AnchorBinding), new XAttribute("kind", "Anchor")));
                scene.Bindings[AnchorBinding] = sceneMarker.Id;
                foreach (CinematicEntityRecord vehicle in vehicles) bindings.Add(new XElement("Bind", new XAttribute("id", bindingOf[vehicle.Id]), new XAttribute("kind", "Rotorcraft")));
                root.Add(bindings);
                AddPassengers(root, bindings);
                var stages = new List<XElement>();
                for (int i = 0; i < shots.Count; i++) stages.Add(BuildStage(shots[i], i, bindings));
                AddEvents(root);
                foreach (XElement stage in stages) root.Add(stage);
                scene.Document = new XDocument(root);
                foreach (CinematicMarkerRecord shot in shots) scene.Shots.Add(shot.Id);
            }

            private void CollectVehicles() {
                foreach (CinematicEntityRecord vehicle in LinkedEntities(sceneMarker, CinematicEntityKind.Rotorcraft)) AddVehicle(vehicle);
            }

            private string AddVehicle(CinematicEntityRecord vehicle) {
                if (bindingOf.TryGetValue(vehicle.Id, out string id)) return id;
                id = "v" + vehicle.Id.ToString(invariant);
                bindingOf.Add(vehicle.Id, id); vehicles.Add(vehicle); scene.Bindings[id] = vehicle.Id;
                return id;
            }

            private void CollectShots() {
                var first = Sorted(Linked(sceneMarker, CinematicMarkerKind.Shot));
                if (first.Count == 0) throw new AuthoringException(sceneMarker.Id, "Сцена не связана ни с одним кадром (маркер «Кадр»): удерживайте Пробел и щёлкните по кадру");
                if (first.Count > 1) throw new AuthoringException(sceneMarker.Id, "Сцена связана с несколькими кадрами; свяжите со сценой только первый кадр, остальные цепочкой");
                var pending = new Stack<CinematicMarkerRecord>();
                pending.Push(first[0]);
                var seen = new HashSet<ushort>();
                var followers = new Dictionary<ushort, List<CinematicMarkerRecord>>();
                while (pending.Count > 0) {
                    CinematicMarkerRecord shot = pending.Pop();
                    if (!seen.Add(shot.Id)) continue;
                    if (shots.Count >= CinematicPlan.MaximumStages) throw new AuthoringException(shot.Id, "Сцена длиннее " + CinematicPlan.MaximumStages + " кадров");
                    shots.Add(shot); scene.Used.Add(shot.Id);
                    string id = shot.Shot.Name.Length > 0 ? shot.Shot.Name : "shot" + shot.Id.ToString(invariant);
                    if (!CinematicPlan.IsId(id) || id is "end" or "abort" or "next") throw new AuthoringException(shot.Id, "Недопустимое имя кадра: " + id);
                    if (!shotNames.TryAdd(id, shot)) throw new AuthoringException(shot.Id, "Имя кадра повторяется: " + id);
                    stageIds.Add(shot.Id, id);
                    var candidates = new List<CinematicMarkerRecord>();
                    foreach (CinematicMarkerRecord next in Sorted(Linked(shot, CinematicMarkerKind.Shot))) if (!seen.Contains(next.Id)) candidates.Add(next);
                    followers.Add(shot.Id, candidates);
                    for (int i = candidates.Count - 1; i >= 0; i--) pending.Push(candidates[i]);
                }
                foreach (CinematicMarkerRecord shot in shots) {
                    List<CinematicMarkerRecord> candidates = followers[shot.Id];
                    candidates.RemoveAll(next => shots.IndexOf(next) < shots.IndexOf(shot));
                    if (candidates.Count > 1 && shot.Shot.Next.Trim().Length == 0)
                        throw new AuthoringException(shot.Id, "Кадр связан с несколькими следующими кадрами: укажите имя в поле «Следующий кадр»");
                    successors[shot.Id] = candidates.Count == 1 ? stageIds[candidates[0].Id] : candidates.Count == 0 ? "end" : null;
                }
            }

            private void CollectRoutes() {
                foreach (CinematicMarkerRecord shot in shots) {
                    foreach (CinematicMarkerRecord start in Sorted(Linked(shot, CinematicMarkerKind.Waypoint))) {
                        var route = new Route { Shot = shot };
                        CinematicMarkerRecord previous = null, current = start;
                        while (current != null) {
                            if (route.Points.Contains(current)) throw new AuthoringException(current.Id, "Маршрут замкнут в кольцо");
                            route.Points.Add(current); scene.Used.Add(current.Id);
                            foreach (CinematicEntityRecord vehicle in LinkedEntities(current, CinematicEntityKind.Rotorcraft)) {
                                if (route.Vehicle != null && route.Vehicle.Id != vehicle.Id) throw new AuthoringException(current.Id, "Маршрут связан с двумя машинами");
                                route.Vehicle = vehicle;
                            }
                            CinematicMarkerRecord next = null;
                            foreach (CinematicMarkerRecord candidate in Sorted(Linked(current, CinematicMarkerKind.Waypoint))) {
                                if (candidate == previous || route.Points.Contains(candidate)) continue;
                                if (next != null) throw new AuthoringException(current.Id, "Путевая точка ветвится: свяжите её только с предыдущей и следующей");
                                next = candidate;
                            }
                            previous = current; current = next;
                            if (route.Points.Count > CinematicPlan.MaximumWaypoints) throw new AuthoringException(start.Id, "Маршрут длиннее " + CinematicPlan.MaximumWaypoints + " точек");
                        }
                        if (route.Vehicle == null) {
                            if (vehicles.Count == 1) route.Vehicle = vehicles[0];
                            else throw new AuthoringException(start.Id, vehicles.Count == 0 ? "Маршрут не связан с вертолётом: свяжите первую точку или сцену с вертолётом" : "Маршрут не указывает, каким вертолётом лететь: свяжите точку с вертолётом");
                        }
                        AddVehicle(route.Vehicle);
                        routes.Add(route);
                    }
                }
            }

            private void AddPassengers(XElement root, XElement bindings) {
                CinematicSceneSettings settings = sceneMarker.Scene;
                if (!settings.BoardPassengers || vehicles.Count == 0) return;
                passengerVehicle = vehicles[0];
                int[] seats;
                if (settings.PassengerSeats.Trim().Length == 0) seats = passengerVehicle.PassengerSeats;
                else {
                    string[] parts = settings.PassengerSeats.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    seats = new int[parts.Length];
                    for (int i = 0; i < parts.Length; i++)
                        if (!int.TryParse(parts[i], NumberStyles.None, invariant, out seats[i]) || seats[i] > 15) throw new AuthoringException(sceneMarker.Id, "Места пассажиров: список номеров через запятую, например 2,3,4");
                }
                if (seats.Length == 0) { scene.Diagnostics.Add(new CinematicDiagnostic(sceneMarker.Id, CinematicSeverity.Warning, "У вертолёта нет пассажирских мест: игроки не будут посажены")); return; }
                string vehicle = bindingOf[passengerVehicle.Id];
                var list = new string[seats.Length];
                for (int i = 0; i < seats.Length; i++) {
                    string id = "p" + seats[i].ToString(invariant);
                    list[i] = seats[i].ToString(invariant);
                    bindings.Add(new XElement("Bind", new XAttribute("id", id), new XAttribute("kind", "Actor"), new XAttribute("vehicle", vehicle), new XAttribute("seat", seats[i]), new XAttribute("required", "false")));
                    passengerBindings.Add(id);
                }
                root.Add(new XElement("Passengers", new XAttribute("vehicle", vehicle), new XAttribute("seats", string.Join(",", list))));
                scene.PreviewPayloadKg = seats.Length * settings.PreviewCrewMassKg;
            }

            private XElement BuildStage(CinematicMarkerRecord shot, int index, XElement bindings) {
                CinematicShotSettings settings = shot.Shot;
                if (settings.MaxSeconds <= 0 || settings.MinSeconds < 0 || settings.MinSeconds > settings.MaxSeconds) throw new AuthoringException(shot.Id, "Длительность кадра: 0 ≤ минимум ≤ максимум, максимум > 0");
                var stage = new XElement("Stage", new XAttribute("id", stageIds[shot.Id]), new XAttribute("min", Number(settings.MinSeconds)), new XAttribute("max", Number(settings.MaxSeconds)),
                    new XAttribute("timeout", Target(shot, settings.TimeoutTo, index)));
                int cue = 0;
                foreach (CinematicMarkerRecord camera in Sorted(Linked(shot, CinematicMarkerKind.Camera))) { stage.Add(Camera(camera, settings)); scene.Used.Add(camera.Id); cue++; }
                foreach (Route route in routes) {
                    if (route.Shot != shot) continue;
                    string vehicle = bindingOf[route.Vehicle.Id];
                    bool engine = false;
                    foreach (CinematicMarkerRecord effect in Linked(shot, CinematicMarkerKind.Effect)) engine |= effect.Effect.Effect == CinematicEffectKind.EngineOn;
                    if (!engine) {
                        stage.Add(new XElement("Engine", new XAttribute("id", "engine" + route.Points[0].Id.ToString(invariant)), new XAttribute("target", vehicle), new XAttribute("enabled", "true")));
                        scene.Diagnostics.Add(new CinematicDiagnostic(route.Points[0].Id, CinematicSeverity.Info, "Двигатели запускаются в начале кадра, потому что маршрут начинается здесь"));
                    }
                    var flight = new XElement("Flight", new XAttribute("id", "route" + route.Points[0].Id.ToString(invariant)), new XAttribute("target", vehicle), new XAttribute("relativeTo", AnchorBinding));
                    for (int i = 0; i < route.Points.Count; i++) flight.Add(Waypoint(route.Points[i], i == route.Points.Count - 1));
                    stage.Add(flight); cue++;
                }
                CinematicMarkerRecord boarding = null, movement = null;
                foreach (CinematicMarkerRecord actor in Sorted(Linked(shot, CinematicMarkerKind.Actor))) {
                    if (actor.Actor.Action == CinematicActorKind.Board) boarding ??= actor;
                    if (actor.Actor.Action == CinematicActorKind.MoveTo) movement ??= actor;
                    foreach (XElement e in Actor(actor)) { stage.Add(e); cue++; }
                    scene.Used.Add(actor.Id);
                }
                // A walk cued with a disembark waits for that person's climb to end; after boarding there is nowhere to walk to.
                if (boarding != null && movement != null)
                    throw new AuthoringException(movement.Id, "Экипаж нельзя вести, пока он садится: перенесите перемещение в следующий кадр с условием «Экипаж закончил»");
                foreach (CinematicMarkerRecord effect in Sorted(Linked(shot, CinematicMarkerKind.Effect))) { stage.Add(Effect(effect, bindings)); scene.Used.Add(effect.Id); cue++; }
                if (cue > CinematicPlan.MaximumCues) throw new AuthoringException(shot.Id, "В кадре больше " + CinematicPlan.MaximumCues + " действий");
                if (settings.Branch != CinematicShotBranch.None) stage.Add(new XElement("Transition", new XAttribute("when", BranchCondition(shot, settings.Branch, settings.BranchValue)),
                    new XAttribute("to", Target(shot, settings.BranchTo, index))));
                stage.Add(new XElement("Transition", new XAttribute("when", Condition(shot, settings.Advance, settings.AdvanceValue)), new XAttribute("to", Target(shot, settings.Next, index))));
                return stage;
            }

            private string BranchCondition(CinematicMarkerRecord shot, CinematicShotBranch branch, string value) => branch switch {
                CinematicShotBranch.VehicleUnsafe => "flight." + ShotVehicle(shot) + ".unsafe",
                CinematicShotBranch.Waypoint => Condition(shot, CinematicShotAdvance.Waypoint, value),
                CinematicShotBranch.Landed => Condition(shot, CinematicShotAdvance.Landed, value),
                CinematicShotBranch.RouteDone => Condition(shot, CinematicShotAdvance.RouteDone, value),
                _ => Condition(shot, CinematicShotAdvance.Signal, value)
            };

            private string Target(CinematicMarkerRecord shot, string target, int index) {
                string value = (target ?? "").Trim();
                if (value.Length == 0 || value == "next") return successors[shot.Id] ?? throw new AuthoringException(shot.Id, "Кадр ветвится: укажите имя следующего кадра");
                if (value is "end" or "abort") return value;
                if (!shotNames.ContainsKey(value)) throw new AuthoringException(shot.Id, "Кадр «" + value + "» не найден в этой сцене");
                return value;
            }

            private string ShotVehicle(CinematicMarkerRecord shot) {
                foreach (CinematicEntityRecord linked in LinkedEntities(shot, CinematicEntityKind.Rotorcraft)) return AddVehicle(linked);
                foreach (Route route in routes) if (route.Shot == shot) return bindingOf[route.Vehicle.Id];
                if (vehicles.Count == 1) return bindingOf[vehicles[0].Id];
                throw new AuthoringException(shot.Id, vehicles.Count == 0 ? "Условие кадра ждёт вертолёт, но в сцене нет вертолёта" : "Условие кадра не знает, какой вертолёт ждать: свяжите кадр с вертолётом");
            }

            private string Condition(CinematicMarkerRecord shot, CinematicShotAdvance advance, string value) {
                string vehicle;
                switch (advance) {
                    case CinematicShotAdvance.AfterTime: return "always";
                    case CinematicShotAdvance.Despawned: return "despawn.done";
                    case CinematicShotAdvance.VehicleReady: return "flight." + ShotVehicle(shot) + ".ready";
                    case CinematicShotAdvance.RouteDone: return "flight." + ShotVehicle(shot) + ".arrived";
                    case CinematicShotAdvance.Landed: return "flight." + ShotVehicle(shot) + ".landed";
                    case CinematicShotAdvance.Contact: return "flight." + ShotVehicle(shot) + ".contact";
                    case CinematicShotAdvance.ActorsDone: return "actors.done";
                    case CinematicShotAdvance.Waypoint:
                        vehicle = ShotVehicle(shot);
                        if (!WaypointExists(vehicle, value)) throw new AuthoringException(shot.Id, "Путевая точка «" + value + "» не найдена на маршрутах " + vehicle);
                        return CinematicPlan.WaypointSignal(vehicle, value);
                    case CinematicShotAdvance.Signal:
                        if (!CinematicPlan.IsId(value) || (value.StartsWith("flight.", StringComparison.Ordinal) || value.StartsWith("despawn.", StringComparison.Ordinal)) || value is "always" or "actors.done") throw new AuthoringException(shot.Id, "Имя сигнала: латиница, цифры, '_', '.', '-' и не служебное имя");
                        awaitedSignals.Add(value);
                        return value;
                    default: throw new AuthoringException(shot.Id, "Неизвестное условие кадра");
                }
            }

            private bool WaypointExists(string vehicle, string name) {
                foreach (Route route in routes) {
                    if (bindingOf[route.Vehicle.Id] != vehicle) continue;
                    foreach (CinematicMarkerRecord point in route.Points) if (WaypointId(point) == name) return true;
                }
                return false;
            }

            private static string WaypointId(CinematicMarkerRecord point) => point.Waypoint.Name.Length > 0 ? point.Waypoint.Name : "wp" + point.Id.ToString(invariant);

            private XElement Waypoint(CinematicMarkerRecord point, bool last) {
                CinematicWaypointSettings w = point.Waypoint;
                if (w.Kind == CinematicWaypointKind.Land && !last) throw new AuthoringException(point.Id, "Посадка возможна только в последней точке маршрута");
                if (w.Kind == CinematicWaypointKind.FlyThrough && w.Hold > 0) throw new AuthoringException(point.Id, "Пролётная точка не может удерживать вертолёт; выберите «Зависание»");
                Vec2 at = Local(point.Position);
                var e = new XElement("Waypoint", new XAttribute("id", WaypointId(point)), new XAttribute("x", Number(at.X)), new XAttribute("y", Number(at.Y)),
                    new XAttribute("mode", w.Kind switch { CinematicWaypointKind.Hover => "Hover", CinematicWaypointKind.Land => "Land", _ => "Cruise" }),
                    new XAttribute("speed", Number(w.Speed)), new XAttribute("acceleration", Number(w.Acceleration)), new XAttribute("jerk", Number(w.Jerk)), new XAttribute("radius", Number(w.Radius)));
                if (w.PassSpeed > 0) e.Add(new XAttribute("terminalSpeed", Number(MathF.Min(w.PassSpeed, w.Speed))));
                if (w.Hold > 0) e.Add(new XAttribute("hold", Number(w.Hold)));
                if (w.Heading != CinematicHeadingKind.Auto) e.Add(new XAttribute("headingDegrees", w.Heading == CinematicHeadingKind.FaceLeft ? "0" : "180"));
                if (w.Kind == CinematicWaypointKind.Land) {
                    e.Add(new XAttribute("landingRate", Number(w.LandingRate)), new XAttribute("settle", Number(w.SettleSeconds)),
                        new XAttribute("maxSlopeDegrees", Number(w.MaxSlopeDegrees)), new XAttribute("clearance", Number(w.Clearance)));
                }
                return e;
            }

            private XElement Camera(CinematicMarkerRecord camera, CinematicShotSettings shot) {
                CinematicCameraSettings c = camera.Camera;
                var e = new XElement("Camera", new XAttribute("id", "camera" + camera.Id.ToString(invariant)), new XAttribute("at", Number(c.StartAt)),
                    new XAttribute("zoom", Number(c.Zoom)), new XAttribute("blend", Number(c.Blend)), new XAttribute("intensity", Number(c.Shake)), new XAttribute("cut", c.Cut ? "true" : "false"), new XAttribute("matchVelocity", c.MatchVelocity ? "true" : "false"), new XAttribute("impulse", Number(c.Impulse)));
                CinematicEntityRecord subject = null;
                foreach (CinematicEntityRecord linked in LinkedEntities(camera, CinematicEntityKind.Rotorcraft)) { subject = linked; break; }
                if (subject != null) e.Add(new XAttribute("target", AddVehicle(subject)));
                Vec2 at = Local(camera.Position);
                switch (c.Kind) {
                    case CinematicCameraKind.Follow:
                        if (subject == null) throw new AuthoringException(camera.Id, "Следящая камера должна быть связана с вертолётом");
                        Vec2 offset = camera.SubjectOffset;
                        e.Add(new XAttribute("mode", "Follow"), new XAttribute("x", Number(offset.X)), new XAttribute("y", Number(offset.Y)),
                            new XAttribute("frameX", Number(c.FrameX)), new XAttribute("frameY", Number(c.FrameY)), new XAttribute("lookAhead", Number(c.LookAhead)));
                        break;
                    case CinematicCameraKind.Dolly:
                        CinematicMarkerRecord end = null;
                        foreach (CinematicMarkerRecord other in Sorted(Linked(camera, CinematicMarkerKind.Camera))) {
                            bool shotLinked = false;
                            foreach (CinematicMarkerRecord _ in Linked(other, CinematicMarkerKind.Shot)) { shotLinked = true; break; }
                            if (!shotLinked) { end = other; break; }
                        }
                        if (end == null) throw new AuthoringException(camera.Id, "Проезд камеры: свяжите камеру со второй камерой (конечная точка), не связанной с кадром");
                        if (c.Duration <= 0 || c.Duration > shot.MaxSeconds) throw new AuthoringException(camera.Id, "Проезд камеры должен длиться (0; длительность кадра]");
                        scene.Used.Add(end.Id);
                        Vec2 to = Local(end.Position);
                        e.Add(new XAttribute("mode", "Establish"), new XAttribute("relativeTo", AnchorBinding), new XAttribute("x", Number(at.X)), new XAttribute("y", Number(at.Y)),
                            new XAttribute("endX", Number(to.X)), new XAttribute("endY", Number(to.Y)), new XAttribute("duration", Number(c.Duration)));
                        break;
                    default:
                        e.Add(new XAttribute("mode", "Fixed"), new XAttribute("relativeTo", AnchorBinding), new XAttribute("x", Number(at.X)), new XAttribute("y", Number(at.Y)));
                        break;
                }
                return e;
            }

            private IEnumerable<XElement> Actor(CinematicMarkerRecord marker) {
                CinematicActorSettings a = marker.Actor;
                var targets = new List<string>();
                if (a.Who == CinematicActorGroup.AllPassengers) targets.AddRange(passengerBindings);
                else {
                    string id = "p" + a.Seat.ToString(invariant);
                    if (!passengerBindings.Contains(id)) throw new AuthoringException(marker.Id, "Место " + a.Seat + " не входит в места пассажиров сцены");
                    targets.Add(id);
                }
                if (targets.Count == 0) throw new AuthoringException(marker.Id, "Действию экипажа некого выполнять: в сцене нет пассажиров");
                string vehicle = null;
                foreach (CinematicEntityRecord linked in LinkedEntities(marker, CinematicEntityKind.Rotorcraft)) { vehicle = AddVehicle(linked); break; }
                vehicle ??= passengerVehicle != null ? bindingOf[passengerVehicle.Id] : null;
                Vec2 at = Local(marker.Position);
                for (int i = 0; i < targets.Count; i++) {
                    string id = "actor" + marker.Id.ToString(invariant) + "_" + i.ToString(invariant);
                    double start = a.StartAt + i * (double)a.Interval;
                    var e = new XElement(a.Action switch { CinematicActorKind.Disembark => "Disembark", CinematicActorKind.Board => "Board", CinematicActorKind.MoveTo => "Move", CinematicActorKind.Face => "Face", _ => "Pose" },
                        new XAttribute("id", id), new XAttribute("target", targets[i]), new XAttribute("at", Number((float)start)));
                    switch (a.Action) {
                        case CinematicActorKind.Disembark:
                        case CinematicActorKind.Board:
                            if (vehicle == null) throw new AuthoringException(marker.Id, "Высадке и посадке нужен вертолёт: свяжите маркер с вертолётом");
                            e.Add(new XAttribute("relativeTo", vehicle));
                            // Both actions name the seat the passenger occupies: the helicopter refuses an exit from any other seat.
                            e.Add(new XAttribute("seat", targets[i].Substring(1)));
                            break;
                        case CinematicActorKind.MoveTo:
                            e.Add(new XAttribute("relativeTo", AnchorBinding), new XAttribute("x", Number(at.X + (i - (targets.Count - 1) * .5f) * a.Spacing)), new XAttribute("y", Number(at.Y)),
                                new XAttribute("speed", Number(a.Speed)), new XAttribute("radius", Number(a.Radius)), new XAttribute("duration", Number(a.Timeout)));
                            break;
                        case CinematicActorKind.Face:
                            e.Add(new XAttribute("relativeTo", AnchorBinding), new XAttribute("x", Number(at.X)), new XAttribute("y", Number(at.Y)));
                            break;
                        default:
                            e.Add(new XAttribute("value", a.Pose.ToString()));
                            break;
                    }
                    yield return e;
                }
            }

            private XElement Effect(CinematicMarkerRecord marker, XElement bindings) {
                CinematicEffectSettings f = marker.Effect;
                string id = "effect" + marker.Id.ToString(invariant);
                CinematicEntityRecord vehicle = null, item = null;
                foreach (CinematicEntityRecord linked in LinkedEntities(marker, CinematicEntityKind.Rotorcraft)) { vehicle = linked; break; }
                foreach (CinematicEntityRecord linked in LinkedEntities(marker, CinematicEntityKind.Item)) { item = linked; break; }
                Vec2 at = Local(marker.Position);
                switch (f.Effect) {
                    case CinematicEffectKind.Despawn:
                        if (vehicle == null) throw new AuthoringException(marker.Id, "Удалению нужен связанный вертолёт");
                        string removalVehicle = AddVehicle(vehicle);
                        if (!WaypointExists(removalVehicle, f.RemovalWaypoint)) throw new AuthoringException(marker.Id, "Удалению нужна существующая точка ухода этого вертолёта");
                        return new XElement("Despawn", new XAttribute("id", id), new XAttribute("target", removalVehicle), new XAttribute("at", Number(f.StartAt)),
                            new XAttribute("value", CinematicPlan.WaypointSignal(removalVehicle, f.RemovalWaypoint)), new XAttribute("radius", Number(f.Range)), new XAttribute("duration", Number(Positive(marker, f.Fade))));
                    case CinematicEffectKind.Signal:
                        if (!CinematicPlan.IsId(f.Signal) || (f.Signal.StartsWith("flight.", StringComparison.Ordinal) || f.Signal.StartsWith("despawn.", StringComparison.Ordinal)) || f.Signal is "always" or "actors.done") throw new AuthoringException(marker.Id, "Имя сигнала: латиница, цифры, '_', '.', '-' и не служебное имя");
                        producedSignals.Add(f.Signal);
                        return new XElement("Signal", new XAttribute("id", id), new XAttribute("value", f.Signal), new XAttribute("at", Number(f.StartAt)));
                    case CinematicEffectKind.Letterbox:
                        return new XElement("Letterbox", new XAttribute("id", id), new XAttribute("at", Number(f.StartAt)), new XAttribute("duration", Number(Positive(marker, f.Duration))),
                            new XAttribute("intensity", Number(f.Intensity)), new XAttribute("enabled", f.Enabled ? "true" : "false"));
                    case CinematicEffectKind.Fade:
                        return new XElement("Fade", new XAttribute("id", id), new XAttribute("at", Number(f.StartAt)), new XAttribute("duration", Number(Positive(marker, f.Duration))),
                            new XAttribute("intensity", Number(f.Intensity)), new XAttribute("enabled", f.Enabled ? "true" : "false"));
                    case CinematicEffectKind.Title:
                        return new XElement("Title", new XAttribute("id", id), new XAttribute("at", Number(f.StartAt)), new XAttribute("duration", Number(Positive(marker, f.Duration))),
                            new XAttribute("value", f.Text), new XAttribute("zoom", Number(f.Intensity)), new XAttribute("blend", Number(f.Fade)), new XAttribute("frameY", Number(f.Place)));
                    case CinematicEffectKind.EngineOn:
                    case CinematicEffectKind.EngineOff:
                        if (vehicle == null && vehicles.Count != 1) throw new AuthoringException(marker.Id, "Запуску и остановке двигателей нужен вертолёт: свяжите маркер с вертолётом");
                        var engine = new XElement("Engine", new XAttribute("id", id), new XAttribute("target", AddVehicle(vehicle ?? vehicles[0])), new XAttribute("at", Number(f.StartAt)),
                            new XAttribute("enabled", f.Effect == CinematicEffectKind.EngineOn ? "true" : "false"));
                        if (f.Effect == CinematicEffectKind.EngineOn && f.Signal == "warm") {
                            float radians = f.Direction * MathF.PI / 180f;
                            engine.Add(new XAttribute("value", "warm"), new XAttribute("vx", Number(f.Speed * MathF.Cos(radians))), new XAttribute("vy", Number(f.Speed * MathF.Sin(radians))));
                        }
                        return engine;
                    case CinematicEffectKind.Light:
                        if (item == null) throw new AuthoringException(marker.Id, "Свету нужен связанный светильник");
                        string light = "i" + item.Id.ToString(invariant);
                        if (!scene.Bindings.ContainsKey(light)) { bindings.Add(new XElement("Bind", new XAttribute("id", light), new XAttribute("kind", "Item"))); scene.Bindings[light] = item.Id; }
                        return new XElement("Light", new XAttribute("id", id), new XAttribute("target", light), new XAttribute("at", Number(f.StartAt)), new XAttribute("duration", Number(Positive(marker, f.Duration))),
                            new XAttribute("intensity", Number(f.Intensity)), new XAttribute("enabled", f.Enabled ? "true" : "false"));
                    case CinematicEffectKind.RotorWash:
                        if (vehicle == null && vehicles.Count != 1) throw new AuthoringException(marker.Id, "Зоне потока ротора нужен вертолёт: свяжите маркер с вертолётом");
                        return new XElement("Wash", new XAttribute("id", id), new XAttribute("target", AddVehicle(vehicle ?? vehicles[0])), new XAttribute("relativeTo", AnchorBinding),
                            new XAttribute("x", Number(at.X)), new XAttribute("y", Number(at.Y)), new XAttribute("radius", Number(f.Range)), new XAttribute("resource", Resource(marker)),
                            new XAttribute("at", Number(f.StartAt)), new XAttribute("duration", Number(Positive(marker, f.Duration))), new XAttribute("intensity", Number(f.Intensity)));
                    default:
                        bool attached = vehicle != null;
                        Vec2 offset = attached ? marker.SubjectOffset : at;
                        var e = new XElement(f.Effect == CinematicEffectKind.Sound ? "Sound" : "Particle", new XAttribute("id", id),
                            new XAttribute("target", attached ? AddVehicle(vehicle) : AnchorBinding), new XAttribute("x", Number(offset.X)), new XAttribute("y", Number(offset.Y)),
                            new XAttribute("resource", Resource(marker)), new XAttribute("at", Number(f.StartAt)), new XAttribute("duration", Number(Positive(marker, f.Duration))),
                            new XAttribute("intensity", Number(f.Intensity)), new XAttribute("radius", Number(f.Range)));
                        if (f.Effect == CinematicEffectKind.Sound && f.Loop) e.Add(new XAttribute("value", "loop"));
                        if (f.Effect == CinematicEffectKind.Particles && f.Speed > 0) {
                            float angle = f.Direction * (MathF.PI / 180f);
                            e.Add(new XAttribute("vx", Number(MathF.Cos(angle) * f.Speed)), new XAttribute("vy", Number(MathF.Sin(angle) * f.Speed)));
                        }
                        return e;
                }
            }

            private static float Positive(CinematicMarkerRecord marker, float value) => value > 0 ? value : throw new AuthoringException(marker.Id, "Длительность эффекта должна быть больше нуля");

            private static string Resource(CinematicMarkerRecord marker) {
                string value = marker.Effect.Resource.Trim().Replace('\\', '/');
                if (value.Length == 0 || value.Contains("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(value)) throw new AuthoringException(marker.Id, "Укажите ресурс эффекта: путь внутри мода или идентификатор частиц");
                return value;
            }

            private void AddEvents(XElement root) {
                var events = new XElement("Events");
                var list = new List<string>();
                foreach (string signal in awaitedSignals) if (!producedSignals.Contains(signal)) list.Add(signal);
                if (sceneMarker.Scene.Trigger == CinematicSceneTrigger.Signal && !CinematicPlan.IsId(sceneMarker.Scene.TriggerSignal))
                    throw new AuthoringException(sceneMarker.Id, "Сцене с запуском по сигналу нужно имя сигнала");
                list.Sort(StringComparer.Ordinal);
                foreach (string signal in list) events.Add(new XElement("Event", new XAttribute("id", signal)));
                if (list.Count > 0) root.Add(events);
            }

            private static string Number(float value) {
                if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
                return value.ToString("R", invariant);
            }
        }
    }
}
