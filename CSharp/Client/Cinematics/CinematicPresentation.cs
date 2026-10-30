using System;
using Barotrauma;
using Barotrauma.Sounds;
using Barotrauma.Particles;
using Barotrauma.Items.Components;
using Barotrauma.Lights;
using Microsoft.Xna.Framework;
using ScpItdMod.Core;
using ScpItdMod.Motion;
using ScpItdMod.Thermal;
using ScpItdMod.Weather;

namespace ScpItdMod.Cinematics {
    internal sealed class CinematicPresentation : IDisposable {
        internal readonly CinematicBindings Bindings;
        internal readonly ulong Id;
        internal CinematicSnapshot State;
        internal double Age;
        private CinematicEffectStage effectsStage;
        internal bool Suppressed;
        private bool ownsOverlay;
        internal void OwnOverlay(bool owns) { if (owns && !ownsOverlay) overlayApplied = 0; ownsOverlay = owns; }
        private readonly float[] vehicleGain, vehicleFade;
        private ulong soundsStarted, overlayApplied;
        private readonly SoundChannel[] sounds = new SoundChannel[64];
        private readonly float[] emission = new float[64];
        private readonly ParticlePrefab[][] prefabs;
        private readonly ParticlePrefab tipPrefab, rollPrefab;
        private readonly float[] tipEmission = new float[64], rollEmission = new float[64];
        private readonly LightSource[] lights;
        private readonly float[] lightRanges;
        private readonly bool[] lightEnabled;
        private bool disposed;
        internal CinematicPresentation(CinematicBindings bindings, CinematicSnapshot state) {
            Bindings = bindings; Id = state.Id; State = state;
            vehicleGain = new float[bindings.Entities.Length]; vehicleFade = new float[bindings.Entities.Length];
            Array.Fill(vehicleGain, 1f); Array.Fill(vehicleFade, 1f);
            lights = new LightSource[bindings.Entities.Length]; lightRanges = new float[lights.Length]; lightEnabled = new bool[lights.Length];
            tipPrefab = ParticleManager.FindPrefab("scpitd_rotortip") ?? throw new InvalidOperationException("Cinematic particle prefab missing: scpitd_rotortip");
            rollPrefab = ParticleManager.FindPrefab("scpitd_rotorroll") ?? throw new InvalidOperationException("Cinematic particle prefab missing: scpitd_rotorroll");
            prefabs = new ParticlePrefab[bindings.Plan.StageCount][];
            for (int s = 0; s < prefabs.Length; s++) {
                CinematicStage stage = bindings.Plan.StageAt(s);
                prefabs[s] = new ParticlePrefab[stage.CueCount];
                for (int c = 0; c < stage.CueCount; c++) {
                    CinematicCue cue = stage.CueAt(c);
                    if (cue.Action == CinematicAction.Despawn) vehicleFade[cue.Target] = (float)cue.Duration;
                    if (cue.Action is CinematicAction.Particle or CinematicAction.Wash) {
                        prefabs[s][c] = ParticleManager.FindPrefab(cue.Resource) ?? throw new InvalidOperationException("Cinematic particle prefab missing: " + cue.Resource);
                        if (!prefabs[s][c].UseCollision) throw new InvalidOperationException("Cinematic material particles require a prefab with UseCollision: " + cue.Resource);
                    }
                    if (cue.Action == CinematicAction.Sound && SoundStore.Ready && SoundStore.File(cue.Resource) == null) throw new InvalidOperationException("Cinematic sound cannot be loaded: " + cue.Resource);
                }
            }
        }
        internal CinematicCue Shot() => Bindings.Plan.Shot(State.Stage, Age, State.Fired);
        private ulong EffectiveFired() => Bindings.Plan.VisualMask(State.Stage, Age, State.Fired);
        internal CinematicCameraSubject Subject(int binding) {
            var subject = new CinematicCameraSubject { UnitsPerMetre = Bindings.UnitsPerMetre };
            if (binding < 0) return subject;
            Vector2 at = Bindings.Position(binding), speed = Velocity(binding);
            subject.Position = new Vec2(at.X, at.Y); subject.Velocity = new Vec2(speed.X, speed.Y);
            if (Bindings.Bov != null && Bindings.Entities[binding] is Item { Removed: false } vehicle && Bindings.Bov.Read(vehicle, out CinematicFlightObservation state)) {
                subject = CinematicCameraSubject.FromFlight(in state);
            }
            return subject;
        }
        internal void Effects(float dt, Vector2 listener, Vector2 listenerVelocity) {
            if (disposed) return;
            for (int b = 0; b < vehicleGain.Length; b++) {
                if ((State.Retiring & (1u << b)) != 0) {
                    float age = State.FadeAges[b] + (float)Math.Max(0, Age - State.StageElapsed);
                    vehicleGain[b] = MathF.Min(vehicleGain[b], CinematicLifetime.FadeGain(age, vehicleFade[b]));
                } else vehicleGain[b] = MathF.Min(1, vehicleGain[b] + dt / vehicleFade[b]);
                if (Bindings.Plan.BindingAt(b).Kind == CinematicBindingKind.Rotorcraft && Bindings.Entities[b] is Item { Removed: false } vehicle)
                    Bindings.Bov?.PresentationGain(vehicle, vehicleGain[b]);
            }
            if (Suppressed || State.Hidden) { StopSounds(); RestoreLights(); return; }
            if (effectsStage.Enter(State.Revision, State.Stage)) { StopSounds(); Array.Clear(emission, 0, emission.Length); soundsStarted = 0; overlayApplied = 0; }
            int budget = 96;
            CinematicStage stage = Bindings.Plan.StageAt(State.Stage);
            ulong fired = EffectiveFired();
            for (int i = 0; i < stage.CueCount; i++) {
                CinematicCue cue = stage.CueAt(i);
                if ((fired & (1UL << i)) == 0 || cue.At > Age) continue;
                if (cue.Target >= 0 && Bindings.Entities[cue.Target]?.Removed != false) { CinematicSoundTails.Release(ref sounds[i]); continue; }
                if (cue.Action is CinematicAction.Letterbox or CinematicAction.Fade or CinematicAction.Title) {
                    if (!ownsOverlay) continue;
                    ulong overlayBit = 1UL << i;
                    if ((overlayApplied & overlayBit) == 0) { overlayApplied |= overlayBit; CinematicOverlay.Apply(cue, Age - cue.At); }
                    continue;
                }
                bool active = Age < cue.At + cue.Duration;
                if (!active) { CinematicSoundTails.Release(ref sounds[i]); continue; }
                Vector2 at = (cue.Action == CinematicAction.Wash ? Bindings.Position(cue.RelativeTo) : Bindings.Position(cue.Target)) + new Vector2(cue.Position.X, cue.Position.Y);
                float distance2 = Vector2.DistanceSquared(listener, at);
                if (cue.Action == CinematicAction.Light && !CinematicHost.Authority && Bindings.Entities[cue.Target] is Item lightItem && lightItem.GetComponent<LightComponent>()?.Light is LightSource source) {
                    if (lights[cue.Target] == null) { lights[cue.Target] = source; lightRanges[cue.Target] = source.Range; lightEnabled[cue.Target] = source.Enabled; }
                    source.Enabled = cue.Enabled;
                    source.Range = Math.Clamp(lightRanges[cue.Target] * cue.Intensity, 0, 4096);
                } else if (cue.Action == CinematicAction.Sound) {
                    ulong bit = 1UL << i;
                    if ((soundsStarted & bit) == 0) {
                        soundsStarted |= bit;
                        if (Age - cue.At < .3 || cue.Value == "loop") {
                            Sound sound = SoundStore.File(cue.Resource);
                            sounds[i] = sound?.Play(Math.Clamp(cue.Intensity, 0, 1), cue.Radius * Bindings.UnitsPerMetre, at);
                            if (sounds[i] != null) sounds[i].Looping = cue.Value == "loop";
                        }
                    }
                    if (sounds[i]?.IsPlaying == true) {
                        float attack = Math.Clamp((float)(Age - cue.At) / CinematicSoundTails.FadeSeconds, 0, 1);
                        float release = Math.Clamp((float)(cue.At + cue.Duration - Age) / CinematicSoundTails.FadeSeconds, 0, 1);
                        sounds[i].Gain = Math.Clamp(cue.Intensity, 0, 1) * MathF.Min(attack, release) * vehicleGain[cue.Target];
                        sounds[i].Position = new Vector3(at, 0);
                        Vector2 toListener = listener - at;
                        if (toListener.LengthSquared() > 1) toListener.Normalize();
                        Vector2 sourceVelocity = Velocity(cue.Target) / Bindings.UnitsPerMetre;
                        float observer = Vector2.Dot(listenerVelocity / Bindings.UnitsPerMetre, toListener), sourceSpeed = Vector2.Dot(sourceVelocity, toListener);
                        sounds[i].FrequencyMultiplier = Math.Clamp((343f - observer) / MathF.Max(85.75f, 343f - sourceSpeed), .5f, 2f);
                    }
                } else if (cue.Action == CinematicAction.Particle && budget > 0 && distance2 < 6000f * 6000f && GameMain.ParticleManager != null) {
                    float lod = Math.Clamp(1f - MathF.Sqrt(distance2) / 6000f, 0, 1);
                    emission[i] += MathF.Min(dt, .1f) * MathF.Min(64f, cue.Intensity * vehicleGain[cue.Target] * 20f) * lod;
                    int count = Math.Min(budget, (int)emission[i]); emission[i] -= MathF.Floor(emission[i]); budget -= count;
                    for (int n = 0; n < count; n++) GameMain.ParticleManager.CreateParticle(prefabs[State.Stage][i], at,
                        Velocity(cue.Target) + new Vector2(cue.Velocity.X, cue.Velocity.Y) * Bindings.UnitsPerMetre);
                } else if (cue.Action == CinematicAction.Wash && budget > 0 && GameMain.ParticleManager != null && Bindings.Bov != null && Bindings.Entities[cue.Target] is Item vehicle) {
                    float units = Bindings.UnitsPerMetre, reach = cue.Radius * units;
                    Vector2 centre = Bindings.Position(cue.Target);
                    Span<float> strengths = stackalloc float[CinematicWashMath.Samples];
                    Span<CinematicOutwash> flows = stackalloc CinematicOutwash[CinematicWashMath.Samples];
                    float total = 0;
                    for (int k = 0; k < strengths.Length; k++) {
                        float x = centre.X + ((k + .5f) / strengths.Length - .5f) * 2f * reach;
                        strengths[k] = 0;
                        if (!Bindings.Bov.Outwash(vehicle, new Vector2(x, centre.Y) / units, out Vector4 raw)) continue;
                        flows[k] = CinematicBovFormat.Flow(raw); strengths[k] = CinematicWashMath.Strength(in flows[k]); total += strengths[k];
                    }
                    EmitRotorFx(i, cue, vehicle, centre, strengths, flows, reach, dt, ref budget);
                    if (total <= 0) continue;
                    emission[i] += MathF.Min(dt, .1f) * CinematicWashMath.Emission(total / strengths.Length, cue.Intensity * vehicleGain[cue.Target]);
                    int count = Math.Min(budget, (int)emission[i]); emission[i] -= MathF.Floor(emission[i]); budget -= count;
                    float cell = 2f * reach / strengths.Length;
                    for (int n = 0; n < count; n++) {
                        uint h = (uint)(Age * 1000) * 2654435761u ^ (uint)(i * 40503 + n * 9973);
                        int k = CinematicWashMath.Pick(strengths, total, (n + .5f) / count);
                        float jitter = Hash(ref h), along = Hash(ref h), scatter = Hash(ref h);
                        float x = centre.X + ((k + .5f) / strengths.Length - .5f) * 2f * reach + (jitter - .5f) * cell;
                        Vec2 launch = CinematicWashMath.Launch(in flows[k], along, scatter) * units;
                        GameMain.ParticleManager.CreateParticle(prefabs[State.Stage][i], new Vector2(x, flows[k].Ground * units + Hash(ref h) * 20f), new Vector2(launch.X, launch.Y));
                    }
                }
            }
        }
        // The snow cue adds the ground vortex at the front of the wall jet; the air cue adds the condensing blade-tip vortices.
        private void EmitRotorFx(int i, CinematicCue cue, Item vehicle, Vector2 centre, ReadOnlySpan<float> strengths, ReadOnlySpan<CinematicOutwash> flows, float reach, float dt, ref int budget) {
            float units = Bindings.UnitsPerMetre, step = MathF.Min(dt, .1f);
            uint h = (uint)(Age * 1000) * 2246822519u ^ (uint)(i * 7919 + 17);
            if (cue.Resource == "scpitd_rotorsnow") {
                float cell = 2f * reach / strengths.Length;
                for (int side = 0; side < 2 && budget > 0; side++) {
                    int k = CinematicRotorFx.FrontIndex(strengths, side == 1);
                    if (k < 0) continue;
                    rollEmission[i] += step * CinematicRotorFx.RollsPerSecond * .5f * Math.Clamp(strengths[k] / CinematicWashMath.BaselineShare, 0f, 1f) * cue.Intensity * vehicleGain[cue.Target];
                    int count = Math.Min(budget, (int)rollEmission[i]); rollEmission[i] -= MathF.Floor(rollEmission[i]); budget -= count;
                    for (int n = 0; n < count; n++) {
                        float x = centre.X + ((k + .5f) / strengths.Length - .5f) * 2f * reach + (Hash(ref h) - .5f) * cell;
                        Vec2 launch = CinematicRotorFx.RollLaunch(in flows[k], Hash(ref h)) * units;
                        Particle p = GameMain.ParticleManager.CreateParticle(rollPrefab, new Vector2(x, flows[k].Ground * units + Hash(ref h) * 30f), new Vector2(launch.X, launch.Y));
                        if (p != null) p.Size *= .8f + Hash(ref h) * .9f;
                    }
                }
            } else if (cue.Resource == "scpitd_rotorair" && Bindings.Bov.Read(vehicle, out CinematicFlightObservation state) && Bindings.Bov.Envelope(vehicle, out Vector4 envelope, out _)) {
                float visibility = CinematicRotorFx.Visibility(ThermalBreath.Condensation(WeatherService.Current.Temperature, WeatherService.Current.Humidity));
                tipEmission[i] += step * 2f * CinematicRotorFx.TipRate(state.RotorShare, cue.Intensity * vehicleGain[cue.Target], visibility);
                int count = Math.Min(budget, (int)tipEmission[i]); tipEmission[i] -= MathF.Floor(tipEmission[i]); budget -= count;
                Vector2 hub = Bindings.Position(cue.Target) + new Vector2(0, envelope.Y * units);
                float radius = state.RotorRadius * units, tilt = state.Tilt;
                Vector2 vehicleVelocity = Velocity(cue.Target);
                for (int n = 0; n < count; n++) {
                    float side = (n & 1) == 0 ? -1f : 1f;
                    Vec2 launch = CinematicRotorFx.TipLaunch(state.InducedVelocity, (float)Age + n * .013f, side, state.RotorShare) * units;
                    Vector2 tip = hub + new Vector2(side * radius * MathF.Cos(tilt), side * radius * MathF.Sin(tilt));
                    Particle p = GameMain.ParticleManager.CreateParticle(tipPrefab, tip, vehicleVelocity * .6f + new Vector2(launch.X, launch.Y));
                    if (p != null) p.Size *= .9f + Hash(ref h) * .6f;
                }
            }
        }
        private static float Hash(ref uint state) { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return (state & 0xFFFFFF) * (1f / 16777216f); }
        internal Vector2 Velocity(int binding) {
            if (binding < 0) return Vector2.Zero;
            if (Bindings.Entities[binding] is Item { Removed: false } item && item.body != null) return (item.body.LinearVelocity + (item.Submarine?.Velocity ?? Vector2.Zero)) * 100f;
            if (Bindings.Entities[binding] is Character { Removed: false } actor && actor.AnimController?.Collider != null) return (actor.AnimController.Collider.LinearVelocity + (actor.Submarine?.Velocity ?? Vector2.Zero)) * 100f;
            return Vector2.Zero;
        }
        internal void StopSounds() { for (int i = 0; i < sounds.Length; i++) { CinematicSoundTails.Release(ref sounds[i]); } }
        internal void RestoreLights() {
            for (int i = 0; i < lights.Length; i++) if (lights[i] != null) { if (Bindings.Entities[i] is Item { Removed: false }) { lights[i].Range = lightRanges[i]; lights[i].Enabled = lightEnabled[i]; } lights[i] = null; }
        }
        public void Dispose() { if (disposed) return; disposed = true; StopSounds(); RestoreLights(); }
    }

