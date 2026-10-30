using System;

namespace ScpItdMod.Rec {
    public struct SightFacts {
        public bool Self;
        public bool Able;
        public bool Hidden;
        public bool InView;
        public bool LineClear;
        public float Light;
        public float Distance;
    }

    public struct HearFacts {
        public bool Able;
        public bool Delivered;
        public bool Radio;
        public bool RadioHeld;
        public float Distance;
        public float Range;
    }

    public static class Perception {
        public const float DarkLight = 0.1f;
        public const float TouchSight = 120.0f;
        public const float MaskFace = 1.0f;

        public static bool Seen(in SightFacts f) =>
            !f.Self && f.Able && !f.Hidden && f.InView && f.LineClear && (f.Light >= DarkLight || f.Distance <= TouchSight);

        public static bool Heard(in HearFacts f) {
            if (!f.Able || !f.Delivered) { return false; }
            if (f.Radio) { return f.RadioHeld; }
            return f.Distance <= f.Range;
        }

        public static float Luminance(byte r, byte g, byte b) => (0.2126f * r + 0.7152f * g + 0.0722f * b) / 255.0f;

        public static float Falloff(float distance, float range, float brightness, float luminance) {
            if (range <= 0.0f || distance >= range || brightness <= 0.0f) { return 0.0f; }
            float k = 1.0f - distance / range;
            return brightness * luminance * k * k;
        }
    }
}
