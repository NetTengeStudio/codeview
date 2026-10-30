using System;
using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;
using ScpItdMod.Hud;

namespace ScpItdMod.Ui {
    public sealed class GameFace : IFace {
        private readonly float[] ascii = new float[0x500];

        public GameFace(string key, ScalableFont font, float size, float lineHeight = 0.0f) {
            Key = key;
            Font = font;
            Size = size;
            LineHeight = lineHeight > 0.0f ? lineHeight : MathF.Ceiling(size * 1.25f);
            for (int i = 0; i < ascii.Length; i++) { ascii[i] = -1.0f; }
        }

        public ScalableFont Font { get; }

        public string Key { get; }

        public float Size { get; }

        public float LineHeight { get; }

        public float Advance(int codepoint) {
            if ((uint)codepoint < (uint)ascii.Length) {
                float cached = ascii[codepoint];
                if (cached >= 0.0f) { return cached; }
                return ascii[codepoint] = Font.GetGlyphDataAndTextureForChar((char)codepoint).GlyphData.Advance;
            }
            char c = codepoint > char.MaxValue ? '?' : (char)codepoint;
            return Font.GetGlyphDataAndTextureForChar(c).GlyphData.Advance;
        }
    }

    public static class Faces {
        public const string Body = "Content/Font/DepartureMono.otf";
        public const string Head = "Content/Font/BigBlueTermPlus.ttf";

        private static readonly Dictionary<(string path, uint size, bool system), GameFace> faces = new Dictionary<(string, uint, bool), GameFace>();
        private static readonly Dictionary<string, string> system = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static int generation = -1;
        private static void EnsureGeneration() {
            int current = FontStore.Generation;
            if (generation == current) { return; }
            Release(); generation = current;
        }

        public static GameFace Mod(string relativePath, float px) {
            EnsureGeneration();
            uint size = (uint)Math.Clamp((int)MathF.Round(px), 6, 96);
            var key = (relativePath, size, false);
            if (faces.TryGetValue(key, out GameFace face)) { return face; }
            ScalableFont font = FontStore.File(relativePath, size);
            if (font == null) { return null; }
            face = new GameFace(relativePath + "@" + size, font, size);
            faces[key] = face;
            return face;
        }

        public static GameFace System(string file, string fallback, float px) {
            EnsureGeneration();
            if (!system.TryGetValue(file, out string path)) {
                string windows = global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.Fonts);
                string candidate = string.IsNullOrEmpty(windows) ? "" : global::System.IO.Path.Combine(windows, file);
                path = candidate.Length > 0 && global::System.IO.File.Exists(candidate) ? candidate : "";
                system[file] = path;
            }
            if (path.Length == 0) { return Mod(fallback, px); }
            uint size = (uint)Math.Clamp((int)MathF.Round(px), 6, 96);
            var key = (path, size, true);
            if (faces.TryGetValue(key, out GameFace face)) { return face; }
            ScalableFont font = FontStore.Absolute(path, size);
            if (font == null) { return Mod(fallback, px); }
            face = new GameFace(file + "@" + size, font, size);
            faces[key] = face;
            return face;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ScalableFont, GameFace> wrapped =
            new System.Runtime.CompilerServices.ConditionalWeakTable<ScalableFont, GameFace>();

        public static GameFace Of(ScalableFont font, float lineHeight) {
            EnsureGeneration();
            if (font == null) { return null; }
            if (wrapped.TryGetValue(font, out GameFace face) && face.LineHeight == lineHeight) { return face; }
            face = new GameFace("vanilla@" + font.Size, font, font.Size, lineHeight);
            wrapped.AddOrUpdate(font, face);
            return face;
        }

        private static RetroFaces retro;
        private static float retroScale = -1.0f;

        public static RetroFaces Retro(float scale) {
            EnsureGeneration();
            if (retro != null && retroScale == scale && retro.Body != null && retro.Small != null && retro.Head != null) { return retro; }
            retroScale = scale;
            retro = new RetroFaces {
                Body = Mod(Body, 14.0f * scale),
                Small = Mod(Body, 12.0f * scale),
                Head = Mod(Head, 14.0f * scale)
            };
            return retro;
        }

        public static void Release() {
            faces.Clear(); wrapped.Clear();
            retro = null;
        }
    }

