using System;
using System.Diagnostics;
using System.Threading;

namespace ScpItdMod.Diagnostics {
    internal readonly struct ProfileScope {
        internal readonly long Start, Allocated;
        internal readonly int Zone, Session;
        internal ProfileScope(long start, long allocated, int zone, int session) { Start = start; Allocated = allocated; Zone = zone; Session = session; }
    }
    internal readonly struct ProfileRecord {
        public readonly long Start, Duration, Allocated;
        public readonly int Zone, Gen0, Gen1, Gen2;
        internal ProfileRecord(in ProfileScope scope, long duration, long allocated) { Start = scope.Start; Duration = duration; Allocated = allocated; Zone = scope.Zone; Gen0 = GC.CollectionCount(0); Gen1 = GC.CollectionCount(1); Gen2 = GC.CollectionCount(2); }
    }
    internal sealed class ProfileBuffer {
        private readonly ProfileRecord[] records;
        private readonly int owner;
        private readonly long deadline;
        private readonly int session;
        private int count, dropped, otherThreads;
        private volatile bool capturing = true;
        internal bool Capturing => capturing;
        internal ReadOnlySpan<ProfileRecord> Records => records.AsSpan(0, count);
        internal int Dropped => dropped;
        internal int OtherThreads => otherThreads;
        internal ProfileBuffer(int capacity, double seconds, int session) {
            if (capacity <= 0 || !double.IsFinite(seconds) || seconds <= 0) throw new ArgumentOutOfRangeException();
            records = new ProfileRecord[capacity]; owner = Environment.CurrentManagedThreadId; this.session = session;
            deadline = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
        }
        internal ProfileScope Begin(int zone) {
            if (!Capturing) return default;
            if (Environment.CurrentManagedThreadId != owner) { Interlocked.Increment(ref otherThreads); return default; }
            long now = Stopwatch.GetTimestamp();
            if (now >= deadline) { capturing = false; return default; }
            return new ProfileScope(now, GC.GetAllocatedBytesForCurrentThread(), zone, session);
        }
        internal void End(in ProfileScope scope) {
            if (scope.Session != session || scope.Start == 0 || Environment.CurrentManagedThreadId != owner) return;
            long end = Stopwatch.GetTimestamp(), allocated = GC.GetAllocatedBytesForCurrentThread() - scope.Allocated;
            if (count < records.Length) records[count++] = new ProfileRecord(scope, end - scope.Start, allocated);
            else { dropped++; capturing = false; }
        }
        internal void Stop() => capturing = false;
    }
}
