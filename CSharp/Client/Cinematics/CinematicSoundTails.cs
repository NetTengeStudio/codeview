using System;
using Barotrauma.Sounds;

namespace ScpItdMod.Cinematics {
    internal static class CinematicSoundTails {
        private static readonly SoundChannel[] channels = new SoundChannel[16];
        private static readonly float[] gains = new float[16], ages = new float[16];
        internal const float FadeSeconds = .6f;
        internal static void Release(ref SoundChannel channel) {
            if (channel == null) return;
            int slot = -1; float quietest = float.MaxValue;
            for (int i = 0; i < channels.Length; i++) {
                if (channels[i] == null) { slot = i; break; }
                float gain = gains[i] * MathF.Max(0, 1f - ages[i] / FadeSeconds);
                if (gain < quietest) { quietest = gain; slot = i; }
            }
            channels[slot]?.Dispose(); channels[slot] = channel; gains[slot] = channel.Gain; ages[slot] = 0; channel = null;
        }
        internal static void Step(float dt) {
            for (int i = 0; i < channels.Length; i++) {
                SoundChannel channel = channels[i];
                if (channel == null) continue;
                ages[i] += dt;
                if (!channel.IsPlaying || ages[i] >= FadeSeconds) { channel.Dispose(); channels[i] = null; continue; }
                channel.Gain = gains[i] * (1f - ages[i] / FadeSeconds);
            }
        }
        internal static void Reset() { for (int i = 0; i < channels.Length; i++) { channels[i]?.Dispose(); channels[i] = null; } }
    }
}