    public sealed class BatchCanvas : ITypeCanvas, IMotionCanvas, IImageCanvas {
        internal static Action<BatchCanvas, Rectangle, bool> ClipObserver;
        private const int RampTexels = 64;

        private readonly Dictionary<long, Texture2D> ramps = new Dictionary<long, Texture2D>();
        private readonly Dictionary<int, Texture2D> arts = new Dictionary<int, Texture2D>();
        private readonly Stack<Rectangle> clips = new Stack<Rectangle>();
        private readonly Color[] rampScratch = new Color[RampTexels];
        private SpriteBatch batch;
        private Rectangle outer;

        public float Scale { get; set; } = 1.0f;

        public float Alpha { get; set; } = 1.0f;

        public SpriteBatch Batch => batch;

        private bool inPlace;
        private bool target;
        private bool subpixel;
        private bool typing;
        private bool motionSampling;
        private static readonly Action FlushMotion = TypeGlyphRenderer.FlushBeforeClip;
        private bool BlurImage(Texture2D texture, in VisualPose pose, Rgba tint, Rendering.MotionBlurShadow shadow = default) {
            if (pose.Path?.Alive != true || !Rendering.MotionBlur.Enabled) { return false; }
            var path = new VisualMotionPath(pose); var region = new Rectangle(0, 0, texture.Width, texture.Height); var origin = new Core.Vec2(.5f, .5f);
            return typing ? Rendering.MotionBlur.Draw(batch.GraphicsDevice, texture, region, Tint(tint), path, origin, shadow, FlushMotion) :
                Rendering.MotionBlur.Draw(batch, texture, region, Tint(tint), path, origin, shadow);
        }
        private void Sampling(bool motion) {
            if (typing || motionSampling == motion) { return; }
            motionSampling = motion; Rescissor(clips.Count > 0 ? clips.Peek() : outer);
        }

        public void BeginTarget(SpriteBatch spriteBatch, bool smooth = false, bool typewriter = false) {
            batch = spriteBatch; motionSampling = false;
            Scale = 1.0f;
            Alpha = 1.0f;
            inPlace = false;
            target = true;
            subpixel = smooth;
            outer = spriteBatch.GraphicsDevice.ScissorRectangle;
            clips.Clear();
            typing = typewriter && TypeGlyphRenderer.Ready(spriteBatch.GraphicsDevice);
            if (typing) { TypeGlyphRenderer.Begin(spriteBatch.GraphicsDevice); return; }
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, smooth ? SamplerState.LinearClamp : SamplerState.PointClamp, null, GameMain.ScissorTestEnable);
        }

        public void Join(SpriteBatch spriteBatch, float scale, bool smooth = false, bool typewriter = false) {
            target = false;
            batch = spriteBatch; motionSampling = false;
            Scale = scale;
            Alpha = 1.0f;
            inPlace = true;
            subpixel = smooth;
            outer = spriteBatch.GraphicsDevice.ScissorRectangle;
            clips.Clear();
            typing = typewriter && TypeGlyphRenderer.Ready(spriteBatch.GraphicsDevice);
            if (typing) { spriteBatch.End(); TypeGlyphRenderer.Begin(spriteBatch.GraphicsDevice); }
        }

        public void Begin(SpriteBatch spriteBatch, float scale) {
            target = false;
            motionSampling = false;
            inPlace = false;
            subpixel = false;
            typing = false;
            batch = spriteBatch; motionSampling = false;
            Scale = scale;
            Alpha = 1.0f;
            HudPaint.BeginPixels(spriteBatch);
            outer = spriteBatch.GraphicsDevice.ScissorRectangle;
            clips.Clear();
        }

        public void End() {
            if (batch == null) { return; }
            if (inPlace && motionSampling && !typing) { Sampling(false); }
            if (typing) {
                try {
                    if (clips.Count > 0) { clips.Clear(); Rescissor(outer); }
                    TypeGlyphRenderer.End();
                } finally {
                    if (inPlace) { batch.Begin(SpriteSortMode.Deferred, null, motionSampling ? SamplerState.LinearClamp : GUI.SamplerState, null, GameMain.ScissorTestEnable); }
                    typing = false;
                    target = false;
                    inPlace = false;
                    batch = null;
                }
                return;
            }
            if (clips.Count > 0) {
                clips.Clear();
                Rescissor(outer);
            }
            if (target) {
                batch.End();
                batch.GraphicsDevice.ScissorRectangle = outer;
            } else if (!inPlace) { HudPaint.End(batch); }
            target = false;
            motionSampling = false;
            inPlace = false;
            batch = null;
        }

