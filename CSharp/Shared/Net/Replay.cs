namespace ScpItdMod.Net {
    public struct Replay {
        private uint top;
        private ulong seen;
        private bool started;

        public void Reset() {
            top = 0;
            seen = 0;
            started = false;
        }

        public bool Accept(uint seq) {
            if (!started) {
                started = true;
                top = seq;
                seen = 1;
                return true;
            }
            int ahead = (int)(seq - top);
            if (ahead > 0) {
                seen = ahead >= 64 ? 1UL : (seen << ahead) | 1UL;
                top = seq;
                return true;
            }
            if (ahead <= -64) { return false; }
            int behind = -ahead;
            ulong bit = 1UL << behind;
            if ((seen & bit) != 0) { return false; }
            seen |= bit;
            return true;
        }
    }

    public struct Gate {
        private float tokens;
        private double stamp;
        private bool started;

        public bool Take(double now, float rate, float burst, float cost = 1.0f) {
            if (!double.IsFinite(now) || !float.IsFinite(rate) || rate < 0 ||
                !float.IsFinite(burst) || burst <= 0 ||
                !float.IsFinite(cost) || cost <= 0) { return false; }
            if (!started) {
                started = true;
                stamp = now;
                tokens = burst;
            } else if (now > stamp) {
                double refill = (now - stamp) * rate;
                tokens = (float)System.Math.Min(burst, tokens + refill);
                stamp = now;
            }
            // A backwards clock cannot advance the refill epoch.
            if (tokens < cost) { return false; }
            tokens -= cost;
            return true;
        }
    }
}
