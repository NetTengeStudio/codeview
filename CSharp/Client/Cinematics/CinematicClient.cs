using System;
using System.Collections.Generic;
using System.Globalization;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;
using ScpItdMod.Net;

namespace ScpItdMod.Cinematics {
    internal static class CinematicClient {
        private static readonly CinematicReplica replica = new CinematicReplica();
        private static readonly Dictionary<ulong, CinematicPresentation> views = new Dictionary<ulong, CinematicPresentation>();
        private static readonly HashSet<ulong> hidden = new HashSet<ulong>();
        private static readonly HashSet<ulong> current = new HashSet<ulong>();
        private static readonly HashSet<ulong> waitingBindings = new HashSet<ulong>();
        private static bool bindingsDirty, restoringCamera;
        private static readonly List<ulong> expired = new List<ulong>();
        private static readonly WireWriter writer = new WireWriter(2048);
        private static CinematicCameraLease camera;
        private static CinematicPresentation selected;
        private static object connection;
        private static double nextHello, nextViewport, previousMotion, nextReady;
        private static float framePeriod = 1f / 60f;
        private static bool hudHidden;
        private static long sentBytes, receivedBytes;
        private static CinematicReadiness readiness;
        private static bool readyPending;
        private static uint readyEpoch;
        private static ulong readyCatalog;
        private static uint readySession, firstReadySequence, lastReadySequence;
        private static bool readySent;
        internal static bool BlocksInput => camera != null;
        internal static void Attach(Harmony harmony) {
            Tagged.Changed += BindingsChanged;
            Humans.Changed += BindingsChanged;
            ModNet.ReceiveSpan(CinematicWire.Channel, CinematicWire.MaximumPacket, Receive);
            CinematicEditor.Attach(harmony);
            harmony.Patch(AccessTools.Method(typeof(GameMain), "Update", new[] { typeof(GameTime) }), postfix: ModuleHost.Bind(typeof(CinematicClient), nameof(Pump)));
            harmony.Patch(AccessTools.Method(typeof(GameScreen), nameof(GameScreen.Update), new[] { typeof(double) }), postfix: ModuleHost.Bind(typeof(CinematicClient), nameof(Update)));
            harmony.Patch(AccessTools.Method(typeof(GameScreen), nameof(GameScreen.Update), new[] { typeof(double) }), postfix: ModuleHost.Bind(typeof(CinematicClient), nameof(SyncHud)));
            harmony.Patch(AccessTools.Method(typeof(Camera), nameof(Camera.MoveCamera)), prefix: ModuleHost.Bind(typeof(CinematicClient), nameof(MoveCamera)));
            harmony.Patch(AccessTools.Method(typeof(Camera), nameof(Camera.UpdateTransform), new[] { typeof(bool), typeof(bool) }),
                prefix: ModuleHost.Bind(typeof(CinematicClient), nameof(Transform)), postfix: ModuleHost.Bind(typeof(CinematicClient), nameof(Listener)));
            harmony.Patch(AccessTools.Method(typeof(GUI), nameof(GUI.Draw), new[] { typeof(Camera), typeof(SpriteBatch) }), postfix: ModuleHost.Bind(typeof(CinematicOverlay), nameof(CinematicOverlay.Draw)));
            harmony.Patch(AccessTools.Method(typeof(GameScreen), nameof(GameScreen.Draw), new[] { typeof(double), typeof(GraphicsDevice), typeof(SpriteBatch) }),
                finalizer: ModuleHost.Bind(typeof(CinematicClient), nameof(EndFrame)));
        }
        internal static void Request(byte operation, string[] args) {
            if (args.Length > 20) throw new ArgumentException("Too many cinematic arguments");
            CinematicWire.Header(writer, operation); writer.U8((byte)args.Length);
            for (int i = 0; i < args.Length; i++) { if (args[i].Length > 128) throw new ArgumentException("Cinematic argument too long"); writer.Text(args[i]); }
            sentBytes += writer.Length; ModNet.SendBytes(CinematicWire.Channel, writer.Span, DeliveryMethod.Reliable);
        }
        private static void Receive(ReadOnlySpan<byte> payload) {
            receivedBytes += payload.Length;
            if (payload.Length >= 2 && payload[0] == CinematicWire.Version && payload[1] == CinematicWire.ReadyAccepted) {
                CinematicRoster currentRoster = replica.Latest;
                if (readyPending && readySent && currentRoster != null && CinematicWire.ReadReadyAccepted(payload, out uint epoch, out ulong catalog, out uint session, out uint sequence) &&
                    epoch == readyEpoch && catalog == readyCatalog && session == readySession &&
                    unchecked(sequence - firstReadySequence) <= unchecked(lastReadySequence - firstReadySequence) &&
                    readiness.Accept(epoch, catalog, currentRoster.Epoch, CinematicSubScenes.CatalogHash)) {
                    readyPending = false;
                    NetTrace.State(CinematicWire.Channel, "ready-confirm", 0, epoch, sequence, session, 0, catalog);
                }
                return;
            }
            if (payload.Length >= 2 && payload[0] == CinematicWire.Version && payload[1] == 7) {
                var reader = new WireReader(payload.Slice(2)); string message = reader.Text(512);
                if (!reader.Failed && reader.End) ModLog.Error("Cinematic server: " + message);
                return;
            }
            if (!CinematicWire.ReadRoster(payload, out CinematicRoster roster)) return;
            bool accepted = replica.Accept(roster, Timing.TotalTime);
            if (NetTrace.Enabled) {
                NetTrace.State(CinematicWire.Channel, accepted ? "roster-accept" : "roster-stale", 0, roster.Epoch, roster.Sequence, 0, roster.Scenes.Length, CinematicWire.PayloadHash(payload), roster.ServerTime);
                if (accepted) foreach (CinematicSnapshot scene in roster.Scenes)
                    NetTrace.State(CinematicWire.Channel, "scene-accept", scene.Id, roster.Epoch, roster.Sequence, scene.Revision, scene.Stage,
                        CinematicWire.StateHash(scene.Revision, scene.Stage, scene.Fired, scene.Removed, scene.Retiring), roster.ServerTime);
            }
            if (accepted) { readyPending = true; bindingsDirty = true; }
        }
        private static void BindingsChanged() => bindingsDirty = true;
        internal static void DetachBindings() { Tagged.Changed -= BindingsChanged; Humans.Changed -= BindingsChanged; }
        internal static void CatalogReady() { readyPending = true; bindingsDirty = true; }
        private static void Ready() {
            CinematicRoster roster = replica.Latest;
            ulong catalog = CinematicSubScenes.CatalogHash;
            if (roster == null || catalog == 0) return;
            if (readiness.Ready(roster.Epoch, catalog)) { readyPending = false; return; }
            if (readyEpoch != roster.Epoch || readyCatalog != catalog) { readyEpoch = roster.Epoch; readyCatalog = catalog; nextReady = 0; readySent = false; }
            if (Timing.TotalTime < nextReady) return;
            nextReady = Timing.TotalTime + 1;
            CinematicWire.WriteReady(writer, CinematicWire.ReadyRequest, roster.Epoch, catalog);
            sentBytes += writer.Length;
            if (ModNet.TrySendBytes(CinematicWire.Channel, writer.Span, DeliveryMethod.Reliable, out uint session, out uint sequence)) {
                if (!readySent || readySession != session || unchecked(sequence - firstReadySequence) >= 0x80000000u) {
                    readySession = session; firstReadySequence = sequence; readySent = true;
                }
                lastReadySequence = sequence;
                NetTrace.State(CinematicWire.Channel, "ready-send", 0, roster.Epoch, sequence, session, 0, catalog);
            }
        }
        private static void Pump() {
            if (Screen.Selected is not GameScreen || !ReferenceEquals(connection, GameMain.Client)) {
                if (views.Count > 0 || camera != null || replica.Latest != null) Reset();
                connection = GameMain.Client;
                if (Screen.Selected is not GameScreen && CinematicHost.Authority && CinematicHost.Sessions.Count > 0) CinematicHost.Reset("Game screen left");
            }
        }
        // While a cinematic camera holds the view, the vanilla and vehicle HUDs stay hidden; they all honour GUI.DisableHUD.
        private static void SyncHud(double __0) {
            bool wanted = camera != null;
            if (wanted && !GUI.DisableHUD) { GUI.DisableHUD = true; hudHidden = true; }
            else if (!wanted && hudHidden) { GUI.DisableHUD = false; hudHidden = false; }
        }
        private static void Update(double __0) {
            if (!ReferenceEquals(connection, GameMain.Client)) { Reset(); connection = GameMain.Client; }
            current.Clear();
            bool retryBindings = bindingsDirty;
            bindingsDirty = false;
            if (CinematicHost.Authority) {
                foreach (CinematicSession session in CinematicHost.Sessions) {
                    current.Add(session.Id);
                    if (!views.TryGetValue(session.Id, out CinematicPresentation view)) {
                        if (hidden.Contains(session.Id)) continue;
                        var snapshot = Snapshot(session);
                        try { view = new CinematicPresentation(session.Bindings, snapshot); views.Add(session.Id, view); }
                        catch (Exception error) { hidden.Add(session.Id); ModLog.Once("Cinematic presentation: " + error.Message); continue; }
                    }
                    CopyState(session, view.State); view.Age = session.Sequencer.StageElapsed;
                }
            } else {
                if (readyPending) Ready();
                if (Timing.TotalTime >= nextViewport && GameMain.GameScreen?.Cam is Camera observerCamera) {
                    nextViewport = Timing.TotalTime + .2;
                    float diagonal = MathF.Sqrt((float)observerCamera.Resolution.X * observerCamera.Resolution.X + (float)observerCamera.Resolution.Y * observerCamera.Resolution.Y) / (2f * observerCamera.Zoom);
                    CinematicWire.Header(writer, 9); writer.F32(observerCamera.Position.X); writer.F32(observerCamera.Position.Y); writer.F32(diagonal); writer.F32(diagonal);
                    sentBytes += writer.Length; ModNet.SendBytes(CinematicWire.Channel, writer.Span, DeliveryMethod.Unreliable);
                }
                if (Timing.TotalTime >= nextHello) {
                    nextHello = Timing.TotalTime + 10;
                    CinematicWire.Header(writer, 0); writer.U64(BitConverter.DoubleToUInt64Bits(Timing.TotalTime));
                    sentBytes += writer.Length; ModNet.SendBytes(CinematicWire.Channel, writer.Span, DeliveryMethod.Reliable);
                }
                CinematicRoster roster = replica.Latest;
                if (roster != null && Timing.TotalTime - replica.ReceivedAt < 6) foreach (CinematicSnapshot state in roster.Scenes) {
                    current.Add(state.Id);
                    if (!views.TryGetValue(state.Id, out CinematicPresentation view)) {
                        if (hidden.Contains(state.Id)) continue;
                        if (waitingBindings.Contains(state.Id) && !retryBindings) continue;
                        if (CinematicSubScenes.CatalogHash == 0) { waitingBindings.Add(state.Id); continue; }
                        try {
                            if (!CinematicHost.Catalog.TryGetValue(state.Plan, out CinematicPlan plan) || plan.Hash != state.PlanHash || state.Bindings.Length != plan.BindingCount || state.Stage >= plan.StageCount)
                                throw new InvalidOperationException("Cinematic definition differs from the server: " + state.Plan);
                            var bindings = new Dictionary<string, ushort>(StringComparer.Ordinal);
                            for (int i = 0; i < state.Bindings.Length; i++) bindings.Add(plan.Bindings[i].Id, state.Bindings[i]);
                            view = new CinematicPresentation(new CinematicBindings(plan, bindings, false, state.Removed, state.UnitsPerMetre), state); views.Add(state.Id, view); waitingBindings.Remove(state.Id);
                        } catch (CinematicBindingUnavailableException error) { waitingBindings.Add(state.Id); ModLog.Once("Cinematic presentation waiting for entities: " + error.Message); continue; }
                        catch (Exception error) { hidden.Add(state.Id); ModLog.Once("Cinematic presentation rejected: " + error.Message); continue; }
                    }
                    if (state.Stage >= view.Bindings.Plan.StageCount || state.PlanHash != view.Bindings.Plan.Hash) { hidden.Add(state.Id); continue; }
                    view.Bindings.Retire(state.Removed); view.State = state; view.Age = state.StageElapsed + replica.Age(Timing.TotalTime);
                }
            }
            expired.Clear();
            foreach (var pair in views) if (!current.Contains(pair.Key)) expired.Add(pair.Key);
            foreach (ulong id in expired) { if (ReferenceEquals(selected, views[id])) ReleaseCamera(); views[id].Dispose(); views.Remove(id); hidden.Remove(id); waitingBindings.Remove(id); }
            expired.Clear();
            foreach (ulong id in waitingBindings) if (!current.Contains(id)) expired.Add(id);
            foreach (ulong id in expired) waitingBindings.Remove(id);
            hidden.RemoveWhere(NotCurrent);
            foreach (CinematicPresentation view in views.Values) { view.Suppressed = view.State.Hidden || hidden.Contains(view.Id); if (view.Suppressed) { view.StopSounds(); view.RestoreLights(); } }
            bool visible = false;
            foreach (CinematicPresentation view in views.Values) visible |= !view.State.Hidden && !hidden.Contains(view.Id);
            if (visible) overlayShown = true;
            else if (overlayShown) { overlayShown = false; CinematicOverlay.Clear(); }
            CinematicPresentation next = null;
            foreach (CinematicPresentation view in views.Values) if (!view.State.Hidden && !hidden.Contains(view.Id) && view.Bindings.Alive && view.Shot() != null && (next == null || view.Id > next.Id)) next = view;
            ulong owner = next?.Id ?? 0;
            if (owner != overlayOwner) { CinematicOverlay.Reset(); overlayOwner = owner; }
            foreach (CinematicPresentation view in views.Values) view.OwnOverlay(ReferenceEquals(view, next));
            if (!ReferenceEquals(next, selected)) {
                ReleaseCamera(); selected = next;
                if (selected != null && GameMain.GameScreen?.Cam != null) camera = new CinematicCameraLease(GameMain.GameScreen.Cam, selected);
            }
        }
        private static CinematicSnapshot Snapshot(CinematicSession session) {
            var state = new CinematicSnapshot { Id = session.Id, Plan = session.Bindings.Plan.Id, PlanHash = session.Bindings.Plan.Hash, Bindings = new ushort[session.Bindings.Entities.Length], FadeAges = new float[session.Bindings.Entities.Length] };
            for (int i = 0; i < state.Bindings.Length; i++) state.Bindings[i] = session.Bindings.Id(i);
            CopyState(session, state); return state;
        }
        private static void CopyState(CinematicSession session, CinematicSnapshot state) {
            CinematicSequencer q = session.Sequencer;
            state.Stage = (ushort)q.Stage; state.StageElapsed = (float)q.StageElapsed; state.Elapsed = (float)q.Elapsed; state.Revision = q.Revision;
            for (int i = 0; i < state.FadeAges.Length; i++) state.FadeAges[i] = session.Player.Lifetime.FadeAge(i);
            state.Fired = q.Fired; state.Removed = session.Player.Lifetime.Removed; state.Retiring = session.Player.Lifetime.Retiring; state.UnitsPerMetre = session.Bindings.UnitsPerMetre; state.Hidden = q.Status != CinematicStatus.Running || q.PresentationSkipped;
        }
        private static bool MoveCamera(Camera __instance) => camera == null || !ReferenceEquals(camera.Camera, __instance);
        private static void Transform(Camera __instance, ref bool __0, ref bool __1) {
            if (restoringCamera || !ReferenceEquals(GameMain.GameScreen?.Cam, __instance)) return;
            try {
                double time = Motion.MotionClient.Runtime.Time;
                float dt = (float)Math.Clamp(time - previousMotion, 0, .1); previousMotion = time;
                if (CinematicHost.Authority ? GameMain.GameSession?.GameMode?.Paused == true : replica.Latest?.Paused == true) dt = 0;
                if (dt > 0) framePeriod = dt;
                if (camera != null) {
                    if (camera.Prepare(framePeriod)) { __0 = false; __1 = false; }
                    else ReleaseCamera();
                }
                CinematicSoundTails.Step(dt);
                foreach (CinematicPresentation view in views.Values) view.Effects(dt, __instance.Position, camera?.Velocity ?? Vector2.Zero);
            } catch (Exception error) { ModLog.Once("Cinematic camera recovered: " + error.Message); if (selected != null) hidden.Add(selected.Id); ReleaseCamera(); }
        }
        private static void Listener(Camera __instance) { if (camera != null && ReferenceEquals(camera.Camera, __instance)) camera.Listener(); }
        private static Exception EndFrame(Exception __exception) { foreach (CinematicPresentation view in views.Values) view.RestoreLights(); return __exception; }
        internal static void Skip(string[] args) {
            ulong id = selected?.Id ?? 0;
            if (args.Length > 0 && !ulong.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out id)) { ModLog.Error("Invalid cinematic instance id"); return; }
            if (id == 0) return;
            hidden.Add(id);
            if (views.TryGetValue(id, out CinematicPresentation view)) view.StopSounds();
            if (view != null) view.RestoreLights();
            if (selected?.Id == id) ReleaseCamera();
            if (!CinematicHost.Authority) { CinematicWire.Header(writer, 6); writer.U64(id); sentBytes += writer.Length; ModNet.SendBytes(CinematicWire.Channel, writer.Span, DeliveryMethod.Reliable); }
        }
        private static void ReleaseCamera() {
            var owned = camera; camera = null; selected = null;
            restoringCamera = true;
            try { owned?.Dispose(); }
            catch (Exception error) { ModLog.Error("Cinematic camera release: " + error.Message); }
            finally {
                restoringCamera = false;
                if (hudHidden) { GUI.DisableHUD = false; hudHidden = false; }
            }
        }
        private static bool overlayShown;
        private static ulong overlayOwner;
        private static bool NotCurrent(ulong id) => !current.Contains(id);
        internal static void Reset() {
            CinematicOverlay.Reset(); CinematicSoundTails.Reset(); overlayShown = false; overlayOwner = 0;
            ReleaseCamera(); foreach (CinematicPresentation view in views.Values) view.Dispose(); CinematicSoundTails.Reset(); views.Clear(); hidden.Clear(); current.Clear(); expired.Clear(); waitingBindings.Clear(); bindingsDirty = false;
            replica.Reset(); nextHello = 0; nextViewport = 0; previousMotion = Motion.MotionClient.Runtime.Time;
            readiness = default; readyPending = readySent = false; readyEpoch = readySession = firstReadySequence = lastReadySequence = 0; readyCatalog = 0; nextReady = 0;
            framePeriod = 1f / 60f;
        }
        internal static void Status() => ModLog.Message($"cinematic client views={views.Count} camera={selected?.Id ?? 0} sent={sentBytes}B received={receivedBytes}B rtt={replica.RoundTripSeconds:F3}s motion properties={Motion.MotionClient.Runtime.Statistics().Properties}; GPU time unmeasured");
    }
}