        public void Fill(in Box box, Rgba color) {
            Sampling(false);
            if (box.Empty || color.A == 0) { return; }
            if (typing) { TypeGlyphRenderer.Fill(box, color.Fade(Alpha)); return; }
            if (subpixel) {
                batch.Draw(GUI.WhiteTexture, new Vector2(box.X, box.Y), null, Tint(color), 0.0f, Vector2.Zero, new Vector2(box.W, box.H), SpriteEffects.None, 0.0f);
                return;
            }
            batch.Draw(GUI.WhiteTexture, new Rectangle((int)MathF.Round(box.X), (int)MathF.Round(box.Y),
                (int)(MathF.Round(box.X + box.W) - MathF.Round(box.X)), (int)(MathF.Round(box.Y + box.H) - MathF.Round(box.Y))), Tint(color));
        }

        public void Shade(in Box box, Ramp ramp, bool vertical, float alpha) {
            Sampling(false);
            if (box.Empty) { return; }
            Texture2D texture = RampTexture(ramp, vertical);
            if (typing) { TypeGlyphRenderer.Image(texture, box, new Rgba(255, 255, 255).Fade(alpha * Alpha)); return; }
            Box s = box.Snap();
            var source = vertical ? new Rectangle(0, 0, 1, RampTexels) : new Rectangle(0, 0, RampTexels, 1);
            batch.Draw(texture, new Rectangle((int)s.X, (int)s.Y, (int)s.W, (int)s.H), source, Tint(new Rgba(255, 255, 255).Fade(alpha)));
        }

        public void Glyph(IFace face, int codepoint, float x, float y, Rgba color) {
            Sampling(false);
            if (face is not GameFace game || color.A == 0) { return; }
            if (typing) { TypeGlyphRenderer.Glyph(game, codepoint, x, y, color.Fade(Alpha), default); return; }
            char c = codepoint > char.MaxValue ? '?' : (char)codepoint;
            var (data, texture) = game.Font.GetGlyphDataAndTextureForChar(c);
            if (texture == null) { return; }
            Vector2 position = new Vector2(x + data.DrawOffset.X, y + data.DrawOffset.Y);
            if (!subpixel) { position = new Vector2(MathF.Round(position.X), MathF.Round(position.Y)); }
            batch.Draw(texture, position, data.TexCoords, Tint(color));
        }

        public void MovingGlyph(IFace face, int codepoint, float x, float y, Rgba color, in TypeGlyphMotion motion) {
            if (face is not GameFace game || color.A == 0) { return; }
            if (typing) { TypeGlyphRenderer.Glyph(game, codepoint, x, y, color.Fade(Alpha), motion); return; }
            float offset = motion.OffsetAt(motion.Phase);
            Glyph(face, codepoint, x + motion.X * offset, y + motion.Y * offset, color.Fade(motion.Coverage));
        }

        public void Art(PixelArt art, in Box box, Rgba tint) {
            Sampling(false);
            if (art == null || box.Empty) { return; }
            if (!arts.TryGetValue(art.Id, out Texture2D texture) || texture.IsDisposed || !ReferenceEquals(texture.GraphicsDevice, batch.GraphicsDevice)) {
                texture?.Dispose();
                texture = new Texture2D(batch.GraphicsDevice, art.Width, art.Height);
                var data = new Color[art.Pixels.Length];
                for (int i = 0; i < data.Length; i++) { data[i] = new Color(art.Pixels[i]); }
                texture.SetData(data);
                arts[art.Id] = texture;
            }
            Box s = box.Snap();
            if (typing) { TypeGlyphRenderer.Image(texture, box, tint.Fade(Alpha)); return; }
            batch.Draw(texture, new Rectangle((int)s.X, (int)s.Y, (int)s.W, (int)s.H), Tint(tint));
        }

