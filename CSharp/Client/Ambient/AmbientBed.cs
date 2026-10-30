using ScpItdMod.Core;

namespace ScpItdMod.Ambient {
    public static class AmbientBed {
        private static readonly SoundSwitch Zones = new SoundSwitch(
            new SoundCue(AmbientConfig.Bed("lowdrone")),
            new SoundCue(AmbientConfig.Bed("ventilation")),
            new SoundCue(AmbientConfig.Bed("rumble"), AmbientConfig.Bed("fuelpump")),
            new SoundCue(AmbientConfig.Bed("servers1"), AmbientConfig.Bed("fan"),
                AmbientConfig.Bed("pulsing")));

        private static SoundCue current;
        private static SoundCue leaving;
        private static float level;
        private static float leavingLevel;

        public static bool Enabled { get; set; } = true;

        public static void Warm() => Zones.Warm();

        public static void Advance(bool active, AmbientZone zone, float deltaTime) {
            Select(active && Enabled ? Zones.Of((int)zone) : null);
            Fade(deltaTime);
        }

        public static void Release() {
            Zones.Release();
            current = null;
            leaving = null;
            level = 0.0f;
            leavingLevel = 0.0f;
        }

        private static void Select(SoundCue wanted) {
            if (ReferenceEquals(wanted, current)) { return; }
            SoundCue outgoing = current;
            float outgoingLevel = level;
            if (ReferenceEquals(wanted, leaving)) {
                level = leavingLevel;
                leaving = null;
            } else {
                level = 0.0f;
            }
            leaving?.Stop();
            leaving = outgoing;
            leavingLevel = outgoingLevel;
            current = wanted;
        }

        private static void Fade(float deltaTime) {
            if (leaving != null) {
                leavingLevel = Scalar.Approach(leavingLevel, 0.0f, AmbientConfig.FadeRate, deltaTime);
                if (leavingLevel <= AmbientConfig.FadeCutoff) {
                    leaving.Stop();
                    leaving = null;
                    leavingLevel = 0.0f;
                } else {
                    leaving.Gain = AmbientConfig.BedGain * leavingLevel;
                }
            }
            if (current == null) { return; }
            level = Scalar.Approach(level, 1.0f, AmbientConfig.FadeRate, deltaTime);
            current.Loop(AmbientConfig.BedGain * level);
            current.Gain = AmbientConfig.BedGain * level;
        }
    }
}
