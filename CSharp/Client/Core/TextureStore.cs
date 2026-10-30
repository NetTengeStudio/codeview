using System;
using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ScpItdMod.Core {
    public static class TextureStore {
        private static readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>();
        private static readonly Dictionary<string, Sprite> sprites = new Dictionary<string, Sprite>();
        private static readonly Dictionary<string, double> rejected = new Dictionary<string, double>();
        internal static IEnumerable<string> LoadedPaths => textures.Keys;
        internal static IEnumerable<string> RejectedPaths => rejected.Keys;
        private static bool Rejected(string path) => rejected.TryGetValue(path, out double time) && Timing.TotalTime < time;

        public static Texture2D File(string relativePath) {
            if (textures.TryGetValue(relativePath, out Texture2D cached)) {
                Texture2D live = Live(cached);
                if (live != null) { return live; }
                textures.Remove(relativePath); cached?.Dispose();
            }
            if (Rejected(relativePath)) { return null; }
            if (TextureLoader.PlaceHolderTexture == null) { return null; }
            string absolute = ModAssets.Resolve(relativePath);
            if (absolute.Length == 0) { return null; }
            Texture2D loaded;
            try {
                loaded = TextureLoader.FromFile(absolute, compress: false);
            } catch (Exception) {
                Reject(relativePath);
                return null;
            }
            if (loaded == null) {
                Reject(relativePath);
                return null;
            }
            rejected.Remove(relativePath);
            textures[relativePath] = loaded;
            return loaded;
        }

        public static Texture2D Generated<TSource>(string key, TSource source, Func<TSource, Texture2D> build) {
            if (textures.TryGetValue(key, out Texture2D cached)) {
                Texture2D live = Live(cached);
                if (live != null) { return live; }
            }
            if (cached != null) { cached.Dispose(); }
            Texture2D created = build(source);
            if (created == null) { return null; }
            textures[key] = created;
            return created;
        }

        public static Sprite Art(string relativePath) => Art(relativePath, null);

        public static Sprite Art(string relativePath, Rectangle? source) {
            string key = source.HasValue ? relativePath + source.Value : relativePath;
            if (sprites.TryGetValue(key, out Sprite cached) && Live(cached.Texture) != null) {
                return cached;
            }
            if (cached != null) { sprites.Remove(key); cached.Remove(); }
            if (Rejected(relativePath)) { return null; }
            if (TextureLoader.PlaceHolderTexture == null) { return null; }
            string absolute = ModAssets.Resolve(relativePath);
            if (absolute.Length == 0) { return null; }
            Sprite built = new Sprite(absolute, source, null);
            if (built.Texture == null) {
                built.Remove();
                Reject(relativePath);
                return null;
            }
            rejected.Remove(relativePath);
            sprites[key] = built;
            return built;
        }

        private static void Reject(string relativePath) {
            rejected[relativePath] = Timing.TotalTime + 2;
            ModLog.Once($"{relativePath} cannot be loaded");
        }

        public static void Release() {
            foreach (KeyValuePair<string, Texture2D> entry in textures) { entry.Value?.Dispose(); }
            textures.Clear();
            foreach (KeyValuePair<string, Sprite> entry in sprites) { entry.Value?.Remove(); }
            sprites.Clear();
            rejected.Clear();
        }

        private static Texture2D Live(Texture2D texture) =>
            texture != null && !texture.IsDisposed && ReferenceEquals(texture.GraphicsDevice, GameMain.Instance?.GraphicsDevice) ? texture : null;
    }
}
