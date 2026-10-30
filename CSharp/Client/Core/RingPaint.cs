using System;
using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Inv;

namespace ScpItdMod.Core {
    public static class RingPaint {
        private const float WedgeMargin = 2.0f;
        private const float DiscMargin = 4.0f;

        private static readonly Dictionary<SectorSpec, Texture2D> sectors = new Dictionary<SectorSpec, Texture2D>();
        private static readonly Dictionary<int, Texture2D> fills = new Dictionary<int, Texture2D>();
        private static readonly Dictionary<int, Texture2D> discs = new Dictionary<int, Texture2D>();

        // Textures are looked up by a struct key every frame; the string key TextureStore wants is built only on a miss.
        private static Texture2D Lookup<TKey>(Dictionary<TKey, Texture2D> cache, TKey key, Func<TKey, string> name, Func<TKey, Texture2D> build) {
            if (cache.TryGetValue(key, out Texture2D cached) && cached != null && !cached.IsDisposed &&
                ReferenceEquals(cached.GraphicsDevice, GameMain.Instance?.GraphicsDevice)) { return cached; }
            Texture2D made = TextureStore.Generated(name(key), key, build);
            cache[key] = made;
            return made;
        }

        private static Texture2D Fill(int side) => Lookup(fills, side, k => "ring-fill:" + k, BuildWedge);

        private static Texture2D Disc(int side) => Lookup(discs, side, k => "ring-disc:" + k, BuildDisc);

        public static void Wedge(SpriteBatch spriteBatch, in InvRing ring, int slot, Color color, float spread) {
            int side = 2 * (int)MathF.Ceiling(ring.Outer) + 4;
            Texture2D fill = Fill(side);
            if (fill == null) { return; }
            float scale = spread * ring.Outer / (side * 0.5f - WedgeMargin);
            spriteBatch.Draw(fill, new Vector2(ring.X, ring.Y), null, color, InvRing.Angle(slot), new Vector2(side * 0.5f), scale, SpriteEffects.None, 0.0f);
        }

        // Draws one band sector of the container deck. The texture is the sector at angle 0,
        // pivoted on the ring centre, so the Motion handles only rotate and scale it.
        public static void Sector(SpriteBatch spriteBatch, Vector2 ringCentre, in BandSlot band, float rotation, float radialScale, Color color) =>
            DrawSector(spriteBatch, ringCentre, band, rotation, radialScale, color, false);

        // The same sector as a hairline just inside its edge, for hover and drop-target emphasis.
        public static void Rim(SpriteBatch spriteBatch, Vector2 ringCentre, in BandSlot band, float rotation, float radialScale, Color color) =>
            DrawSector(spriteBatch, ringCentre, band, rotation, radialScale, color, true);

        private static void DrawSector(SpriteBatch spriteBatch, Vector2 ringCentre, in BandSlot band, float rotation, float radialScale, Color color, bool rim) {
            var spec = new SectorSpec(band.Inner, band.Outer, band.Half, band.Gap, rim);
            Texture2D texture = Lookup(sectors, spec, SectorName, BuildSector);
            if (texture == null) { return; }
            var pivot = new Vector2(-spec.X0, spec.HalfHeight);
            spriteBatch.Draw(texture, ringCentre, null, color, rotation, pivot, radialScale, SpriteEffects.None, 0.0f);
        }

        // A filled dot, used by the pager. `radius` is in pixels.
        public static void Dot(SpriteBatch spriteBatch, Vector2 at, float radius, Color color) {
            const int side = 48;
            Texture2D disc = Disc(side);
            if (disc == null) { return; }
            spriteBatch.Draw(disc, at, null, color, 0.0f, new Vector2(side * 0.5f), radius / (side * 0.5f - DiscMargin), SpriteEffects.None, 0.0f);
        }

        public static void Hub(SpriteBatch spriteBatch, in InvRing ring, float spread, float alpha) {
            int side = 2 * (int)MathF.Ceiling(ring.HubRadius) + 8;
            Texture2D disc = Disc(side);
            if (disc == null) { return; }
            float hub = ring.HubRadius * spread;
            float unit = side * 0.5f - DiscMargin;
            var centre = new Vector2(ring.X, ring.Y);
            var origin = new Vector2(side * 0.5f);
            spriteBatch.Draw(disc, centre, null, Color.White * (0.12f * alpha), 0.0f, origin, (hub + 2.0f) / unit, SpriteEffects.None, 0.0f);
            spriteBatch.Draw(disc, centre, null, Color.Black * (0.65f * alpha), 0.0f, origin, hub / unit, SpriteEffects.None, 0.0f);
        }

