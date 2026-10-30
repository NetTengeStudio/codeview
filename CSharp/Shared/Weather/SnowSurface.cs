using System;
using ScpItdMod.Core;

namespace ScpItdMod.Weather {
    // One sample of the snow surface as the picture needs it, and the slice between two neighbouring samples.
    public struct SnowSample {
        public float X, Ground, Depth, Slope, Track, Compaction, Load, Fresh, Blood;
        // Steepest rise of the surface towards each side within a short reach (dimensionless slope, 0 when the surface falls away): what the sky and the sun can see past this sample.
        public float HorizonLeft, HorizonRight;
        public float Top => Ground + Depth;
    }

    public struct SnowSlice { public SnowSample A, B; }
}