        public void MotionImage(string key, in VisualPose pose, Rgba tint) {
            if (pose.Bounds.Empty || pose.Opacity <= 0 || tint.A == 0) { return; }
            Texture2D texture = TextureStore.File(key);
            if (texture == null) { return; }
            Sampling(true);
            float effectAlpha = pose.Opacity * (tint.A / 255f);
            if (typing) {
                if (pose.Glow > 0.01f) {
                    var glow = new VisualPose(pose.Bounds.Inset(-pose.Glow * 2), pose.Angle, effectAlpha, path: pose.Path, pathScale: pose.PathScale);
                    var glowTint = new Rgba(255, 230, 120, 150);
                    if (!BlurImage(texture, glow, glowTint)) { TypeGlyphRenderer.MotionImage(texture, glow, glowTint.Fade(Alpha)); }
                }
                var shadowMotion = new Rendering.MotionBlurShadow(new Core.Vec2(pose.Shadow * .5f, pose.Shadow), pose.Shadow > 0 ? 65 / 255f * tint.A / 255f * Alpha : 0);
                if (BlurImage(texture, pose, tint, shadowMotion)) { return; }
                if (pose.Shadow > 0) {
                    var shadow = new VisualPose(pose.Bounds.Offset(pose.Shadow * 0.5f, pose.Shadow), pose.Angle, effectAlpha);
                    TypeGlyphRenderer.MotionImage(texture, shadow, new Rgba(0, 0, 0, 65).Fade(Alpha));
                }
                TypeGlyphRenderer.MotionImage(texture, pose, tint.Fade(Alpha)); return;
            }
            Box box = pose.Bounds;
            var position = new Vector2(box.CenterX, box.CenterY);
            var origin = new Vector2(texture.Width * 0.5f, texture.Height * 0.5f);
            var size = new Vector2(box.W / texture.Width, box.H / texture.Height);
            if (pose.Glow > 0.01f) {
                var glowSize = new Vector2((box.W + pose.Glow * 4) / texture.Width, (box.H + pose.Glow * 4) / texture.Height);
                var glow = new VisualPose(pose.Bounds.Inset(-pose.Glow * 2), pose.Angle, effectAlpha, path: pose.Path, pathScale: pose.PathScale);
                if (!BlurImage(texture, glow, new Rgba(255, 230, 120, 150))) { batch.Draw(texture, position, null, Tint(new Rgba(255, 230, 120, 150).Fade(effectAlpha)), pose.Angle, origin, glowSize, SpriteEffects.None, 0); }
            }
            var blurShadow = new Rendering.MotionBlurShadow(new Core.Vec2(pose.Shadow * .5f, pose.Shadow), pose.Shadow > 0 ? 65 / 255f * tint.A / 255f * Alpha : 0);
            if (BlurImage(texture, pose, tint, blurShadow)) { return; }
            if (pose.Shadow > 0) {
                batch.Draw(texture, position + new Vector2(pose.Shadow * 0.5f, pose.Shadow), null,
                    Tint(new Rgba(0, 0, 0, 65).Fade(effectAlpha)), pose.Angle, origin, size, SpriteEffects.None, 0);
            }
            batch.Draw(texture, position, null, Tint(tint.Fade(pose.Opacity)), pose.Angle, origin, size, SpriteEffects.None, 0);
        }

        public bool Image(ICanvasImage image, in Box box, Rgba tint) {
            if (image is not TextureImage source || source.Texture.IsDisposed || box.Empty) { return false; }
            Sampling(true);
            if (typing) { TypeGlyphRenderer.Image(source.Texture, box, tint.Fade(Alpha)); return true; }
            batch.Draw(source.Texture, new Vector2(box.X, box.Y), null, Tint(tint), 0, Vector2.Zero,
                new Vector2(box.W / source.Width, box.H / source.Height), SpriteEffects.None, 0);
            return true;
        }

        public void Image(string key, in Box box, Rgba tint) {
            Sampling(false);
            Texture2D texture = TextureStore.File(key);
            if (texture == null || box.Empty) { return; }
            if (typing) { TypeGlyphRenderer.Image(texture, box, tint.Fade(Alpha)); return; }
            Box s = box.Snap();
            batch.Draw(texture, new Rectangle((int)s.X, (int)s.Y, (int)s.W, (int)s.H), Tint(tint));
        }

