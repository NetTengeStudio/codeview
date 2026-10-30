using System;
using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework.Graphics;

namespace ScpItdMod.Core {
    public static class FontStore {
        public const uint Atlas = 160;
        private static GraphicsDevice device;
        private static int generation;
        public static int Generation { get { EnsureDevice(); return generation; } }
        private static void EnsureDevice() {
            GraphicsDevice current = GameMain.Instance?.GraphicsDevice;
            if (ReferenceEquals(current, device)) { return; }
            Release(); device = current;
        }
        internal static IEnumerable<string> LoadedPaths => fonts.Keys;
        internal static IEnumerable<string> RejectedPaths => rejected.Keys;

        private static readonly Dictionary<string, ScalableFont> fonts =
            new Dictionary<string, ScalableFont>();
        private static readonly Dictionary<string, double> rejected = new Dictionary<string, double>();

        private static bool Rejected(string key) => rejected.TryGetValue(key, out double retryAt) && Timing.TotalTime < retryAt;

        public static ScalableFont File(string relativePath) => File(relativePath, Atlas);

        public static ScalableFont File(string relativePath, uint size) {
            EnsureDevice();
            string key = size == Atlas ? relativePath : relativePath + "@" + size;
            if (fonts.TryGetValue(key, out ScalableFont cached)) { return cached; }
            if (Rejected(key)) { return null; }
            if (GameMain.Instance?.GraphicsDevice == null) { return null; }
            string absolute = ModAssets.Resolve(relativePath);
            if (absolute.Length == 0) { return null; }
            ScalableFont loaded;
            try {
                loaded = new ScalableFont(absolute, size, GameMain.Instance.GraphicsDevice,
                    dynamicLoading: true, TextManager.SpeciallyHandledCharCategory.Cyrillic);
            } catch (Exception error) {
                rejected[key] = Timing.TotalTime + 2;
                ModLog.Once($"{relativePath} cannot be loaded: {error.Message}");
                return null;
            }
            rejected.Remove(key);
            fonts[key] = loaded;
            return loaded;
        }

        public static ScalableFont Absolute(string absolutePath, uint size) {
            EnsureDevice();
            string key = "abs:" + absolutePath + "@" + size;
            if (fonts.TryGetValue(key, out ScalableFont cached)) { return cached; }
            if (Rejected(key) || GameMain.Instance?.GraphicsDevice == null) { return null; }
            ScalableFont loaded;
            try {
                loaded = new ScalableFont(absolutePath, size, GameMain.Instance.GraphicsDevice,
                    dynamicLoading: true, TextManager.SpeciallyHandledCharCategory.Cyrillic);
            } catch (Exception error) {
                rejected[key] = Timing.TotalTime + 2;
                ModLog.Once($"{absolutePath} cannot be loaded: {error.Message}");
                return null;
            }
            rejected.Remove(key);
            fonts[key] = loaded;
            return loaded;
        }

        public static void Release() {
            generation++; device = null;
            foreach (KeyValuePair<string, ScalableFont> entry in fonts) { entry.Value?.Dispose(); }
            fonts.Clear();
            rejected.Clear();
        }
    }
}
