using System;
using System.Collections.Generic;
using System.Diagnostics;
using Vector2 = ScpItdMod.Core.Vec2;
using Vector4 = ScpItdMod.Motion.Vec4;

namespace ScpItdMod.Motion {
    public readonly struct MotionHandle<T> where T : struct, IEquatable<T> {
        internal readonly MotionChannel<T> Channel;
        internal readonly int Index;
        internal readonly uint Generation;
        internal MotionHandle(MotionChannel<T> channel, int index, uint generation) { Channel = channel; Index = index; Generation = generation; }
        public bool Alive => Channel != null && Channel.Alive(Index, Generation);
        public T Value { get { Require(); return Channel.Read(Index, Generation, false); } }
        public T Velocity { get { Require(); return Channel.Read(Index, Generation, true); } }
        public bool Running => Alive && Channel.Running(Index);
        public MotionHandle<T> Track() { Require(); Channel.Track(Index); return this; }
        public T Sample(double secondsAgo) { Require(); return Channel.Sample(Index, secondsAgo); }
        public bool To(T target, MotionTransition transition) => Channel != null && Channel.Target(Index, Generation, target, transition);
        public void Snap(T value) { Require(); Channel.Snap(Index, value); }
        public void Impulse(T velocity) { Require(); Channel.Impulse(Index, velocity); }
        public void Cancel(bool complete = false) { if (Alive) { Channel.Cancel(Index, complete); } }
        internal void Require() { if (!Alive) { throw new ObjectDisposedException(nameof(MotionHandle<T>)); } }
        internal void RequireOwner(MotionScope owner) {
            Require();
            if (!ReferenceEquals(Channel.Runtime, owner.Runtime) || Channel.Owner(Index) != owner.Id) { throw new ArgumentException("Sequence property belongs to another motion scope."); }
        }
    }

