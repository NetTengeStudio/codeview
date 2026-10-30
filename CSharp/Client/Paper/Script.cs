using System;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;

namespace ScpItdMod.Paper {
    public struct Glyph {
        public Texture2D Texture;
        public Rectangle Source;
        public Vector2 Centre;
        public float Turn;
        public float Size;
        public float Ink;
        public int Index;
    }

    public sealed class Script : IGlyphs {
        public const string Asset = "Content/Font/Caveat.ttf";
        private const uint MetricSize = 96;
        private const int Table = 0x500;
        private const string Warm = " abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.,:;!?-—–()«»\"'/№%+=*" +
            "абвгдеёжзийклмнопрстуфхцчшщъыьэюяАБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ";

        public static readonly Script Hand = new Script(Asset, true);
        public static readonly Script Printed = new Script("Content/Font/SourceCodePro-Semibold.otf", false);
        public static readonly Script Heading = new Script("Content/Font/Exo2-Bold.ttf", false);
        private readonly string fontAsset;
        private readonly bool handwriting;
        private static Glyph[] header = Array.Empty<Glyph>();
        private static ScalableFont headerFont;

        private readonly float[] advances = new float[Table];
        private ScalableFont metric;

        private Script(string asset, bool handwriting) { fontAsset = asset; this.handwriting = handwriting; Array.Fill(advances, float.NaN); }

        public bool Ready => Metric() != null;

        public void Release() {
            metric = null;
            if (ReferenceEquals(this, Heading)) { headerFont = null; header = Array.Empty<Glyph>(); }
            Array.Fill(advances, float.NaN);
        }

        public float Advance(char c) {
            if (c < Table) {
                float known = advances[c];
                if (!float.IsNaN(known)) { return known; }
            }
            ScalableFont font = Metric();
            float advance = font == null ? Sheet.Em * 0.4f : font.GetGlyphDataAndTextureForChar(c).GlyphData.Advance * (Sheet.Em / MetricSize);
            if (c < Table && font != null) { advances[c] = advance; }
            return advance;
        }

        private ScalableFont Metric() {
            if (metric != null) { return metric; }
            metric = FontStore.File(fontAsset, MetricSize);
            metric?.MeasureString(Warm);
            return metric;
        }

        public static uint AtlasFor(float pixelsPerUnit) => MetricSize;

        public int Build(ReadOnlySpan<char> text, InkLayout layout, ulong seed, ReadOnlySpan<uint> grains, uint atlas, ref Glyph[] into) {
            ScalableFont font = FontStore.File(fontAsset, MetricSize);
            if (font == null) { return 0; }
            Metric();
            if (into.Length < text.Length) { into = new Glyph[Math.Max(text.Length, into.Length * 2)]; }
            float k = Sheet.Em / MetricSize;
            int n = 0;
            for (int i = 0; i < text.Length; i++) {
                char c = text[i];
                if (c == '\n' || c == ' ') { continue; }
                (ScalableFont.GlyphData data, Texture2D texture) = font.GetGlyphDataAndTextureForChar(c);
                if (texture == null || data.TexIndex < 0) { continue; }
                int row = layout.Line(i);
                float x = Sheet.BodyLeft + layout.X(i);
                uint h = i < grains.Length ? grains[i] : Hash(seed, (uint)i, c);
                uint lineHash = Hash(seed, 0x9E37u + (uint)row, '\n');
                float drift = handwriting ? (Unit(lineHash) - 0.5f) * 0.9f * (x - Sheet.BodyLeft) / Sheet.BodyWidth : 0;
                float top = Sheet.BodyTop + row * Sheet.Pitch + drift + (handwriting ? (Unit(h) - 0.5f) * 0.25f : 0);
                float scale = k;
                Rectangle src = data.TexCoords;
                into[n++] = new Glyph {
                    Texture = texture,
                    Source = src,
                    Centre = new Vector2(x + (data.DrawOffset.X + src.Width * 0.5f) * k, top + (data.DrawOffset.Y + src.Height * 0.5f) * k),
                    Turn = handwriting ? (Unit(h >> 16) - 0.5f) * 0.01f : 0,
                    Size = scale,
                    Ink = handwriting ? 0.92f + Unit(h >> 24) * 0.08f : 1,
                    Index = i
                };
            }
            return n;
        }

