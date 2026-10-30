using System;
using System.Reflection;

namespace ScpItdMod.Core {
    public sealed class SubsystemGuard {
        private readonly FaultBreaker breaker;
        private readonly Assembly mine;
        private readonly Action<string> report;

        public SubsystemGuard(int limit, Assembly mine, Action<string> report) {
            breaker = new FaultBreaker(limit);
            this.mine = mine ?? throw new ArgumentNullException(nameof(mine));
            this.report = report ?? throw new ArgumentNullException(nameof(report));
        }

        public bool Tripped(string name) => breaker.Tripped(name);

        public int Faults(string name) => breaker.Count(name);

        public bool Run(string name, Action body, Action onTrip = null) {
            if (body == null) throw new ArgumentNullException(nameof(body));
            if (breaker.Tripped(name)) return false;
            try {
                body();
                return true;
            } catch (Exception error) when (FaultPolicy.Owns(error, mine)) {
                bool tripped = breaker.Record(name);
                if (breaker.Count(name) == 1) report("contained " + FaultPolicy.Describe(error, mine) + " in " + name);
                if (tripped) Trip(name, error, onTrip);
                return false;
            }
        }

        private void Trip(string name, Exception cause, Action onTrip) {
            report(name + " failed " + FaultPolicy.TripLimit + " times (last: " + FaultPolicy.Describe(cause, mine) + "); it is switched off for this round and the game keeps running");
            if (onTrip == null) return;
            try {
                onTrip();
            } catch (Exception error) when (FaultPolicy.Owns(error, mine)) {
                report(name + " cleanup failed: " + FaultPolicy.Describe(error, mine));
            }
        }

        public void Reset() => breaker.Reset();
    }
}
