using System;
using System.Collections.Generic;
using System.Diagnostics;
using Barotrauma;
using FarseerPhysics;
using FarseerPhysics.Collision.Shapes;
using FarseerPhysics.Common;
using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;
using ScpItdMod.Core;

namespace ScpItdMod.Cinematics {
    internal struct CinematicPreviewFrame {
        internal double Time;
        internal ushort Stage;
        internal float StageTime;
        internal ulong Fired;
        internal uint Removed;
        internal bool HasCamera;
        internal Vec2 Camera;
        internal float Zoom, Roll;
        internal int WashStart, WashCount;
    }

    internal struct CinematicWashSample {
        internal Vec2 Ground;
        internal float Speed, Ratio;
    }

    internal sealed class CinematicPreview : ICinematicWorld, ICinematicRemovalWorld, IDisposable {
        internal const float StepSeconds = 1f / 60f;
        internal readonly CinematicAuthoredScene Scene;
        internal readonly CinematicPlan Plan;
        internal readonly Vec2 Origin;
        internal readonly int PoseFloats;
        internal readonly List<CinematicPreviewFrame> Frames = new List<CinematicPreviewFrame>(4096);
        internal readonly List<string> Notes = new List<string>();
        internal readonly int[] Vehicles;
        internal readonly object[] Ghosts;
        internal readonly CinematicCrewEstimate Crew;
        internal int GroundPolygons { get; private set; }
        internal bool Finished { get; private set; }
        internal string Outcome { get; private set; } = "";
        internal double MicrosecondsPerStep { get; private set; }
        private readonly BovCinematicAdapter bov;
        private readonly object[] sandboxes;
        private readonly CinematicVehicleDirector[] directors;
        private readonly CinematicScenePlayer player;
        private readonly Vec2 resolution;
        private readonly float[] pose;
        private float[] poses = new float[0];
        private readonly List<CinematicWashSample> wash = new List<CinematicWashSample>(4096);
        internal const int WashSamples = 9;
        private Vec2[] crewPositions = new Vec2[0];
        private byte[] crewPhases = new byte[0];
        private CinematicCameraRig rig;
        private double time, stepCost;
        private long steps;
        private bool disposed;

        internal CinematicPreview(CinematicAuthoredScene scene, Item sceneItem, Vec2 resolution) {
            Scene = scene; Plan = scene.Plan; this.resolution = resolution;
            Origin = new Vec2(sceneItem.WorldPosition.X, sceneItem.WorldPosition.Y);
            if (!BovCinematicAdapter.TryCreate(out bov, out string failure)) throw new InvalidOperationException(failure);
            PoseFloats = bov.PoseFloats; pose = new float[PoseFloats];
            sandboxes = new object[Plan.BindingCount]; directors = new CinematicVehicleDirector[Plan.BindingCount]; Ghosts = new object[Plan.BindingCount];
            var vehicles = new List<int>();
            Vector2 originSim = ConvertUnits.ToSimUnits(sceneItem.WorldPosition);
            try {
                for (int i = 0; i < Plan.BindingCount; i++) {
                    if (Plan.BindingAt(i).Kind != CinematicBindingKind.Rotorcraft) continue;
                    if (!scene.Bindings.TryGetValue(Plan.BindingAt(i).Id, out ushort id) || Entity.FindEntityByID(id) is not Item template)
                        throw new InvalidOperationException("Вертолёт " + Plan.BindingAt(i).Id + " не найден в редакторе");
                    object sandbox = bov.SandboxCreate(template, originSim, (uint)Plan.Hash) ?? throw new InvalidOperationException("BOVehicles не смог построить изолированную модель для " + template.Name);
                    sandboxes[i] = sandbox;
                    GroundPolygons = CaptureGround(sandbox, sceneItem.Submarine);
                    float payload = Plan.Passengers != null && Plan.Passengers.Vehicle == i ? scene.PreviewPayloadKg : 0f;
                    bov.SandboxConfigure(sandbox, payload, Vector2.Zero, true);
                    directors[i] = new CinematicVehicleDirector(new SandboxRotorcraft(bov, sandbox), (ulong)Plan.Hash | 1UL);
                    Ghosts[i] = bov.GhostCreate(sandbox);
                    vehicles.Add(i);
                }
            } catch { Dispose(); throw; }
            Vehicles = vehicles.ToArray();
            Crew = new CinematicCrewEstimate(Plan.BindingCount);
            if (Plan.Passengers != null) for (int i = 0; i < Plan.BindingCount; i++) if (Plan.BindingAt(i).Kind == CinematicBindingKind.Actor && Plan.BindingAt(i).Vehicle == Plan.Passengers.Vehicle) Crew.Seat(i);
            player = new CinematicScenePlayer(Plan, directors, this);
            Notes.Add("Физика вертолёта: та же модель BOVehicles, контроллер и мост, что в раунде; изолированный мир Farseer");
            Notes.Add(GroundPolygons > 0 ? "Опора: " + GroundPolygons + " многоугольников конструкций подлодки; рельеф уровня и подвижные предметы не учитываются" : "Опора: в подлодке нет конструкций с телом, вертолёту не на что сесть");
            Notes.Add("Ветер: штиль с порывами турбулентности BOV; в раунде добавляется ветер уровня");
            Notes.Add("Экипаж: приближённо (выход " + CinematicCrewEstimate.ClimbSeconds.ToString("0") + " с, ходьба по прямой); в раунде анимации BOV");
            Notes.Add("Камера: тот же чистый риг и телеметрия BOV, что в раунде");
        }