        public static void Draw(SpriteBatch spriteBatch, ReadOnlySpan<Glyph> glyphs, Vector2 origin, float scale, float turn,
            Color ink, ReadOnlySpan<float> born, float now) {
            float cos = MathF.Cos(turn);
            float sin = MathF.Sin(turn);
            for (int i = 0; i < glyphs.Length; i++) {
                ref readonly Glyph g = ref glyphs[i];
                float alpha = g.Ink;
                if ((uint)g.Index < (uint)born.Length) {
                    float age = now - born[g.Index];
                    if (age < InkIn) { alpha *= age <= 0.0f ? 0.0f : age / InkIn; }
                }
                if (alpha <= 0.0f) { continue; }
                float lx = g.Centre.X * scale;
                float ly = g.Centre.Y * scale;
                var at = new Vector2(origin.X + lx * cos - ly * sin, origin.Y + lx * sin + ly * cos);
                spriteBatch.Draw(g.Texture, at, g.Source, Fade(ink, alpha), turn + g.Turn,
                    new Vector2(g.Source.Width * 0.5f, g.Source.Height * 0.5f), g.Size * scale, SpriteEffects.None, 0.0f);
            }
        }

        public static void DrawHeader(SpriteBatch batch, Vector2 origin, float scale, float turn, float alpha = 1) {
            ScalableFont font = Heading.Metric();
            if (font == null) { return; }
            const string title = "Документ";
            const float size = 14;
            if (!ReferenceEquals(headerFont, font)) {
                headerFont = font; header = new Glyph[title.Length];
                float k = size / MetricSize, width = 0;
                for (int i = 0; i < title.Length; i++) { width += font.GetGlyphDataAndTextureForChar(title[i]).GlyphData.Advance * k; }
                float x = Sheet.TitleLeft + (Sheet.TitleRight - Sheet.TitleLeft - width) * 0.5f;
                for (int i = 0; i < title.Length; i++) {
                    var (glyph, texture) = font.GetGlyphDataAndTextureForChar(title[i]); Rectangle source = glyph.TexCoords;
                    header[i] = new Glyph { Texture = texture, Source = source, Size = k, Ink = 1,
                        Centre = new Vector2(x + (glyph.DrawOffset.X + source.Width * 0.5f) * k, Sheet.TitleTop + (glyph.DrawOffset.Y + source.Height * 0.5f) * k) };
                    x += glyph.Advance * k;
                }
            }
            Draw(batch, header, origin, scale, turn, Fade(new Color(62, 62, 58), alpha), ReadOnlySpan<float>.Empty, 0);
        }

        public const float InkIn = 0.11f;

        public static readonly Color InkColor = new Color(28, 36, 88);

        public static void DrawMark(SpriteBatch spriteBatch, in Mark mark, Vector2 origin, float scale, float turn, float alpha, ulong seed) {
            string art = Stamps.Current.ArtOf(mark.Kind);
            Texture2D texture = art == null ? null : TextureStore.File(art);
            if (texture == null || alpha <= 0.0f) { return; }
            float cos = MathF.Cos(turn);
            float sin = MathF.Sin(turn);
            float lx = mark.Left * scale;
            float ly = mark.Top * scale;
            var at = new Vector2(origin.X + lx * cos - ly * sin, origin.Y + lx * sin + ly * cos);
            float press = 0.82f + Unit(Hash(seed, mark.X ^ ((uint)mark.Y << 16), mark.Kind[0])) * 0.16f;
            spriteBatch.Draw(texture, at, null, Fade(Color.White, alpha * press), turn + mark.Angle,
                new Vector2(texture.Width * 0.5f, texture.Height * 0.5f), Sheet.MarkWidth * scale / texture.Width, SpriteEffects.None, 0.0f);
        }

        public static uint Hash(ulong seed, uint index, char c) {
            ulong h = seed ^ (index * 0x9E3779B97F4A7C15UL) ^ ((ulong)c << 32);
            h ^= h >> 33;
            h *= 0xFF51AFD7ED558CCDUL;
            h ^= h >> 33;
            h *= 0xC4CEB9FE1A85EC53UL;
            h ^= h >> 33;
            return (uint)h;
        }

        private static float Unit(uint h) => (h & 0xFF) / 255.0f;

        public static Color Fade(Color color, float alpha) => new Color(color.R, color.G, color.B, (byte)(color.A * Math.Clamp(alpha, 0.0f, 1.0f)));
    }
}