        public void Sprite(Barotrauma.Sprite sprite, in Box box, Rgba tint) {
            Sampling(false);
            if (sprite?.Texture == null || box.Empty || sprite.size.X <= 0 || sprite.size.Y <= 0) { return; }
            float scale = MathF.Min(box.W / sprite.size.X, box.H / sprite.size.Y);
            if (typing) {
                var fitted = new Box(box.CenterX - sprite.size.X * scale * 0.5f, box.CenterY - sprite.size.Y * scale * 0.5f, sprite.size.X * scale, sprite.size.Y * scale);
                TypeGlyphRenderer.Image(sprite.Texture, fitted, sprite.SourceRect, tint.Fade(Alpha));
                return;
            }
            sprite.Draw(batch, new Vector2(box.CenterX, box.CenterY), Tint(tint), 0.0f, scale);
        }

        public void BeginWorld(in Box clip, Matrix transform) {
            motionSampling = false;
            Box s = clip.Snap();
            Rectangle parent = clips.Count > 0 ? clips.Peek() : outer;
            Rectangle rect = Rectangle.Intersect(new Rectangle((int)s.X, (int)s.Y, (int)s.W, (int)s.H), parent);
            batch.End();
            batch.GraphicsDevice.ScissorRectangle = rect;
            ClipObserver?.Invoke(this, rect, target);
            batch.Begin(SpriteSortMode.BackToFront, BlendState.NonPremultiplied, SamplerState.LinearClamp, null, GameMain.ScissorTestEnable, null, transform);
        }

        public void EndWorld() => Rescissor(clips.Count > 0 ? clips.Peek() : outer);

        public void PushClip(in Box box) {
            Box s = box.Snap();
            var rect = new Rectangle((int)s.X, (int)s.Y, (int)s.W, (int)s.H);
            Rectangle parent = clips.Count > 0 ? clips.Peek() : outer;
            rect = Rectangle.Intersect(rect, parent);
            clips.Push(rect);
            Rescissor(rect);
        }

        public void PopClip() {
            if (clips.Count == 0) { return; }
            clips.Pop();
            Rescissor(clips.Count > 0 ? clips.Peek() : outer);
        }

        public void Release() {
            foreach (Texture2D t in ramps.Values) { t?.Dispose(); }
            foreach (Texture2D t in arts.Values) { t?.Dispose(); }
            ramps.Clear();
            arts.Clear();
        }

        private void Rescissor(Rectangle rect) {
            if (typing) { TypeGlyphRenderer.FlushBeforeClip(); batch.GraphicsDevice.ScissorRectangle = rect; ClipObserver?.Invoke(this, rect, target); return; }
            batch.End();
            batch.GraphicsDevice.ScissorRectangle = rect;
            ClipObserver?.Invoke(this, rect, target);
            if (inPlace) {
                batch.Begin(SpriteSortMode.Deferred, null, motionSampling ? SamplerState.LinearClamp : GUI.SamplerState, null, GameMain.ScissorTestEnable);
            } else {
                batch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, subpixel || motionSampling ? SamplerState.LinearClamp : SamplerState.PointClamp, null, GameMain.ScissorTestEnable);
            }
        }

        private Color Tint(Rgba c) {
            int alpha = Math.Clamp((int)(c.A * Alpha + 0.5f), 0, 255);
            return new Color(c.WithAlpha(alpha).Packed);
        }

        private Texture2D RampTexture(Ramp ramp, bool vertical) {
            long key = ramp.Id * 2L + (vertical ? 1 : 0);
            if (ramps.TryGetValue(key, out Texture2D texture) && !texture.IsDisposed && ReferenceEquals(texture.GraphicsDevice, batch.GraphicsDevice)) { return texture; }
            texture?.Dispose();
            texture = vertical ? new Texture2D(batch.GraphicsDevice, 1, RampTexels) : new Texture2D(batch.GraphicsDevice, RampTexels, 1);
            for (int i = 0; i < RampTexels; i++) {
                Rgba c = ramp.Sample((i + 0.5f) / RampTexels);
                rampScratch[i] = new Color(c.R, c.G, c.B, c.A);
            }
            texture.SetData(rampScratch);
            ramps[key] = texture;
            return texture;
        }
    }
}