        private int CaptureGround(object sandbox, Submarine submarine) {
            int count = 0;
            Span<Vector2> polygon = stackalloc Vector2[Settings.MaxPolygonVertices];
            foreach (Structure structure in Structure.WallList) {
                if (structure.Removed || structure.Bodies == null || !ReferenceEquals(structure.Submarine, submarine)) continue;
                Vector2 frame = structure.Submarine?.SimPosition ?? Vector2.Zero;
                foreach (Body body in structure.Bodies) {
                    foreach (Fixture fixture in body.FixtureList) {
                        if (fixture.IsSensor || fixture.Shape is not PolygonShape shape || shape.Vertices.Count < 3) continue;
                        int n = Math.Min(shape.Vertices.Count, polygon.Length);
                        for (int v = 0; v < n; v++) polygon[v] = body.GetWorldPoint(shape.Vertices[v]) + frame;
                        bov.SandboxGround(sandbox, polygon.Slice(0, n), 0f);
                        count++;
                    }
                }
            }
            return count;
        }

        internal double Duration => Frames.Count * (double)StepSeconds;
        internal float UnitsPerMetre(int binding) => binding >= 0 && directors[binding] != null ? directors[binding].UnitsPerMetre : Units;
        internal float Units {
            get {
                float units = 100f;
                foreach (CinematicVehicleDirector director in directors) if (director != null) units = director.UnitsPerMetre;
                return units;
            }
        }

        internal void Advance(double budgetMilliseconds) {
            if (Finished || disposed) return;
            long start = Stopwatch.GetTimestamp(), limit = start + (long)(budgetMilliseconds * Stopwatch.Frequency / 1000.0);
            while (!Finished && Stopwatch.GetTimestamp() < limit) SimulateStep();
        }

