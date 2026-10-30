using ScpItdMod.Core;

namespace ScpItdMod.Stamina {
    public static class StaminaAudio {
        private static readonly SoundCue Breathing = new SoundCue(
            "Content/Sound/breath1.ogg",
            "Content/Sound/breath2.ogg",
            "Content/Sound/breath3.ogg");

        private static readonly SoundCue Gasping = new SoundCue("Content/Sound/breath0.ogg");

        public static void Warm() {
            Breathing.Warm();
            Gasping.Warm();
        }

        public static void Advance() {
            float gain = Scalar.Clamp01(
                (StaminaConfig.BreathVolumeSpan - StaminaApi.Value) * (1.0f / StaminaConfig.BreathVolumeSpan));
            Breathing.Gain = gain;
            Gasping.Gain = gain;
            if (!StaminaApi.Winded || (!StaminaApi.Exerting && !StaminaApi.Locked)) { return; }
            (StaminaApi.Locked ? Gasping : Breathing).Play(gain);
        }

        public static void Reset() {
            Breathing.Stop();
            Gasping.Stop();
        }

        public static void Release() {
            Breathing.Release();
            Gasping.Release();
        }
    }
}
