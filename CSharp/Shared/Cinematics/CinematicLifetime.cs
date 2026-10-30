using System;
using System.Collections.Generic;
using ScpItdMod.Core;

namespace ScpItdMod.Cinematics {
    public readonly struct CinematicViewport {
        public readonly Vec2 Centre, HalfSize;
        public CinematicViewport(Vec2 centre, Vec2 halfSize) { Centre = centre; HalfSize = halfSize; }
        public bool Clear(Vec2 point, float radius, float minimumDistance) {
            Vec2 delta = point - Centre;
            if (delta.LengthSquared() <= minimumDistance * minimumDistance) return false;
            float x = MathF.Max(0, MathF.Abs(delta.X) - HalfSize.X), y = MathF.Max(0, MathF.Abs(delta.Y) - HalfSize.Y);
            return x * x + y * y > radius * radius;
        }
    }

    public readonly struct CinematicObserver {
        public const float MaximumAge = 1f;
        public readonly CinematicViewport View;
        public readonly double ReportedAt;
        public CinematicObserver(CinematicViewport view, double reportedAt) { View = view; ReportedAt = reportedAt; }
        public bool Fresh(double now) => ReportedAt > 0 && now >= ReportedAt && now - ReportedAt <= MaximumAge;
        public bool Clear(double now, Vec2 point, float radius, float minimumDistance) => ReportedAt > 0 && now >= ReportedAt && now - ReportedAt <= MaximumAge && View.Clear(point, radius, minimumDistance);
    }

    public interface ICinematicRemovalWorld {
        bool CanRemove(int binding, float radiusMetres, float distanceMetres);
        void Remove(int binding);
    }

    public sealed class CinematicRemovalRegistry {
        private readonly HashSet<ushort> removed = new HashSet<ushort>();
        public bool Contains(ushort id) => removed.Contains(id);
        public bool Record(ushort id) => id != 0 && removed.Add(id);
        public void Reset() => removed.Clear();
    }

    public sealed class CinematicLifetime {
        public const float ClearSeconds = 1f;
        private readonly CinematicCue[] requests;
        private readonly float[] clearAge;
        public uint Removed { get; private set; }
        public uint Retiring { get; private set; }
        public bool Pending { get; private set; }
        public uint PendingMask { get { uint mask = 0; for (int i = 0; i < requests.Length; i++) if (requests[i] != null) mask |= 1u << i; return mask; } }
        public float FadeAge(int binding) { float age = clearAge[binding]; return (Retiring & (1u << binding)) == 0 ? 0 : MathF.Max(0, age - ClearSeconds); }
        public static float FadeGain(float age, float seconds) => Math.Clamp(1f - MathF.Max(0, age) / seconds, 0, 1);
        public CinematicLifetime(int bindings) { if ((uint)bindings > CinematicPlan.MaximumBindings) throw new ArgumentOutOfRangeException(nameof(bindings)); requests = new CinematicCue[bindings]; clearAge = new float[bindings]; }
        public bool Request(CinematicCue cue) {
            if (cue == null || cue.Action != CinematicAction.Despawn || (uint)cue.Target >= requests.Length) throw new ArgumentException("A validated Despawn cue is required", nameof(cue));
            uint bit = 1u << cue.Target;
            if ((Removed & bit) != 0) return true;
            if (requests[cue.Target] != null) return requests[cue.Target].Value == cue.Value && requests[cue.Target].Radius == cue.Radius && requests[cue.Target].Duration == cue.Duration;
            requests[cue.Target] = cue; Pending = true; return true;
        }
        public void Step(float dt, CinematicScenePlayer player, ICinematicRemovalWorld world) {
            if (!Pending) return;
            bool pending = false;
            for (int i = 0; i < requests.Length; i++) {
                CinematicCue cue = requests[i];
                if (cue == null) continue;
                uint bit = 1u << i;
                CinematicVehicleDirector vehicle = player.Vehicles[i];
                bool eligible = player.Test(cue.Value) && world.CanRemove(i, vehicle.VisibilityRadius, cue.Radius);
                if (!eligible) { clearAge[i] = 0; Retiring &= ~bit; pending = true; continue; }
                clearAge[i] += dt;
                if (clearAge[i] >= ClearSeconds) Retiring |= bit;
                if (clearAge[i] < ClearSeconds + cue.Duration) { pending = true; continue; }
                vehicle.Complete(); vehicle.Dispose(); player.Vehicles[i] = null;
                world.Remove(i);
                Removed |= bit; Retiring &= ~bit; requests[i] = null;
            }
            Pending = pending;
        }
    }
}