    internal abstract class MotionChannel {
        internal abstract int ActiveCount { get; }
        internal abstract int LiveCount { get; }
        internal abstract void Step(double seconds);
        internal abstract void Release(int owner);
    }
    internal abstract class MotionChannel<T> : MotionChannel where T : struct, IEquatable<T> {
        protected struct Entry {
            internal T Value, Velocity, From, InitialVelocity, Target, Requested;
            internal MotionTransition Transition;
            internal double Elapsed;
            internal int Owner, Active, Next, OwnerNext;
            internal uint Generation, Writer;
        }
        protected Entry[] entries;
        protected double[] started;
        protected MotionHistory<T>[] histories;
        protected int[] active;
        protected int used, activeCount, liveCount;
        private int free = -1;
        private readonly Dictionary<int, int> owners = new Dictionary<int, int>();
        internal readonly MotionRuntime Runtime;
        internal MotionChannel(MotionRuntime runtime, int capacity) {
            Runtime = runtime; entries = new Entry[capacity]; active = new int[capacity];
            started = new double[capacity]; histories = new MotionHistory<T>[capacity];
        }
        internal sealed override int ActiveCount => activeCount;
        internal sealed override int LiveCount => liveCount;
        internal bool Alive(int index, uint generation) => (uint)index < (uint)used && entries[index].Owner != 0 && entries[index].Generation == generation;
        internal int Owner(int index) => entries[index].Owner;
        internal bool Running(int index) => entries[index].Active >= 0;
        internal T Read(int index, uint generation, bool velocity) {
            if (!Alive(index, generation)) { throw new ObjectDisposedException(nameof(MotionHandle<T>)); }
            return velocity ? entries[index].Velocity : entries[index].Value;
        }
        internal MotionHandle<T> Create(int owner, T value) {
            Validate(value);
            int index;
            if (free >= 0) { index = free; free = entries[index].Next; }
            else {
                if (used == entries.Length) {
                    int capacity = used * 2; Array.Resize(ref entries, capacity); Array.Resize(ref active, capacity);
                    Array.Resize(ref started, capacity); Array.Resize(ref histories, capacity);
                }
                index = used++;
            }
            uint generation = entries[index].Generation;
            int ownerNext = owners.TryGetValue(owner, out int head) ? head : -1; owners[owner] = index;
            entries[index] = new Entry { Owner = owner, OwnerNext = ownerNext, Generation = generation == 0 ? 1 : generation, Value = value, From = value, Target = value, Requested = value, Active = -1 };
            started[index] = Runtime.Time; histories[index] = null;
            liveCount++;
            return new MotionHandle<T>(this, index, entries[index].Generation);
        }
        protected void Activate(int index) {
            if (entries[index].Active >= 0) { return; }
            entries[index].Active = activeCount; active[activeCount++] = index;
        }
        protected void Deactivate(int index) {
            ref Entry entry = ref entries[index];
            if (entry.Active < 0) { return; }
            int moved = active[--activeCount]; active[entry.Active] = moved; entries[moved].Active = entry.Active; entry.Active = -1;
        }
        protected void Settle(int index) { entries[index].Value = entries[index].Target; entries[index].Velocity = default; Deactivate(index); }
        protected static void AdvanceWriter(ref Entry entry) { if (++entry.Writer == 0) { entry.Writer++; } }
        internal void Track(int index) { histories[index] ??= new MotionHistory<T>(); }
        protected void Record(int index) {
            MotionHistory<T> history = histories[index];
            if (history == null) { return; }
            ref Entry entry = ref entries[index];
            history.Record(new MotionRecord<T>(entry.From, entry.Target, entry.InitialVelocity, entry.Transition, started[index], Runtime.Time));
        }
        private void BreakHistory(int index) {
            histories[index]?.Clear(); ref Entry entry = ref entries[index];
            entry.From = entry.Value; entry.InitialVelocity = default; started[index] = Runtime.Time; entry.Elapsed = 0;
        }
        internal void Cancel(int index, bool complete) {
            AdvanceWriter(ref entries[index]);
            if (complete) { entries[index].Value = entries[index].Target; }
            entries[index].Velocity = default; entries[index].Target = entries[index].Requested = entries[index].Value; entries[index].Transition = default;
            BreakHistory(index);
            Deactivate(index);
        }
        internal uint Writer(int index) => entries[index].Writer;
        internal void CancelLease(int index, uint generation, uint writer) {
            if (!Alive(index, generation) || entries[index].Writer != writer) { return; }
            AdvanceWriter(ref entries[index]);
            entries[index].Target = entries[index].Requested = entries[index].Value; entries[index].Transition = default; Deactivate(index);
            BreakHistory(index);
        }
        internal void Snap(int index, T value) {
            Validate(value); Cancel(index, false); entries[index].Value = entries[index].Target = entries[index].Requested = value;
            entries[index].From = value;
        }
        private void Free(int index) {
            Deactivate(index); uint generation = entries[index].Generation + 1;
            entries[index] = new Entry { Generation = generation == 0 ? 1 : generation, Active = -1, Next = free };
            started[index] = 0; histories[index] = null;
            free = index; liveCount--;
        }
        internal sealed override void Release(int owner) {
            if (owner == 0) {
                for (int i = 0; i < used; i++) { if (entries[i].Owner != 0) { Free(i); } }
                owners.Clear(); return;
            }
            if (!owners.Remove(owner, out int index)) { return; }
            while (index >= 0) { int next = entries[index].OwnerNext; Free(index); index = next; }
        }
        internal abstract bool Target(int index, uint generation, T value, MotionTransition transition, bool claim = false);
        internal abstract void Impulse(int index, T velocity);
        internal abstract T Sample(int index, double secondsAgo);
        internal void ValidateValue(T value) => Validate(value);
        protected abstract void Validate(T value);
    }
    internal sealed class MotionChannel<T, TOps> : MotionChannel<T> where T : struct, IEquatable<T> where TOps : struct, IMotionOps<T> {
        private static readonly TOps Ops = default;
        internal MotionChannel(MotionRuntime runtime, int capacity) : base(runtime, capacity) { }
        protected override void Validate(T value) { if (!Ops.Finite(value)) { throw new ArgumentOutOfRangeException(nameof(value)); } }
        internal override bool Target(int index, uint generation, T value, MotionTransition transition, bool claim = false) {
            if (!Alive(index, generation)) { return false; }
            Validate(value);
            if (!transition.Valid) { throw new ArgumentException("An initialized transition is required.", nameof(transition)); }
            ref Entry entry = ref entries[index];
            if (entry.Active >= 0 && transition.Priority < entry.Transition.Priority) { return false; }
            if (EqualityComparer<T>.Default.Equals(entry.Requested, value) && entry.Transition.Equals(transition)) {
                if (claim) { AdvanceWriter(ref entry); }
                return true;
            }
            AdvanceWriter(ref entry);
            Record(index);
            entry.Requested = value; entry.Target = Ops.Add(entry.Value, Ops.Subtract(value, entry.Value)); entry.Transition = transition; entry.Elapsed = 0;
            started[index] = Runtime.Time;
            entry.From = entry.Value; entry.InitialVelocity = entry.Velocity;
            if (Runtime.ReducedMotion || Ops.Magnitude(Ops.Subtract(entry.Value, entry.Target)) == 0 && Ops.Magnitude(entry.Velocity) == 0) { Settle(index); return true; }
            Activate(index); return true;
        }
        internal override void Impulse(int index, T velocity) {
            Validate(velocity); AdvanceWriter(ref entries[index]);
            if (Runtime.ReducedMotion) { Settle(index); return; }
            Record(index); started[index] = Runtime.Time;
            entries[index].Velocity = Ops.Add(entries[index].Velocity, velocity);
            if (!entries[index].Transition.Valid) { entries[index].Transition = MotionPresets.Hover; }
            entries[index].From = entries[index].Value; entries[index].InitialVelocity = entries[index].Velocity; entries[index].Elapsed = 0;
            Activate(index);
        }
        internal override T Sample(int index, double secondsAgo) {
            if (!double.IsFinite(secondsAgo) || secondsAgo < 0) { throw new ArgumentOutOfRangeException(nameof(secondsAgo)); }
            ref Entry entry = ref entries[index];
            if (secondsAgo == 0 || Runtime.ReducedMotion || Runtime.Suspended) { return entry.Value; }
            MotionHistory<T> history = histories[index]; double start = started[index];
            if (!entry.Transition.Valid && (history == null || history.Empty)) { return entry.Value; }
            double time = Runtime.SampleTime(secondsAgo);
            MotionRecord<T> record = new MotionRecord<T>(entry.From, entry.Target, entry.InitialVelocity, entry.Transition, start, Runtime.Time);
            if (time < start && history != null && history.Find(time, out MotionRecord<T> previous)) { record = previous; }
            double age = Math.Max(0, time - record.Started);
            SpringMatrix matrix = record.Transition.Valid && record.Transition.Kind == MotionKind.Spring ? Runtime.Matrix(record.Transition, age) : default;
            MotionCurve<T, TOps>.Evaluate(record.From, record.Target, record.Velocity, record.Transition, age, matrix, out T value, out _);
            return value;
        }
        internal override void Step(double seconds) {
            for (int at = 0; at < activeCount;) {
                int index = active[at]; ref Entry entry = ref entries[index];
                if (Runtime.ReducedMotion) { Settle(index); continue; }
                MotionTransition transition = entry.Transition;
                entry.Elapsed += seconds;
                if (transition.Kind == MotionKind.Spring) {
                    SpringMatrix m = Runtime.Matrix(transition, entry.Elapsed);
                    MotionCurve<T, TOps>.Spring(entry.From, entry.Target, entry.InitialVelocity, m, out entry.Value, out entry.Velocity);
                    if (Ops.Magnitude(Ops.Subtract(entry.Value, entry.Target)) <= 0.0001f && Ops.Magnitude(entry.Velocity) <= 0.0001f) { Settle(index); continue; }
                } else {
                    if (entry.Elapsed >= transition.Duration) { Settle(index); continue; }
                    MotionCurve<T, TOps>.Evaluate(entry.From, entry.Target, entry.InitialVelocity, transition, entry.Elapsed, default, out entry.Value, out entry.Velocity);
                }
                at++;
            }
        }
    }

