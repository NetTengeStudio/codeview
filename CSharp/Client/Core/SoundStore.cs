using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Sounds;

namespace ScpItdMod.Core {
    public static class SoundStore {
        private static readonly Dictionary<string, Sound> sounds = new Dictionary<string, Sound>();
        private static SoundManager owner;

        public static bool Ready {
            get {
                SoundManager manager = GameMain.SoundManager;
                return manager != null && !manager.Disabled;
            }
        }

        public static Sound File(string relativePath) {
            SoundManager manager = GameMain.SoundManager;
            if (!ReferenceEquals(owner, manager)) { Release(); owner = manager; }
            if (!Ready) { return null; }
            if (sounds.TryGetValue(relativePath, out Sound cached)) {
                Sound live = Live(cached);
                if (live != null) { return live; }
                sounds.Remove(relativePath);
            }
            string absolute = ModAssets.Resolve(relativePath);
            if (absolute.Length == 0) { return null; }
            Sound loaded;
            try {
                loaded = GameMain.SoundManager.LoadSound(absolute);
            } catch (Exception error) {
                ModLog.Once($"{relativePath} cannot be loaded: {error.Message}");
                return null;
            }
            if (loaded == null) { return null; }
            sounds[relativePath] = loaded;
            return loaded;
        }

        public static void Warm(string[] relativePaths) {
            if (!Ready) { return; }
            for (int i = 0; i < relativePaths.Length; i++) { File(relativePaths[i]); }
        }

        public static void Release() {
            foreach (KeyValuePair<string, Sound> entry in sounds) { entry.Value?.Dispose(); }
            sounds.Clear();
            owner = null;
        }

        private static Sound Live(Sound sound) => sound != null && !sound.Disposed ? sound : null;
    }
}
