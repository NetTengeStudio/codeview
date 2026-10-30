using Barotrauma;
using ScpItdMod.Core;

namespace ScpItdMod.Ambient {
    public static class AmbientConfig {
        public const float ChancePerSecond = 0.04f;
        public const float GeneralChance = 1.0f / 3.0f;
        public const float Gain = 1.0f;
        public const float Range = 1000.0f;
        public const float Jitter = 100.0f;
        public const double QuietSeconds = 8.0;

        public const float BedGain = 0.45f;
        public const float FadeRate = 0.7f;
        public const float FadeCutoff = 0.02f;

        public const int GeneralCount = 15;
        public const int InteriorCount = 11;
        public const int MachineryCount = 11;
        public const int OutpostCount = 12;

        private const string SoundFolder = "Content/Sound/Ambient/";
        private const string ClipPrefix = "ambient";
        private const string BedFolder = "bed/";

        public static string[] Bank(string folder, int count) =>
            SoundBank.Paths(SoundFolder + folder + "/", ClipPrefix, count);

        public static string Bed(string clip) => SoundFolder + BedFolder + clip + ".ogg";

        public static bool Triggered(float deltaTime) => Roll() < ChancePerSecond * deltaTime;

        public static bool Mixes() => Roll() < GeneralChance;

        private static float Roll() => Rand.Range(0.0f, 1.0f, Rand.RandSync.Unsynced);
    }
}
