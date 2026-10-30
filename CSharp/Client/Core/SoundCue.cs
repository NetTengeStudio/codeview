using System;
using Barotrauma;
using Barotrauma.Sounds;
using Microsoft.Xna.Framework;

namespace ScpItdMod.Core {
    public sealed class SoundCue {
        private const int HistoryShare = 3;

        private readonly string[] paths;
        private readonly int[] recent;

        private SoundChannel channel;
        private int recentCount;
        private int recentHead;

        public SoundCue(params string[] relativePaths) {
            paths = relativePaths;
            recent = new int[Math.Max(1, relativePaths.Length / HistoryShare)];
        }

        public bool Playing => channel != null && channel.IsPlaying;

        public float Gain {
            set { if (Playing) { channel.Gain = value; } }
        }

        public void Warm() => SoundStore.Warm(paths);

        public void Play(float gain) => Start(gain, null, 0.0f);

        public void Play(float gain, Vector2 worldPosition, float range) =>
            Start(gain, worldPosition, range);

        public void Loop(float gain) {
            if (Playing) { return; }
            Start(gain, null, 0.0f);
            if (channel != null) { channel.Looping = true; }
        }

        public void PlayOnce(float gain, Vector2 worldPosition, float range, float pitch, Hull hullGuess) {
            Sound sound = Variant();
            if (sound == null) { return; }
            SoundPlayer.PlaySound(sound, worldPosition, gain, range, pitch, hullGuess);
        }

        public void Stop() {
            channel?.Dispose();
            channel = null;
        }

        public void Release() {
            Stop();
            recentCount = 0;
            recentHead = 0;
        }

        private void Start(float gain, Vector2? worldPosition, float range) {
            if (Playing) { return; }
            Stop();
            Sound sound = Variant();
            if (sound == null) { return; }
            channel = worldPosition.HasValue
                ? sound.Play(gain, range, worldPosition.Value)
                : sound.Play(gain);
        }

        private Sound Variant() {
            int picked = Pick();
            Sound sound = SoundStore.File(paths[picked]);
            if (sound != null) { return sound; }
            for (int i = 0; i < paths.Length; i++) {
                if (i == picked) { continue; }
                sound = SoundStore.File(paths[i]);
                if (sound == null) { continue; }
                Remember(i);
                return sound;
            }
            return null;
        }

        private int Pick() {
            if (paths.Length <= 1) { return 0; }
            int slot = Rand.Int(paths.Length - recentCount, Rand.RandSync.Unsynced);
            for (int i = 0; i < paths.Length; i++) {
                if (Heard(i)) { continue; }
                if (slot > 0) { slot--; continue; }
                Remember(i);
                return i;
            }
            return 0;
        }

        private bool Heard(int index) {
            for (int i = 0; i < recentCount; i++) {
                if (recent[i] == index) { return true; }
            }
            return false;
        }

        private void Remember(int index) {
            if (Heard(index)) { return; }
            recent[recentHead] = index;
            recentHead = (recentHead + 1) % recent.Length;
            if (recentCount < recent.Length) { recentCount++; }
        }
    }
}