        private void SimulateStep() {
            long begin = Stopwatch.GetTimestamp();
            player.Step(StepSeconds);
            foreach (int i in Vehicles) if (directors[i] != null && !bov.SandboxStep(sandboxes[i], StepSeconds)) player.Sequencer.Fail("Модель вертолёта разошлась численно: " + Plan.BindingAt(i).Id);
            time += StepSeconds;
            Record();
            stepCost += (Stopwatch.GetTimestamp() - begin) * (1e6 / Stopwatch.Frequency); steps++;
            MicrosecondsPerStep = stepCost / steps;
            CinematicStatus status = player.Sequencer.Status;
            if (status != CinematicStatus.Running && !(status == CinematicStatus.Completed && player.Lifetime.Pending) || time >= Plan.MaximumSeconds) {
                Finished = true;
                Outcome = status switch {
                    CinematicStatus.Completed => "Сцена завершена за " + time.ToString("0.0") + " с",
                    CinematicStatus.Running => "Достигнут предел длительности сцены",
                    _ => "Сцена прервана: " + player.Sequencer.Failure + " " + player.VehicleFailure()
                };
            }
        }

        private void Record() {
            int frame = Frames.Count;
            int stride = Vehicles.Length * PoseFloats;
            if (poses.Length < (frame + 1) * stride) Array.Resize(ref poses, Math.Max((frame + 1) * stride, poses.Length * 2));
            for (int v = 0; v < Vehicles.Length; v++) bov.SandboxPose(sandboxes[Vehicles[v]], poses.AsSpan(frame * stride + v * PoseFloats, PoseFloats));
            int crew = Crew.Members.Length;
            if (crewPositions.Length < (frame + 1) * crew) { Array.Resize(ref crewPositions, Math.Max((frame + 1) * crew, crewPositions.Length * 2)); Array.Resize(ref crewPhases, crewPositions.Length); }
            for (int c = 0; c < crew; c++) { crewPositions[frame * crew + c] = Crew.Members[c].Position; crewPhases[frame * crew + c] = (byte)Crew.Members[c].Phase; }
            CinematicSequencer q = player.Sequencer;
            var record = new CinematicPreviewFrame { Time = time, Stage = (ushort)q.Stage, StageTime = (float)q.StageElapsed, Fired = q.Fired, Removed = player.Lifetime.Removed };
            CinematicCue shot = Plan.Shot(q.Stage, q.StageElapsed, q.Fired);
            if (shot != null) {
                rig ??= new CinematicCameraRig(Origin, shot.Zoom);
                CinematicCameraSubject subject = Subject(shot.Target, frame);
                rig.Evaluate(shot, q.StageElapsed - shot.At, in subject, Anchor(shot.RelativeTo >= 0 ? shot.RelativeTo : shot.Target), resolution, false, false,
                    q.Elapsed, (uint)Plan.Hash, StepSeconds, out Vec2 camera, out float zoom, out float roll);
                record.HasCamera = true; record.Camera = camera; record.Zoom = zoom; record.Roll = roll;
            }
            record.WashStart = wash.Count;
            CinematicStage stage = Plan.StageAt(q.Stage);
            ulong mask = Plan.VisualMask(q.Stage, q.StageElapsed, q.Fired);
            for (int c = 0; c < stage.CueCount; c++) {
                CinematicCue cue = stage.CueAt(c);
                if (cue.Action != CinematicAction.Wash || (mask & (1UL << c)) == 0 || q.StageElapsed < cue.At || q.StageElapsed > cue.At + cue.Duration || directors[cue.Target] == null) continue;
                float units = directors[cue.Target].UnitsPerMetre;
                Vec2 centre = Anchor(cue.RelativeTo) + cue.Position;
                for (int k = 0; k < WashSamples; k++) {
                    float x = centre.X + ((float)k / (WashSamples - 1) - .5f) * 2f * cue.Radius * units;
                    if (!bov.SandboxOutwash(sandboxes[cue.Target], new Vector2(x, centre.Y) / units, out Vector4 raw)) continue;
                    CinematicOutwash flow = CinematicBovFormat.Flow(raw);
                    wash.Add(new CinematicWashSample { Ground = new Vec2(x, flow.Ground * units), Speed = flow.Speed * units, Ratio = flow.Ratio });
                }
            }
            record.WashCount = wash.Count - record.WashStart;
            Frames.Add(record);
        }

        internal CinematicWashSample Wash(int index) => wash[index];

