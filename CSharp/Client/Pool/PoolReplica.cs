using System;
using Quaternion = ScpItdMod.Motion.Rotation3;
using ScpItdMod.Motion;
using Vector2 = ScpItdMod.Core.Vec2;

namespace ScpItdMod.Pool {
    public sealed class PoolReplica {
        private const int Capacity = 8;
        private static readonly Quaternion InitialOrientation = BallRotation.ToRender(Quaternion.Identity);
        public const double Delay = 0.1;
        public readonly Table Table;
        public readonly Ball[] Current = new Ball[16], Previous = new Ball[16];
        private readonly Ball[] frames = new Ball[Capacity * 16];
        private readonly double[] stamps = new double[Capacity];
        public PoolMeta Meta { get; private set; }
        public bool Ready { get; private set; }
        public double ReceivedAt { get; private set; }
        private int first, count, version, sampleVersion = -1, sampleA, sampleB;
        private double clockOffset, sampleAt = double.NaN;
        private float sampleT, interval;
        public PoolReplica(Table table) { Table = table ?? throw new ArgumentNullException(nameof(table)); }
        public bool Apply(ReadOnlySpan<byte> data, double now, out PoolMeta header) {
            header = default;
            if (!double.IsFinite(now)) { return false; }
            Span<Ball> incoming = stackalloc Ball[16];
            if (!PoolWire.ReadSnapshot(data, Table, incoming, out header, out ushort mask)) { return false; }
            bool same = Ready && header.Epoch == Meta.Epoch;
            if (Ready && (header.Table != Meta.Table || !same && (int)(header.Epoch - Meta.Epoch) <= 0)) { return false; }
            if (same && !header.Full && header.Revision == Meta.Revision && (int)(header.Tick - Meta.Tick) <= 0) { return false; }
            if (!same && !header.Full || same && ((int)(header.Revision - Meta.Revision) < 0 || header.Revision == Meta.Revision && (int)(header.Tick - Meta.Tick) < 0) || same && header.Revision != Meta.Revision && !header.Full) { return false; }
            bool reset = !same || header.Tick < Meta.Tick || !Meta.Moving && header.Moving || header.Tick == Meta.Tick && header.Revision != Meta.Revision && !header.Moving;
            for (int i = 0; i < 16; i++) { Previous[i] = Current[i]; if ((mask & 1 << i) != 0) { Current[i] = incoming[i]; } if (reset) { Previous[i] = Current[i]; } }
            double stamp = header.Tick * (double)Physics.Step;
            if (reset) { first = count = 0; clockOffset = now - stamp; }
            else { clockOffset = Math.Min(clockOffset, now - stamp); }
            if (count == 0 || stamps[(first + count - 1) % Capacity] < stamp) {
                if (count == Capacity) { first = (first + 1) % Capacity; count--; }
                int next = (first + count) % Capacity; Current.AsSpan().CopyTo(frames.AsSpan(next * 16, 16)); stamps[next] = stamp; count++;
            }
            version++;
            if (!header.Full) { header.Name0 = Meta.Name0; header.Name1 = Meta.Name1; }
            Meta = header; ReceivedAt = now; Ready = true; return true;
        }
        public Quaternion Orientation(int index, double now) {
            if ((uint)index >= 16 || !double.IsFinite(now)) { throw new ArgumentOutOfRangeException(nameof(index)); }
            if (!Ready || count == 0) { return InitialOrientation; }
            if (sampleAt != now || sampleVersion != version) { Bracket(now); }
            return BallRotation.ToRender(BallRotation.Interpolate(frames[sampleA * 16 + index], frames[sampleB * 16 + index], interval, sampleT));
        }
        public Ball Sample(int index, double now) {
            if ((uint)index >= 16 || !double.IsFinite(now)) { throw new ArgumentOutOfRangeException(nameof(index)); }
            if (!Ready || count == 0) { return Current[index]; }
            if (sampleAt != now || sampleVersion != version) { Bracket(now); }
            Ball a = frames[sampleA * 16 + index], b = frames[sampleB * 16 + index]; float t = sampleT;
            Vector2 position = Vector2.Lerp(a.Position, b.Position, t), dv = a.Velocity - b.Velocity;
            float speed = dv.LengthSquared();
            if (!a.Down && interval > 0 && speed > 0.000001f) {
                float hit = Math.Clamp(Vector2.Dot(b.Position - a.Position - b.Velocity * interval, dv) / speed, 0, interval);
                Vector2 left = a.Position + a.Velocity * hit, right = b.Position - b.Velocity * (interval - hit);
                float tolerance = Table.Slide * 9.80665f * interval * interval + 0.0001f;
                if (hit > 0.00001f && interval - hit > 0.00001f && Vector2.DistanceSquared(left, right) <= tolerance * tolerance) {
                    Vector2 contact = (left + right) / 2; float elapsed = t * interval;
                    position = elapsed < hit ? Vector2.Lerp(a.Position, contact, elapsed / hit) : Vector2.Lerp(contact, b.Position, (elapsed - hit) / (interval - hit));
                }
            }
            b.Position = position; b.Orientation = BallRotation.Interpolate(a, b, interval, t);
            if (t < 1) { b.Pocket = a.Pocket; } return b;
        }
        private void Bracket(double now) {
            sampleAt = now; sampleVersion = version; double target = now - clockOffset - Delay;
            sampleA = sampleB = first; sampleT = 0; interval = 0;
            for (int i = 0; i < count; i++) {
                int slot = (first + i) % Capacity;
                if (stamps[slot] >= target) {
                    sampleB = slot;
                    if (sampleA != sampleB) { interval = (float)(stamps[sampleB] - stamps[sampleA]); sampleT = interval <= 0 ? 1 : Math.Clamp((float)(target - stamps[sampleA]) / interval, 0, 1); }
                    return;
                }
                sampleA = sampleB = slot;
            }
        }
    }
}
