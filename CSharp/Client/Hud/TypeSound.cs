using Barotrauma.Sounds;
using ScpItdMod.Core;

namespace ScpItdMod.Hud {
    internal static class TypeSound {
        private const string Path = "Content/Sound/type_click.ogg";
        private static TypeClickBudget budget;
        private static Sound sound;
        private static SoundChannel voice;

        public static void Warm() => sound = SoundStore.File(Path);

        public static void Present(ref TypeClickCursor cursor, TypeText text, int limit, float age, float visibility, double now) {
            bool due = cursor.Advance(text, limit, age);
            if (!due || visibility < 0.05f) { return; }
            float gain = TypeSettings.ClickGain;
            if (gain <= 0.0f || !budget.Try(now)) { return; }
            if (sound == null || sound.Disposed) { Warm(); }
            if (sound == null || sound.Disposed || sound.Loading || voice != null && voice.IsPlaying) { return; }
            voice?.Dispose();
            voice = sound.Play(gain * visibility, SoundManager.SoundCategoryUi);
        }

        public static void Release() {
            voice?.Dispose();
            voice = null;
            sound = null;
            budget = default;
        }
    }
}