        internal ReadOnlySpan<float> Pose(int frame, int vehicle) {
            int stride = Vehicles.Length * PoseFloats;
            return poses.AsSpan(frame * stride + vehicle * PoseFloats, PoseFloats);
        }

        internal Vec2 PoseDisplay(int frame, int vehicle) {
            ReadOnlySpan<float> p = Pose(frame, vehicle);
            return new Vec2(p[6] * 100f, p[7] * 100f);
        }

        internal CinematicCrewPhase CrewPhase(int frame, int binding) => (CinematicCrewPhase)crewPhases[frame * Crew.Members.Length + binding];
        internal Vec2 CrewPosition(int frame, int binding) => crewPositions[frame * Crew.Members.Length + binding];

        private CinematicCameraSubject Subject(int binding, int frame) {
            var subject = new CinematicCameraSubject { UnitsPerMetre = 100f };
            if (binding < 0) return subject;
            int vehicle = Array.IndexOf(Vehicles, binding);
            if (vehicle < 0) { subject.Position = Anchor(binding); return subject; }
            CinematicVehicleDirector director = directors[binding];
            if (director == null) return subject;
            subject = CinematicCameraSubject.FromFlight(in director.Observation);
            return subject;
        }

        public Vec2 Anchor(int binding) {
            if (binding < 0) return Vec2.Zero;
            if (Plan.BindingAt(binding).Kind == CinematicBindingKind.Anchor) return Origin;
            if (directors[binding] is CinematicVehicleDirector director) return director.Observation.Position * director.UnitsPerMetre;
            if (Plan.BindingAt(binding).Kind == CinematicBindingKind.Actor) return Crew.Members[binding].Position;
            return Scene.Bindings.TryGetValue(Plan.BindingAt(binding).Id, out ushort id) && Entity.FindEntityByID(id) is Item item ? new Vec2(item.WorldPosition.X, item.WorldPosition.Y) : Origin;
        }

        public bool Execute(CinematicCue cue) {
            switch (cue.Action) {
                case CinematicAction.Disembark:
                    if (directors[cue.RelativeTo] is not CinematicVehicleDirector vehicle) return true;
                    if (!vehicle.Observation.Grounded) return false;
                    Vec2 at = Anchor(cue.RelativeTo);
                    Crew.Disembark(cue.Target, new Vec2(at.X, at.Y + vehicle.Observation.Scale * 100f * LowestWheel(cue.RelativeTo)));
                    return true;
                case CinematicAction.Board: Crew.Board(cue.Target); return true;
                case CinematicAction.Move:
                    return Crew.Move(cue.Target, Anchor(cue.RelativeTo) + cue.Position, cue.Speed * Units, cue.Radius * Units, (float)cue.Duration);
                default: return true;
            }
        }

        private float LowestWheel(int binding) {
            if (!bov.SandboxObserve(sandboxes[binding], out _, out _, out _, out Vector4 geometry, out _)) return 0f;
            return geometry.Y;
        }

        public bool CanRemove(int binding, float radiusMetres, float distanceMetres) {
            if (rig == null || directors[binding] == null) return false;
            float units = directors[binding].UnitsPerMetre;
            CinematicCue shot = Plan.Shot(player.Sequencer.Stage, player.Sequencer.StageElapsed, player.Sequencer.Fired);
            float zoom = shot?.Zoom ?? 1f;
            return new CinematicViewport(rig.Listener, resolution / (2 * zoom)).Clear(Anchor(binding), radiusMetres * units, distanceMetres * units);
        }
        public void Remove(int binding) { }
        public bool Test(string condition) => condition == "actors.done" && Crew.Done;
        public void EnterStage(int stage) { }
        public string Step(float dt) => Crew.Step(dt);

        public void Dispose() {
            if (disposed) return;
            disposed = true;

            foreach (CinematicVehicleDirector director in directors) director?.Dispose();
            foreach (object ghost in Ghosts) if (ghost != null) bov?.GhostDispose(ghost);
        }
    }
}
