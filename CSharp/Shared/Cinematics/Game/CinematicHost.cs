using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Barotrauma;
using ScpItdMod.Core;

namespace ScpItdMod.Cinematics {
    public static class CinematicHost {
        internal const int MaximumScenes = 8;
        internal static readonly Dictionary<string, CinematicPlan> Catalog = new Dictionary<string, CinematicPlan>(StringComparer.Ordinal);
        internal static readonly List<CinematicSession> Sessions = new List<CinematicSession>(MaximumScenes);
        internal static readonly CinematicRemovalRegistry Removals = new CinematicRemovalRegistry();
        internal static uint Epoch { get; private set; } = unchecked((uint)Environment.TickCount);
        private static uint nextId;
        private static readonly double[] samples = new double[256];
        private static int sampleIndex, sampleCount;
        private static long allocated;
        private static bool updating, resetting, starting;
        private static string pendingReset;
        private static double nextDepartureReport;
        public static event Action<ulong, string> Marker;
        public static event Action<ulong, CinematicStatus, string> Ended;
        internal static void Publish(ulong instance, string marker) { Marker?.Invoke(instance, marker); CinematicSubScenes.Signal(marker); }
        internal static void ReleaseEvents() { Marker = null; Ended = null; }
        internal static string LastFailure { get; private set; } = "";
        internal static bool Authority {
            get {
#if SERVER
                return true;
#else
                return GameMain.NetworkMember == null;
#endif
            }
        }
        public static ulong Start(string scene, IReadOnlyDictionary<string, ushort> bindings, ushort[] audience = null) => Start(scene, bindings, audience, null);
        internal static ulong ReserveId() {
            ulong id = ((ulong)Epoch << 32) | ++nextId;
            return (uint)id != 0 ? id : ((ulong)Epoch << 32) | ++nextId;
        }
        internal static ulong Start(string scene, IReadOnlyDictionary<string, ushort> bindings, ushort[] audience, CinematicPreparation preparation) {
            if (!Authority) throw new InvalidOperationException("Only the server can start world cinematics");
            if (resetting || pendingReset != null || starting) throw new InvalidOperationException("The cinematic host is transitioning");
            if (Screen.Selected is not GameScreen || GameMain.GameSession == null || GameMain.World == null) throw new InvalidOperationException("An active round is required");
            if (Sessions.Count >= MaximumScenes) throw new InvalidOperationException("Cinematic scene budget exhausted");
            if (!Catalog.TryGetValue(scene, out CinematicPlan plan)) throw new InvalidOperationException("Unknown or invalid cinematic " + scene);
            if (bindings == null) throw new ArgumentNullException(nameof(bindings));
            foreach (var binding in bindings) if (Removals.Contains(binding.Value)) throw new CinematicBindingUnavailableException("Scene entity was retired in this round: " + binding.Key);
            foreach (CinematicSession active in Sessions) if (active.Bindings.Plan.Id == scene) throw new InvalidOperationException("Scene is already running or finishing its physical departure");
            ushort[] recipients = audience == null || audience.Length == 0 ? null : (ushort[])audience.Clone();
            ulong[] keys = null;
#if SERVER
            if (recipients != null) {
                var selected = new List<ulong>();
                foreach (var client in GameMain.Server.ConnectedClients) {
                    if (client.Character == null) continue;
                    for (int a = 0; a < recipients.Length; a++) if (recipients[a] == client.Character.ID) { selected.Add(Access.Id(client)); break; }
                }
                if (selected.Count == 0) throw new InvalidOperationException("Selected cinematic audience has no connected players");
                keys = selected.ToArray();
            }
#endif
            starting = true;
            try {
                using var boarding = plan.Passengers == null ? null : new CinematicBoarding(plan, bindings);
                var bound = new CinematicBindings(plan, bindings);
                ulong id0 = preparation?.Owner ?? ReserveId();
                var session = new CinematicSession(id0, bound, recipients, keys, preparation);
                Sessions.Add(session);
                boarding?.Commit();
                ScpItdMod.Net.NetTrace.State(CinematicWire.Channel, "scene-start", id0, Epoch, 0, session.Sequencer.Revision, 0, plan.Hash, Timing.TotalTime);
#if SERVER
                CinematicServer.Changed();
#endif
                ModLog.Message("Cinematic started: " + scene + " instance=" + id0);
                return id0;
            } finally { starting = false; }
        }
        public static bool Stop(ulong id, string reason = "Stopped by director") {
            if (!Authority) return false;
            for (int i = 0; i < Sessions.Count; i++) if (Sessions[i].Id == id) { Sessions[i].Sequencer.Cancel(reason); if (!updating) Remove(i); return true; }
            return false;
        }
        public static bool Signal(ulong id, string signal) {
            if (!Authority || string.IsNullOrEmpty(signal) || signal.Length > 64) return false;
            for (int i = 0; i < Sessions.Count; i++) if (Sessions[i].Id == id && Sessions[i].Bindings.Plan.AcceptsSignal(signal)) { Sessions[i].Sequencer.Signal(signal); return true; }
            return false;
        }
        public static bool SkipPresentation(ulong id) {
            if (!Authority) return false;
            for (int i = 0; i < Sessions.Count; i++) if (Sessions[i].Id == id) {
                Sessions[i].Sequencer.SkipPresentation();
#if SERVER
                CinematicServer.Changed();
#endif
                return true;
            }
            return false;
        }
        internal static void Disconnect(Character character) {
            if (character == null) return;
            for (int i = Sessions.Count - 1; i >= 0; i--) {
                CinematicSession scene = Sessions[i];
                for (int b = 0; b < scene.Bindings.Entities.Length; b++) if (ReferenceEquals(scene.Bindings.Entities[b], character)) {
                    scene.Sequencer.Cancel("A scene actor disconnected");
                    if (!updating) Remove(i);
                    break;
                }
            }
        }
        internal static void Step(float dt) {
            if (!Authority) return;
            if (!(dt > 0) || !float.IsFinite(dt) || dt > .25f) { Reset("Unsafe simulation timestep"); return; }
            CinematicSubScenes.BeforeSimulation();
            if (Sessions.Count == 0) return;
            long before = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
            updating = true;
            try {
            for (int i = Sessions.Count - 1; i >= 0; i--) {
                CinematicSession session = Sessions[i]; uint revision = session.Sequencer.Revision, removed = session.Player.Lifetime.Removed, retiring = session.Player.Lifetime.Retiring;
                try { if (session.Sequencer.Status == CinematicStatus.Running || session.Player.Lifetime.Pending) session.Advance(dt); }
                catch (Exception error) { session.Player.Fail(error.GetType().Name + ": " + error.Message); }
                if (session.Sequencer.Status != CinematicStatus.Running && !(session.Sequencer.Status == CinematicStatus.Completed && session.Player.Lifetime.Pending)) Remove(i);
#if SERVER
                else if (revision != session.Sequencer.Revision || removed != session.Player.Lifetime.Removed || retiring != session.Player.Lifetime.Retiring) CinematicServer.Changed();
#endif
            }
            if (Timing.TotalTime >= nextDepartureReport) {
                nextDepartureReport = Timing.TotalTime + 5;
                foreach (CinematicSession s in Sessions) if (s.Sequencer.Status == CinematicStatus.Completed && s.Player.Lifetime.Pending) ModLog.Message(s.DepartureReport());
            }
            } finally { updating = false; if (pendingReset != null) { string reason = pendingReset; pendingReset = null; Reset(reason); } }
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            samples[sampleIndex] = (Stopwatch.GetTimestamp() - start) * (1e6 / Stopwatch.Frequency);
            sampleIndex = (sampleIndex + 1) % samples.Length; sampleCount = Math.Min(sampleCount + 1, samples.Length);
        }
        private static void Remove(int index) {
            CinematicSession session = Sessions[index]; Sessions.RemoveAt(index);
            ScpItdMod.Net.NetTrace.State(CinematicWire.Channel, "scene-end:" + session.Sequencer.Failure, session.Id, Epoch, 0, session.Sequencer.Revision,
                (int)session.Sequencer.Status, session.Bindings.Plan.Hash, Timing.TotalTime);
            if (session.Sequencer.Status == CinematicStatus.Failed) {
                LastFailure = session.Bindings.Plan.Id + ": " + session.Sequencer.Failure;
                for (int i = 0; i < session.Vehicles.Length; i++) if (session.Vehicles[i]?.Controller.Unsafe == true) LastFailure += "; " + session.Vehicles[i].Controller.Failure;
                ModLog.Error(LastFailure);
            }
            uint abandoned = session.Sequencer.Status == CinematicStatus.Failed ? session.Player.Lifetime.PendingMask : 0;
            for (int b = 0; abandoned != 0 && b < session.Vehicles.Length; b++) {
                if ((abandoned & (1u << b)) == 0 || session.Bindings.Entities[b] is not Barotrauma.Item { Removed: false }) continue;
                try { session.Vehicles[b]?.Dispose(); session.Vehicles[b] = null; session.Remove(b); ModLog.Error("Cinematic departure failed, the helicopter is removed instead of being left on the pad"); }
                catch (Exception error) { ModLog.Error("Cinematic failed-departure removal: " + error.Message); }
            }
            session.Dispose();
            try { Ended?.Invoke(session.Id, session.Sequencer.Status, session.Sequencer.Failure); }
            catch (Exception error) { ModLog.Error("Cinematic completion callback: " + error.Message); }
#if SERVER
            CinematicServer.Changed();
#endif
        }
        internal static void Reset(string reason) {
            if (resetting) return;
            if (updating) {
                pendingReset = reason;
                foreach (CinematicSession session in Sessions) session.Sequencer.Cancel(reason);
                return;
            }
            resetting = true;
            try {
#if SERVER
                CinematicActors.Reset();
#endif
                CinematicSubScenes.Reset();
                for (int i = Sessions.Count - 1; i >= 0; i--) { Sessions[i].Sequencer.Cancel(reason); Remove(i); }
                Epoch++; if (Epoch == 0) Epoch++;
            } finally { resetting = false; }
        }
        internal static void Reload() {
            if (Sessions.Count > 0) throw new InvalidOperationException("Stop active cinematics before recompiling scenes");
            CinematicSubScenes.RoundStart();
        }
        internal static void StartCommand(string[] args) {
            if (args.Length < 1) throw new ArgumentException("scpitd_cine_start scene [binding=id ...] [audience=characterId,...]");
            if (args.Length == 1 && CinematicSubScenes.Scenes.TryGetValue(args[0], out CinematicAuthoredScene authored)) { Start(args[0], authored.Bindings); return; }
            var bindings = new Dictionary<string, ushort>(StringComparer.Ordinal); ushort[] audience = null;
            for (int i = 1; i < args.Length; i++) {
                string[] pair = args[i].Split('=', 2);
                if (pair.Length != 2) throw new ArgumentException("Expected binding=id");
                if (pair[0] == "audience") {
                    string[] ids = pair[1].Split(',', StringSplitOptions.RemoveEmptyEntries); if (ids.Length > 64) throw new ArgumentException("Audience exceeds 64");
                    audience = new ushort[ids.Length];
                    for (int j = 0; j < ids.Length; j++) audience[j] = ushort.Parse(ids[j], CultureInfo.InvariantCulture);
                } else bindings.Add(pair[0], ushort.Parse(pair[1], CultureInfo.InvariantCulture));
            }
            Start(args[0], bindings, audience);
        }
        internal static void Status() {
            ModLog.Message("Cinematic ownership: actors=" + CinematicActors.Count + " lights=" + CinematicLightLease.Count);
            if (sampleCount > 0) {
                var copy = new double[sampleCount]; Array.Copy(samples, copy, sampleCount); Array.Sort(copy);
                ModLog.Message($"cinematic host scenes={Sessions.Count} cpu p50={copy[sampleCount / 2]:F2}us p95={copy[(sampleCount - 1) * 95 / 100]:F2}us p99={copy[(sampleCount - 1) * 99 / 100]:F2}us accumulated allocations={allocated}B; GPU time unmeasured");
            }
            foreach (CinematicSession s in Sessions) {
                ModLog.Message($"{s.Id} {s.Bindings.Plan.Id} stage={s.Bindings.Plan.Stages[s.Sequencer.Stage].Id} t={s.Sequencer.StageElapsed:F2}s skipped={s.Sequencer.PresentationSkipped}");
                foreach (CinematicTransition t in s.Bindings.Plan.Stages[s.Sequencer.Stage].Transitions) ModLog.Message(" transition " + (t.Negate ? "!" : "") + t.Condition + " measured=" + s.Sequencer.Condition(t.Condition, s.Player) + " min=" + s.Bindings.Plan.Stages[s.Sequencer.Stage].Minimum.ToString(CultureInfo.InvariantCulture));
                foreach (CinematicVehicleDirector v in s.Vehicles) if (v != null) ModLog.Message($" vehicle={((LiveRotorcraft)v.Airframe).Item.ID} waypoint={v.Controller.Goal?.Id}#{v.Controller.Waypoint} position={v.Observation.Position} velocity={v.Observation.Velocity} error={v.Controller.Error} headingError={v.Controller.HeadingError:F3} aligning={v.Controller.AligningHeading} rotor={v.Observation.RotorShare:F3} contact={v.Observation.Grounded} settling={v.Controller.SettlingContact} stable={v.Controller.StableSeconds:F2}s unsafe={v.Controller.Failure}");
            }
            if (LastFailure.Length > 0) ModLog.Message("Last cinematic failure: " + LastFailure);
        }
    }
}