        public static Texture2D Mask(string key, Texture2D source) => TextureStore.Generated("mask:" + key, source, BuildAlphaMask);

        private const float SectorPad = 3.0f;

        // Texture-space description of one band sector. Pixels map 1:1 to ring-centred coordinates.
        private static string SectorName(SectorSpec spec) =>
            (spec.Rim ? "orbit-rim:" : "orbit-sector:") + spec.Inner.ToString("F1") + ":" + spec.Outer.ToString("F1") + ":" + spec.Half.ToString("F4") + ":" + spec.Gap.ToString("F2");

        public readonly struct SectorSpec : IEquatable<SectorSpec> {
            public readonly float Inner, Outer, Half, Gap;
            public readonly int X0, Width, HalfHeight;
            public readonly bool Rim;

            public SectorSpec(float inner, float outer, float half, float gap, bool rim) {
                Inner = inner; Outer = outer; Half = half; Gap = gap; Rim = rim;
                X0 = (int)MathF.Floor(inner - SectorPad);
                Width = (int)MathF.Ceiling(outer + SectorPad) - X0;
                HalfHeight = (int)MathF.Ceiling(outer * MathF.Sin(half) + SectorPad);
            }

            public bool Equals(SectorSpec other) => Inner == other.Inner && Outer == other.Outer && Half == other.Half && Gap == other.Gap && Rim == other.Rim;

            public override bool Equals(object other) => other is SectorSpec spec && Equals(spec);

            public override int GetHashCode() => HashCode.Combine(Inner, Outer, Half, Gap, Rim);
        }

        private static Texture2D BuildSector(SectorSpec spec) {
            GraphicsDevice device = GameMain.Instance?.GraphicsDevice;
            if (device == null) { return null; }
            int width = spec.Width, height = spec.HalfHeight * 2;
            var pixels = new Color[width * height];
            for (int py = 0; py < height; py++) {
                float y = py + 0.5f - spec.HalfHeight;
                for (int px = 0; px < width; px++) {
                    float x = spec.X0 + px + 0.5f;
                    float d = BandSlot.SectorDistance(x, y, spec.Inner, spec.Outer, spec.Half, spec.Gap);
                    float cover = spec.Rim ? BandSlot.RimCoverage(d) : 0.5f - d;
                    byte v = Scalar.ToByte(Scalar.Clamp01(cover));
                    pixels[py * width + px] = new Color(v, v, v, v);
                }
            }
            var texture = new Texture2D(device, width, height);
            texture.SetData(pixels);
            return texture;
        }

        private static Texture2D BuildWedge(int side) => BuildMask(side, InvRing.WedgeDistance, WedgeMargin);

        private static Texture2D BuildDisc(int side) => BuildMask(side, InvRing.DiscDistance, DiscMargin);

        private static Texture2D BuildMask(int side, Func<float, float, float> distance, float margin) {
            GraphicsDevice device = GameMain.Instance?.GraphicsDevice;
            if (device == null) { return null; }
            var pixels = new Color[side * side];
            float half = side * 0.5f;
            float texel = 1.0f / (half - margin);
            for (int py = 0; py < side; py++) {
                float y = (py + 0.5f - half) * texel;
                for (int px = 0; px < side; px++) {
                    float x = (px + 0.5f - half) * texel;
                    byte v = Scalar.ToByte(Scalar.Clamp01(0.5f - distance(x, y) / texel));
                    pixels[py * side + px] = new Color(v, v, v, v);
                }
            }
            var texture = new Texture2D(device, side, side);
            texture.SetData(pixels);
            return texture;
        }

        private static Texture2D BuildAlphaMask(Texture2D source) {
            GraphicsDevice device = GameMain.Instance?.GraphicsDevice;
            if (device == null || source == null || source.IsDisposed) { return null; }
            var pixels = new Color[source.Width * source.Height];
            source.GetData(pixels);
            for (int i = 0; i < pixels.Length; i++) {
                byte a = pixels[i].A;
                pixels[i] = new Color(a, a, a, a);
            }
            var texture = new Texture2D(device, source.Width, source.Height);
            texture.SetData(pixels);
            return texture;
        }
    }
}
