using System;

namespace ScpItdMod.Motion {
    public sealed class SurfaceBudget {
        public long Limit { get; }
        public long Bytes { get; private set; }
        public long Peak { get; private set; }
        public int Count { get; private set; }
        public int Rejected { get; private set; }
        public SurfaceBudget(long limit) { if (limit <= 0) { throw new ArgumentOutOfRangeException(nameof(limit)); } Limit = limit; }
        public bool Reserve(long bytes) {
            if (bytes <= 0) { throw new ArgumentOutOfRangeException(nameof(bytes)); }
            if (bytes > Limit - Bytes) { Rejected++; return false; }
            Bytes += bytes; Count++; Peak = Math.Max(Peak, Bytes); return true;
        }
        public void Release(long bytes) {
            if (bytes <= 0 || bytes > Bytes || Count == 0) { throw new InvalidOperationException("Unbalanced surface ownership."); }
            Bytes -= bytes; Count--;
        }
    }
}