    internal sealed class CinematicCameraLease : IDisposable {
        internal readonly Camera Camera;
        private readonly float originalZoom, originalRotation, originalAngular, originalOffset, originalShake, originalLock;
        private readonly bool originalFreeze, originalLos;
        private readonly LightManager lighting;
        private readonly LosMode originalLosMode;
        private readonly Vector3 originalListener;
        private readonly CinematicCameraRig rig;
        private readonly CinematicPresentation view;
        private bool disposed;
        internal Vector2 Velocity => new Vector2(rig.Velocity.X, rig.Velocity.Y);
        internal CinematicCameraLease(Camera camera, CinematicPresentation view) {
            Camera = camera; this.view = view;
            originalZoom = camera.Zoom; originalRotation = camera.Rotation;
            originalAngular = camera.AngularVelocity; originalOffset = camera.OffsetAmount; originalShake = camera.Shake; originalLock = camera.MovementLockTimer; originalFreeze = camera.Freeze;
            originalListener = GameMain.SoundManager?.ListenerPosition ?? Vector3.Zero;
            lighting = GameMain.LightManager;
            if (lighting != null) {
                originalLos = lighting.LosEnabled; originalLosMode = lighting.LosMode;
                lighting.LosEnabled = false; lighting.LosMode = LosMode.None;
            }
            rig = new CinematicCameraRig(new Vec2(camera.Position.X, camera.Position.Y), camera.Zoom);
        }
        internal bool Prepare(float frameSeconds) {
            if (disposed || !view.Bindings.Alive) return false;
            CinematicCue current = view.Shot();
            if (current == null || current.Target >= 0 && view.Bindings.Entities[current.Target]?.Removed != false) return false;
            CinematicCameraSubject subject = view.Subject(current.Target);
            Vector2 anchor = view.Bindings.Position(current.RelativeTo >= 0 ? current.RelativeTo : current.Target);
            rig.Evaluate(current, view.Age - current.At, in subject, new Vec2(anchor.X, anchor.Y), new Vec2(Camera.Resolution.X, Camera.Resolution.Y), false,
                MotionClient.Runtime.ReducedMotion, view.State.Elapsed + view.Age - view.State.StageElapsed, (uint)view.Bindings.Plan.Hash, frameSeconds, out Vec2 position, out float zoom, out float roll);
            Camera.Position = new Vector2(position.X, position.Y); Camera.Zoom = zoom;
            Camera.Rotation = originalRotation + roll; Camera.AngularVelocity = 0; Camera.Shake = 0;
            return true;
        }
        internal void Listener() {
            if (GameMain.SoundManager != null) GameMain.SoundManager.ListenerPosition = new Vector3(rig.Listener.X, rig.Listener.Y, -100f / Camera.Zoom);
            view.Bindings.Bov?.Listener(view.Id, Velocity / 100f, true);
        }
        public void Dispose() {
            if (disposed) return;
            disposed = true;
            try { view.Bindings.Bov?.Listener(view.Id, Vector2.Zero, false); }
            finally {
                if (lighting != null && ReferenceEquals(lighting, GameMain.LightManager)) { lighting.LosEnabled = originalLos; lighting.LosMode = originalLosMode; }
                Camera.TargetPos = Camera.Position; Camera.Zoom = originalZoom; Camera.Rotation = originalRotation;
                Camera.AngularVelocity = originalAngular; Camera.OffsetAmount = originalOffset; Camera.Shake = originalShake; Camera.Freeze = originalFreeze; Camera.MovementLockTimer = originalLock;
                if (GameMain.SoundManager != null) GameMain.SoundManager.ListenerPosition = new Vector3(Camera.Position, originalListener.Z);
                Camera.UpdateTransform(false);
            }
        }
    }
}
