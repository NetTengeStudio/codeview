using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using ScpItdMod.Core;

namespace ScpItdMod.Rec {
    public static class VoiceOps {
        private static bool broken;

        public static bool Broken => broken;

        public static object Create(VoiceStore store, double from, double to, Subject? only, List<VoicePacket> packets, List<byte[]> owners) {
            if (broken) { return null; }
            try {
                return Make(store, from, to, only, packets, owners);
            } catch (Exception error) when (error is TypeLoadException || error is MissingMethodException || error is MethodAccessException || error is TypeAccessException) {
                broken = true;
                ModLog.Error($"recorded voice playback is unavailable: {error.Message}");
                return null;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static object Make(VoiceStore store, double from, double to, Subject? only, List<VoicePacket> packets, List<byte[]> owners) =>
            GhostVoice.Create(store, from, to, only, packets, owners);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Playing(object voice) => voice is GhostVoice v && v.Playing;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Start(object voice, Vector2? at, float range) => (voice as GhostVoice)?.Start(at, range, muffle: false);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Move(object voice, Vector2 at) => (voice as GhostVoice)?.Move(at);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Dispose(object voice) => (voice as GhostVoice)?.Dispose();
    }
}
