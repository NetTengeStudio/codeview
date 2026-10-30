using Barotrauma;
using ScpItdMod.Core;

namespace ScpItdMod.Footsteps {
    public static class FootstepConfig {
        public const float Range = 500.0f;
        public const float WalkGain = 0.7f;
        public const float RunGain = 1.0f;
        public const float SnowWalkGain = .95f, SnowRunGain = 1f, DeepSnowWalkGain = 1f, DeepSnowRunGain = 1.45f;
        public const float PitchSpread = 0.06f;
        public const int BankSize = 8;

        private const string SoundFolder = "Content/Sound/Footsteps/";

        public static string[] Bank(string prefix) => SoundBank.Paths(SoundFolder, prefix, BankSize);

        public static float Pitch() =>
            1.0f + Rand.Range(-PitchSpread, PitchSpread, Rand.RandSync.Unsynced);
    }
}