    public sealed class MotionScope : IDisposable {
        internal readonly MotionRuntime Runtime;
        internal readonly int Id;
        private readonly uint epoch;
        private bool disposed;
        internal MotionScope(MotionRuntime runtime, int id) { Runtime = runtime; Id = id; epoch = runtime.Epoch; }
        public bool Alive => !disposed && epoch == Runtime.Epoch;
        private void Require() { if (!Alive) { throw new ObjectDisposedException(nameof(MotionScope)); } }
        public MotionHandle<T> Property<T, TOps>(T initial = default) where T : struct, IEquatable<T> where TOps : struct, IMotionOps<T> { Require(); return Runtime.Column<T, TOps>().Create(Id, initial); }
        public MotionHandle<float> Scalar(float initial = 0) { Require(); return Runtime.Scalars.Create(Id, initial); }
        public MotionHandle<float> Angle(float initial = 0) { Require(); return Runtime.Angles.Create(Id, initial); }
        public MotionHandle<Vector2> Vector(Vector2 initial = default) { Require(); return Runtime.Vectors.Create(Id, initial); }
        public MotionHandle<Vector4> Color(Vector4 initial) { Require(); return Runtime.Colors.Create(Id, initial); }
        public MotionPlayback Play(MotionSequence sequence, float speed = 1, int repeats = 0) { Require(); return Runtime.Play(this, sequence, speed, repeats); }
        public void Dispose() { if (!Alive) { disposed = true; return; } disposed = true; Runtime.Release(Id); }
    }

