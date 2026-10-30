using System;

namespace ScpItdMod.Rec {
    public sealed class ServerClock {
        public const int Window = 8;

        private readonly double[] offsets = new double[Window];
        private readonly double[] delays = new double[Window];
        private int count;
        private int next;

        public bool Valid => count > 0;

        public double Offset { get; private set; }

        public double Delay { get; private set; }

        public void Reset() { count = 0; next = 0; Offset = 0; Delay = 0; }

        public void Add(double sentLocal, double receivedLocal, double serverTime) {
            double delay = receivedLocal - sentLocal;
            if (!double.IsFinite(delay) || delay < 0 || !double.IsFinite(serverTime)) { return; }
            offsets[next] = serverTime - (sentLocal + delay * 0.5);
            delays[next] = delay;
            next = (next + 1) % Window;
            if (count < Window) { count++; }
            int best = 0;
            for (int i = 1; i < count; i++) { if (delays[i] < delays[best]) { best = i; } }
            Offset = offsets[best];
            Delay = delays[best];
        }
    }
}
