using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ScpItdMod.Rec {
    [Flags]
    public enum SceneFlags : byte { None = 0, Item = 1, Below = 2, Above = 4, FlipX = 8, FlipY = 16 }

    // Shared gameplay orientation; sprite-effect fields only exist in CLIENT builds.
    public static class SceneSpriteFlip {
        public static SceneFlags Resolve(bool flippedX, bool flippedY, bool canSpriteFlipX, bool canSpriteFlipY) {
            SceneFlags flags = SceneFlags.None;
            if (flippedX && canSpriteFlipX) { flags |= SceneFlags.FlipX; }
            if (flippedY && canSpriteFlipY) { flags |= SceneFlags.FlipY; }
            return flags;
        }
    }

    // A plain render description. No Barotrauma object references may escape into history.
    public readonly struct SceneObject : IEquatable<SceneObject> {
        public readonly ushort Id;
        public readonly uint Generation;
        public readonly string Prefab;
        public readonly SceneFlags Flags;
        public readonly float X, Y, Rotation, ScaleX, ScaleY;
        public readonly ushort Width, Height;
        public readonly uint Color;
        public readonly SceneShadow Shadow;

        public SceneObject(ushort id, uint generation, string prefab, SceneFlags flags,
            float x, float y, float rotation, float scaleX, float scaleY,
            ushort width, ushort height, uint color, SceneShadow shadow = null) {
            Id = id; Generation = generation; Prefab = prefab ?? "";
            Flags = flags; X = Quantize(x); Y = Quantize(y);
            Rotation = Quantize(rotation, 4096); ScaleX = Quantize(scaleX, 1024); ScaleY = Quantize(scaleY, 1024);
            Width = width; Height = height; Color = color; Shadow = shadow;
        }
        private static float Quantize(float value, int units = 8) => MathF.Round(value * units) / units;
        public ulong Key => ((ulong)Generation << 16) | Id;
        public bool Valid => Id != 0 && Generation != 0 && Prefab.Length is > 0 and <= 80
            && Width > 0 && Height > 0 && float.IsFinite(X) && float.IsFinite(Y)
            && float.IsFinite(Rotation) && float.IsFinite(ScaleX) && float.IsFinite(ScaleY)
            && ScaleX > 0 && ScaleY > 0 && (Flags & ~(SceneFlags.Item | SceneFlags.Below | SceneFlags.Above | SceneFlags.FlipX | SceneFlags.FlipY)) == 0;
        public bool Equals(SceneObject other) => Id == other.Id && Generation == other.Generation &&
            string.Equals(Prefab, other.Prefab, StringComparison.Ordinal) && Flags == other.Flags && X == other.X && Y == other.Y &&
            Rotation == other.Rotation && ScaleX == other.ScaleX && ScaleY == other.ScaleY &&
            Width == other.Width && Height == other.Height && Color == other.Color && (ReferenceEquals(Shadow,other.Shadow) || Shadow != null && Shadow.Equals(other.Shadow));
        public override bool Equals(object obj) => obj is SceneObject other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Id, Generation, X, Y);
        public int Bytes => 64 + Prefab.Length * 2 + (Shadow?.Count ?? 0) * 16;
    }

    public readonly struct SceneChange {
        public readonly ulong Key;
        public readonly bool Removed;
        public readonly SceneObject State;
        public SceneChange(ulong key, bool removed, in SceneObject state) { Key = key; Removed = removed; State = state; }
        public int Bytes => Removed ? 16 : 16 + State.Bytes;
    }

    public sealed class SceneTape {
        public const int MaxObjects = ushort.MaxValue;
        public const int InitialObjects = 512;
        public const double SampleSeconds = 1.0;
        public const double KeySeconds = 10.0;
        public const double MaxStaleness = 1.5;
        private sealed class Frame {
            public double Time;
            public float X, Y;
            public bool Key, Gap;
            public SceneChange[] Changes;
            public long Bytes;
        }
        private readonly List<Frame> frames = new List<Frame>(256);
        private readonly Dictionary<ulong, SceneObject> previous = new Dictionary<ulong, SceneObject>(InitialObjects);
        private readonly Dictionary<ulong, SceneObject> current = new Dictionary<ulong, SceneObject>(InitialObjects);
        private readonly Dictionary<ulong, SceneObject> reconstructed = new Dictionary<ulong, SceneObject>(InitialObjects);
        private readonly List<SceneChange> pending = new List<SceneChange>(InitialObjects);
        private readonly List<ulong> gone = new List<ulong>(InitialObjects);
        private long budget;
        private readonly double retention;
        private long bytes;
        private double keyAt = double.NegativeInfinity;
        private bool interrupted = true;

        public SceneTape(long budgetBytes, double keepSeconds) {
            if (budgetBytes < 1024 || !double.IsFinite(keepSeconds) || keepSeconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(budgetBytes));
            budget = budgetBytes; retention = keepSeconds;
        }
        public long Bytes => bytes;
        public long Budget => budget;
        public void Resize(long bytesLimit) {
            if (bytesLimit < 1024) { throw new ArgumentOutOfRangeException(nameof(bytesLimit)); }
            budget = bytesLimit;
            if (frames.Count > 0) { Enforce(frames[frames.Count - 1].Time); }
        }
        public int Frames => frames.Count;
        public void Clear() { frames.Clear(); previous.Clear(); current.Clear(); reconstructed.Clear(); pending.Clear(); gone.Clear(); bytes = 0; keyAt = double.NegativeInfinity; interrupted = true; }
        public void Gap(double time) {
            if (!double.IsFinite(time) || time < 0 || interrupted) { return; }
            if (frames.Count > 0 && time < frames[frames.Count - 1].Time) { Clear(); return; }
            Store(new Frame { Time = time, Gap = true, Changes = Array.Empty<SceneChange>(), Bytes = 40 });
            previous.Clear(); interrupted = true;
            Enforce(time);
        }
        public void Sample(double time, float cameraX, float cameraY, IReadOnlyList<SceneObject> objects) {
            if (!double.IsFinite(time) || time < 0 || !float.IsFinite(cameraX) || !float.IsFinite(cameraY) || objects == null)
                throw new ArgumentOutOfRangeException(nameof(time));
            if (frames.Count > 0 && time < frames[frames.Count - 1].Time) { Clear(); }
            current.Clear();
            if (objects.Count > MaxObjects) { throw new ArgumentOutOfRangeException(nameof(objects)); }
            int n = objects.Count;
            for (int i = 0; i < n; i++) {
                SceneObject obj = objects[i];
                if (!obj.Valid || !current.TryAdd(obj.Key, obj)) { throw new ArgumentException("Invalid or duplicated historical scene identity"); }
            }
            bool key = interrupted || time - keyAt >= KeySeconds || frames.Count == 0;
            pending.Clear();
            foreach (KeyValuePair<ulong, SceneObject> pair in current) {
                if (key || !previous.TryGetValue(pair.Key, out SceneObject old) || !old.Equals(pair.Value))
                    pending.Add(new SceneChange(pair.Key, false, pair.Value));
            }
            if (!key) {
                foreach (ulong id in previous.Keys) {
                    if (!current.ContainsKey(id)) { pending.Add(new SceneChange(id, true, default)); }
                }
            }
            // Empty deltas are cheap and carry scene/camera continuity during static periods.
            var changes = pending.Count == 0 ? Array.Empty<SceneChange>() : pending.ToArray();
            long size = 40;
            for (int i = 0; i < changes.Length; i++) { size += changes[i].Bytes; }
            Store(new Frame { Time = time, X = cameraX, Y = cameraY, Key = key, Changes = changes, Bytes = size });
            previous.Clear();
            foreach (KeyValuePair<ulong, SceneObject> pair in current) { previous.Add(pair.Key, pair.Value); }
            if (key) { keyAt = time; }
            interrupted = false;
            Enforce(time);
        }
        private void Store(Frame frame) { frames.Add(frame); bytes += frame.Bytes; }
        private void Enforce(double now) {
            // A single oversized keyframe must not bypass the configured memory budget.
            if (bytes > budget && frames.Count == 1) {
                frames.Clear(); bytes = 0; previous.Clear(); interrupted = true;
                keyAt = double.NegativeInfinity;
                return;
            }
            // Evict complete keyframe groups only. Never leave orphaned deltas.
            while (frames.Count > 1 && (bytes > budget || now - frames[0].Time > retention)) {
                int next = 1;
                while (next < frames.Count && !frames[next].Key && !frames[next].Gap) { next++; }
                if (next == frames.Count) {
                    // No reconstructable successor yet. A single oversize group is discarded.
                    if (bytes <= budget) { break; }
                    frames.Clear(); bytes = 0; previous.Clear(); interrupted = true; keyAt = double.NegativeInfinity;
                    return;
                }
                for (int i = 0; i < next; i++) { bytes -= frames[i].Bytes; }
                frames.RemoveRange(0, next);
                if (frames.Count > 0 && frames[0].Gap) {
                    bytes -= frames[0].Bytes; frames.RemoveAt(0);
                }
            }
        }
        public bool Read(double time, List<SceneObject> output, out float cameraX, out float cameraY) {
            output.Clear(); cameraX = cameraY = 0;
            if (!double.IsFinite(time) || frames.Count == 0 || time < frames[0].Time) { return false; }
            int lo = 0, hi = frames.Count - 1;
            while (lo < hi) {
                int mid = lo + (hi - lo + 1) / 2;
                if (frames[mid].Time <= time) { lo = mid; } else { hi = mid - 1; }
            }
            Frame target = frames[lo];
            if (target.Gap || time - target.Time > MaxStaleness) { return false; }
            int first = lo;
            while (first > 0 && !frames[first].Key && !frames[first].Gap) { first--; }
            if (!frames[first].Key) { return false; }
            reconstructed.Clear();
            for (int i = first; i <= lo; i++) {
                foreach (SceneChange change in frames[i].Changes) {
                    if (change.Removed) { reconstructed.Remove(change.Key); }
                    else { reconstructed[change.Key] = change.State; }
                }
            }
            foreach (SceneObject state in reconstructed.Values) { output.Add(state); }
            output.Sort(static (a, b) => a.Key.CompareTo(b.Key));
            cameraX = target.X; cameraY = target.Y;
            return true;
        }
    }
}