    public readonly struct MotionStatistics {
        public readonly int Active, Properties, Timelines;
        public readonly double CpuMicroseconds, P95Microseconds, P99Microseconds;
        public readonly long AllocatedBytes;
        public readonly long SpringMatrices;
        internal MotionStatistics(int active, int properties, int timelines, double cpu, double p95, double p99, long allocated, long springMatrices) {
            Active = active; Properties = properties; Timelines = timelines; CpuMicroseconds = cpu; P95Microseconds = p95; P99Microseconds = p99; AllocatedBytes = allocated;
            SpringMatrices = springMatrices;
        }
    }

    public sealed class MotionRuntime {
        internal readonly MotionChannel<float, ScalarOps> Scalars;
        internal readonly MotionChannel<float, AngleOps> Angles;
        internal readonly MotionChannel<Vector2, VectorOps> Vectors;
        internal readonly MotionChannel<Vector4, ColorOps> Colors;
        private readonly List<MotionChannel> channels;
        private readonly Dictionary<Type, MotionChannel> columns = new Dictionary<Type, MotionChannel>();
        private readonly List<MotionPlayback> timelines = new List<MotionPlayback>(16);
        private struct MatrixEntry {
            internal float Omega, Damping;
            internal double Seconds;
            internal SpringMatrix Matrix;
            internal uint Stamp;
        }
        private readonly MatrixEntry[] matrices = new MatrixEntry[256];
        private readonly double[] timings = new double[512];
        private int matrixCount, nextOwner, timingCount, timingIndex;
        private int lastMatrix = -1;
        private uint matrixStamp;
        private bool initialized, updating, wasPaused;
        private double last, rate = 1;
        private long allocated, springMatrices;
        private struct ClockSpan { internal double WallStart, WallEnd, TimeStart, TimeEnd; }
        private readonly ClockSpan[] clocks = new ClockSpan[8];
        private int clockCursor, clockCount;
        internal uint Epoch { get; private set; } = 1;
        public double Time { get; private set; }
        public bool ReducedMotion { get; set; }
        public bool Suspended { get; private set; }
        public double TimeScale {
            get => rate;
            set { if (!double.IsFinite(value) || value < 0 || value > 16) { throw new ArgumentOutOfRangeException(nameof(value)); } rate = value; }
        }
        public MotionRuntime(int capacity = 64) {
            if (capacity < 1 || capacity > 1000000) { throw new ArgumentOutOfRangeException(nameof(capacity)); }
            Scalars = new MotionChannel<float, ScalarOps>(this, capacity); Angles = new MotionChannel<float, AngleOps>(this, capacity);
            Vectors = new MotionChannel<Vector2, VectorOps>(this, capacity); Colors = new MotionChannel<Vector4, ColorOps>(this, capacity);
            channels = new List<MotionChannel>(8) { Scalars, Angles, Vectors, Colors };
            columns.Add(typeof(MotionChannel<float, ScalarOps>), Scalars); columns.Add(typeof(MotionChannel<float, AngleOps>), Angles);
            columns.Add(typeof(MotionChannel<Vector2, VectorOps>), Vectors); columns.Add(typeof(MotionChannel<Vector4, ColorOps>), Colors);
        }
        internal MotionChannel<T, TOps> Column<T, TOps>() where T : struct, IEquatable<T> where TOps : struct, IMotionOps<T> {
            Type key = typeof(MotionChannel<T, TOps>);
            if (columns.TryGetValue(key, out MotionChannel cached)) { return (MotionChannel<T, TOps>)cached; }
            var channel = new MotionChannel<T, TOps>(this, 64); columns.Add(key, channel); channels.Add(channel); return channel;
        }
        public MotionScope Scope() {
            if (nextOwner == int.MaxValue) { throw new InvalidOperationException("Motion scope identifier space exhausted."); }
            return new MotionScope(this, ++nextOwner);
        }
        internal SpringMatrix Matrix(MotionTransition transition, double seconds) {
            if (matrixStamp == 0) { matrixStamp = 1; }
            if (lastMatrix >= 0) {
                ref MatrixEntry last = ref matrices[lastMatrix];
                if (last.Omega == transition.Omega && last.Damping == transition.Damping && last.Seconds == seconds) { return last.Matrix; }
            }
            ulong time = (ulong)BitConverter.DoubleToInt64Bits(seconds);
            uint hash = (uint)(time ^ time >> 32) ^ (uint)BitConverter.SingleToInt32Bits(transition.Omega);
            hash = unchecked(hash ^ (uint)BitConverter.SingleToInt32Bits(transition.Damping) * 0x9e3779b1u);
            hash = unchecked((hash ^ hash >> 16) * 0x7feb352du); hash ^= hash >> 15;
            int index = (int)(hash & (uint)(matrices.Length - 1));
            while (matrices[index].Stamp == matrixStamp) {
                ref MatrixEntry cached = ref matrices[index];
                if (cached.Omega == transition.Omega && cached.Damping == transition.Damping && cached.Seconds == seconds) { lastMatrix = index; return cached.Matrix; }
                index = (index + 1) & (matrices.Length - 1);
            }
            var matrix = new SpringMatrix(transition.Omega, transition.Damping, seconds);
            springMatrices++;
            if (matrixCount < matrices.Length / 2) {
                matrices[index] = new MatrixEntry { Omega = transition.Omega, Damping = transition.Damping, Seconds = seconds, Matrix = matrix, Stamp = matrixStamp };
                lastMatrix = index; matrixCount++;
            }
            return matrix;
        }
        private void Step(double seconds) {
            matrixCount = 0; lastMatrix = -1;
            if (++matrixStamp == 0) { Array.Clear(matrices, 0, matrices.Length); matrixStamp = 1; }
            for (int i = 0; i < channels.Count; i++) { channels[i].Step(seconds); }
        }
        private void RecordClock(double wallStart, double wallEnd, double timeStart, double timeEnd) {
            if (wallEnd <= wallStart) { return; }
            clocks[clockCursor] = new ClockSpan { WallStart = wallStart, WallEnd = wallEnd, TimeStart = timeStart, TimeEnd = timeEnd };
            clockCursor = (clockCursor + 1) & (clocks.Length - 1); clockCount = Math.Min(clockCount + 1, clocks.Length);
        }
        internal double SampleTime(double secondsAgo) {
            if (updating || clockCount == 0) { return Time - secondsAgo * rate; }
            double wall = last - secondsAgo; ClockSpan span = default;
            for (int i = 0; i < clockCount; i++) {
                span = clocks[(clockCursor - i - 1 + clocks.Length) & (clocks.Length - 1)];
                if (wall >= span.WallStart) { return span.TimeStart + (span.TimeEnd - span.TimeStart) * ((wall - span.WallStart) / (span.WallEnd - span.WallStart)); }
            }
            return span.TimeStart - (span.WallStart - wall) * ((span.TimeEnd - span.TimeStart) / (span.WallEnd - span.WallStart));
        }
        public void Advance(double now, bool paused = false) {
            if (!double.IsFinite(now)) { throw new ArgumentOutOfRangeException(nameof(now)); }
            if (updating) { throw new InvalidOperationException("Motion Advance is not reentrant."); }
            if (!initialized) { last = now; initialized = true; }
            if (now < last) { return; }
            double wallStart = last, timeStart = Time, delta = now - last; last = now;
            if (paused || rate == 0) { Suspended = true; wasPaused = true; RecordClock(wallStart, now, Time, Time); return; }
            if (wasPaused) { wasPaused = false; RecordClock(wallStart, now, Time, Time); return; }
            Suspended = false;
            long start = Stopwatch.GetTimestamp(), before = GC.GetAllocatedBytesForCurrentThread();
            updating = true;
            try {
                double end = Time + delta * rate;
                if (!double.IsFinite(end)) { throw new ArgumentOutOfRangeException(nameof(now)); }
                int events = 0;
                while (true) {
                    double next = end;
                    bool due = false;
                    for (int i = 0; i < timelines.Count; i++) {
                        MotionPlayback playback = timelines[i];
                        if (playback.Active && playback.Next <= next) { next = Math.Max(Time, playback.Next); due = true; }
                    }
                    if (next > Time) { Step(next - Time); Time = next; }
                    if (!due) { if (ReducedMotion) { Step(0); } break; }
                    for (int i = 0; i < timelines.Count; i++) {
                        MotionPlayback playback = timelines[i];
                        while (playback.Active && playback.Next <= Time) {
                            if (++events > 16384) { throw new InvalidOperationException("Motion timeline event budget exceeded."); }
                            playback.Dispatch();
                        }
                    }
                }
                for (int i = 0; i < timelines.Count; i++) { timelines[i].TryComplete(); }
                for (int i = timelines.Count - 1; i >= 0; i--) { if (!timelines[i].Active) { timelines.RemoveAt(i); } }
                RecordClock(wallStart, now, timeStart, Time);
            } finally {
                updating = false;
                allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                timings[timingIndex] = (Stopwatch.GetTimestamp() - start) * (1e6 / Stopwatch.Frequency);
                timingIndex = (timingIndex + 1) % timings.Length; timingCount = Math.Min(timingCount + 1, timings.Length);
            }
        }
        internal MotionPlayback Play(MotionScope scope, MotionSequence sequence, float speed, int repeats) {
            if (sequence == null) { throw new ArgumentNullException(nameof(sequence)); }
            if (!float.IsFinite(speed) || speed <= 0 || speed > 16 || repeats < 0 || repeats > 1024) { throw new ArgumentOutOfRangeException(nameof(speed)); }
            sequence.Validate(scope, speed);
            var playback = new MotionPlayback(scope, sequence, Time, speed, repeats);
            timelines.Add(playback); return playback;
        }
        internal void Release(int owner) {
            for (int i = 0; i < timelines.Count; i++) { if (timelines[i].Owner.Id == owner) { timelines[i].Cancel(); } }
            for (int i = 0; i < channels.Count; i++) { channels[i].Release(owner); }
            if (!updating) { for (int i = timelines.Count - 1; i >= 0; i--) { if (!timelines[i].Active) { timelines.RemoveAt(i); } } }
        }
        public void Reset() {
            if (updating) { throw new InvalidOperationException("Cannot reset motion runtime inside a timeline event."); }
            for (int i = 0; i < timelines.Count; i++) { timelines[i].Cancel(); }
            timelines.Clear();
            for (int i = 0; i < channels.Count; i++) { channels[i].Release(0); }
            if (++Epoch == 0) { Epoch++; }
            initialized = false; wasPaused = false; Suspended = false; Time = 0; timingCount = timingIndex = 0; allocated = springMatrices = 0;
            clockCursor = clockCount = 0;
        }
        // True while any property is still moving. Renderers use it to decide whether an idle surface needs a repaint.
        public bool Animating {
            get {
                for (int i = 0; i < channels.Count; i++) { if (channels[i].ActiveCount > 0) { return true; } }
                return false;
            }
        }
        public MotionStatistics Statistics() {
            int active = 0, properties = 0;
            for (int i = 0; i < channels.Count; i++) { active += channels[i].ActiveCount; properties += channels[i].LiveCount; }
            Span<double> sorted = stackalloc double[512]; timings.AsSpan(0, timingCount).CopyTo(sorted);
            sorted = sorted.Slice(0, timingCount); sorted.Sort();
            return new MotionStatistics(active, properties, timelines.Count, timingCount == 0 ? 0 : timings[(timingIndex + timings.Length - 1) % timings.Length],
                timingCount == 0 ? 0 : sorted[(int)((timingCount - 1) * 0.95)], timingCount == 0 ? 0 : sorted[(int)((timingCount - 1) * 0.99)], allocated, springMatrices);
        }
    }
}
