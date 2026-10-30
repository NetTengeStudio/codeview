using System;

namespace ScpItdMod.Docs {
    // Tracks when a file must be written. Writes run in the background, so a snapshot can be in flight
    // while the game keeps changing the data. Every change bumps a generation; a finished write clears
    // Dirty only when it covers the latest generation, otherwise the file is written again.
    public sealed class WriteBehind {
        private readonly double gap, ceiling;
        private double due = -1, last = double.NegativeInfinity;
        private long changes;
        private int failures;

        public WriteBehind(double gap, double ceiling) {
            this.gap = gap;
            this.ceiling = ceiling;
        }

        public bool Dirty { get; private set; }

        // Consecutive failed writes. Zero means the last write of this file succeeded.
        public int Failures => failures;

        public void Mark(double now) {
            changes++;
            if (!Dirty) { Dirty = true; due = Math.Max(now, last + gap); }
        }

        public bool Due(double now) => Dirty && now >= due;

        // Takes the snapshot generation for a write that is about to start. The file is not due again until that write ends.
        public long Start() {
            due = double.PositiveInfinity;
            return changes;
        }

        public void Done(double now, long generation) {
            failures = 0;
            last = now;
            if (generation == changes) {
                Dirty = false;
                due = -1;
            } else {
                due = now;
            }
        }

        public bool Failed(double now) {
            failures++;
            due = now + Math.Min(ceiling, gap * Math.Pow(2, Math.Min(failures, 16)));
            return failures == 1 || failures % 10 == 0;
        }
    }
}
