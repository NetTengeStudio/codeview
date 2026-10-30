using System;
using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Motion;

namespace ScpItdMod.Cinematics {
    internal static class CinematicOverlay {
        private const int MaximumTitles = 4;
        private const string TypeSound = "@Primary package/Sounds/pp_type.ogg";
        private const float SnapSeconds = .15f, SlideLines = .35f, DesignHeight = 1080f, SmallScale = .5f, LeftMargin = .07f, TypeGain = .35f, TypePitchSpread = .06f, TypeCharsPerSecond = 13f, TypeSoftChars = 2.5f;
        private static readonly Color ink = new Color(238, 242, 248), shadow = new Color(0, 0, 0);
        private static readonly List<Title> titles = new List<Title>(MaximumTitles);
        private static MotionScope scope;
        private static MotionHandle<float> bars, fade;
        private static float aspect = 2.39f;

        private sealed class Title {
            internal string[] Lines;
            internal string[][] Glyphs;
            internal float[][] PrefixWidths;
            internal float[] Widths;
            internal bool[] Small;
            internal ScalableFont MeasuredFont;
            internal uint MeasuredSize;
            internal bool MeasuredUpperCase, MetricsReady;
            internal float RowHeight;
            internal bool Left;
            internal double Started;
            internal int Clicked;
            internal float Scale, Place;
            internal double Release;
            internal float FadeSeconds;
            internal bool Releasing;
            internal MotionScope Scope;
            internal MotionHandle<float> Alpha, Slide;
        }

        private static bool Live => scope != null && bars.Alive && fade.Alive;

        internal static bool Active => Live && (bars.Value > .001f || fade.Value > .001f || titles.Count > 0);

        private static void Ensure() {
            if (Live) return;
            Reset();
            scope = MotionClient.Runtime.Scope();
            bars = scope.Scalar(0); fade = scope.Scalar(0);
        }

        private static void Drive(MotionHandle<float> handle, float target, float seconds) {
            if (seconds < SnapSeconds || MotionClient.Runtime.ReducedMotion) handle.Snap(target);
            else handle.To(target, MotionTransition.Tween(seconds, MotionKind.Smoother));
        }

        internal static void Apply(CinematicCue cue, double age) {
            Ensure();
            switch (cue.Action) {
                case CinematicAction.Letterbox:
                    aspect = cue.Intensity;
                    Drive(bars, cue.Enabled ? 1f : 0f, (float)Math.Max(0, cue.Duration - age));
                    break;
                case CinematicAction.Fade:
                    Drive(fade, cue.Enabled ? MathF.Min(1f, cue.Intensity) : 0f, (float)Math.Max(0, cue.Duration - age));
                    break;
                case CinematicAction.Title:
                    if (!CinematicTitleMath.Visible(age, cue.Duration)) break;
                    if (titles.Count >= MaximumTitles) Release(titles[0], true);
                    string text = cue.Value; bool left = text.StartsWith("<", StringComparison.Ordinal); if (left) text = text.Substring(1);
                    var title = new Title { Started = MotionClient.Runtime.Time - age, Left = left, Lines = text.Split('|'), Scale = cue.Zoom, Place = cue.FrameY, FadeSeconds = cue.Blend, Release = MotionClient.Runtime.Time + cue.Duration - age - cue.Blend, Scope = MotionClient.Runtime.Scope() };
                    title.Small = new bool[title.Lines.Length];
                    title.Glyphs = new string[title.Lines.Length][];
                    title.PrefixWidths = new float[title.Lines.Length][];
                    title.Widths = new float[title.Lines.Length];
                    for (int line = 0; line < title.Lines.Length; line++) {
                        string trimmed = title.Lines[line].Trim();
                        title.Small[line] = trimmed.StartsWith("~", StringComparison.Ordinal);
                        string value = title.Lines[line] = title.Small[line] ? trimmed.Substring(1).Trim() : trimmed;
                        title.Glyphs[line] = new string[value.Length];
                        title.PrefixWidths[line] = new float[value.Length];
                        for (int c = 0; c < value.Length; c++) title.Glyphs[line][c] = value.Substring(c, 1);
                    }
                    title.Alpha = title.Scope.Scalar(CinematicTitleMath.Opacity(age, cue.Duration, cue.Blend));
                    float slideSeconds = MathF.Max(.4f, cue.Blend * 1.4f);
                    title.Slide = title.Scope.Scalar(1f - CinematicTitleMath.Ease(age, slideSeconds));
                    if (age < cue.Blend) title.Alpha.To(1, MotionTransition.Tween((float)(cue.Blend - age), MotionKind.Smoother));
                    if (age < slideSeconds) title.Slide.To(0, MotionTransition.Tween((float)(slideSeconds - age), MotionKind.Smoother));
                    if (age >= cue.Duration - cue.Blend) {
                        title.Releasing = true; title.Release = MotionClient.Runtime.Time + cue.Duration - age;
                        title.Alpha.To(0, MotionTransition.Tween((float)(cue.Duration - age), MotionKind.Smoother));
                    }
                    titles.Add(title);
                    break;
            }
        }

        private static void Release(Title title, bool immediate) {
            if (title.Releasing && !immediate) return;
            title.Releasing = true;
            if (immediate) { title.Scope.Dispose(); titles.Remove(title); return; }
            float seconds = MathF.Max(.2f, title.FadeSeconds);
            title.Alpha.To(0, MotionTransition.Tween(seconds, MotionKind.Smoother));
            title.Release = MotionClient.Runtime.Time + seconds;
        }

        internal static void Draw(SpriteBatch __1) {
            if (!Live) return;
            try { Render(__1); }
            catch (Exception error) { ScpItdMod.Core.ModLog.Once("Cinematic overlay recovered: " + error.Message); Reset(); }
        }

        private static void Render(SpriteBatch batch) {
            double now = MotionClient.Runtime.Time;
            for (int i = titles.Count - 1; i >= 0; i--) {
                Title title = titles[i];
                TypeSounds(title, now);
                if (!title.Releasing && now >= title.Release) Release(title, false);
                else if (title.Releasing && now >= title.Release && title.Alpha.Value < .01f) Release(title, true);
            }
            float thickness = bars.Value * MathF.Max(0f, (GameMain.GraphicsHeight - GameMain.GraphicsWidth / aspect) * .5f);
            float darkness = fade.Value;
            if (thickness < .5f && darkness < .002f && titles.Count == 0) return;
            if (darkness > .002f) GUI.DrawRectangle(batch, new Rectangle(0, 0, GameMain.GraphicsWidth, GameMain.GraphicsHeight), Color.Black * darkness, isFilled: true);
            if (thickness >= .5f) {
                int band = (int)MathF.Ceiling(thickness);
                GUI.DrawRectangle(batch, new Rectangle(0, 0, GameMain.GraphicsWidth, band), Color.Black, isFilled: true);
                GUI.DrawRectangle(batch, new Rectangle(0, GameMain.GraphicsHeight - band, GameMain.GraphicsWidth, band), Color.Black, isFilled: true);
            }
            foreach (Title title in titles) DrawTitle(batch, title);
        }

        private static readonly Random pitch = new Random(17);

        private static void TypeSounds(Title title, double now) {
            if (title.Releasing) return;
            int total = 0;
            for (int l = 0; l < title.Lines.Length; l++) total += title.Lines[l].Length;
            int reached = Math.Min(total, (int)((now - title.Started) * TypeCharsPerSecond));
            if (reached <= title.Clicked) return;
            title.Clicked = reached;
            Barotrauma.Sounds.SoundChannel channel = ScpItdMod.Core.SoundStore.File(TypeSound)?.Play(TypeGain);
            if (channel != null) channel.FrequencyMultiplier = 1f + ((float)pitch.NextDouble() * 2f - 1f) * TypePitchSpread;
        }

        private static void DrawTitle(SpriteBatch batch, Title title) {
            float alpha = Math.Clamp(title.Alpha.Value, 0f, 1f);
            if (alpha <= .002f) return;
            GUIFont font = GUIStyle.LargeFont;
            MeasureTitle(title, font);
            float unit = GameMain.GraphicsHeight / DesignHeight, baseScale = title.Scale * unit * 1.35f;
            float rowHeight = title.RowHeight * baseScale;
            float total = 0;
            for (int l = 0; l < title.Lines.Length; l++) total += rowHeight * (title.Small[l] ? SmallScale : 1f) * 1.2f;
            float y = GameMain.GraphicsHeight * title.Place - total * .5f;
            float typed = (float)(MotionClient.Runtime.Time - title.Started) * TypeCharsPerSecond;
            for (int l = 0; l < title.Lines.Length; l++) {
                bool small = title.Small[l];
                string text = title.Lines[l];
                float scale = baseScale * (small ? SmallScale : 1f), height = rowHeight * (small ? SmallScale : 1f);
                float width = title.Widths[l] * scale;
                float x0 = title.Left ? GameMain.GraphicsWidth * LeftMargin : (GameMain.GraphicsWidth - width) * .5f;
                for (int c = 0; c < text.Length; c++) {
                    float reveal = Math.Clamp((typed - c) / TypeSoftChars, 0f, 1f);
                    if (reveal <= 0f) break;
                    if (text[c] == ' ') continue;
                    float soft = reveal * reveal * (3f - 2f * reveal), line = alpha * soft * (small ? .85f : 1f);
                    var at = new Vector2(x0 + title.PrefixWidths[l][c] * scale, y + (1f - soft) * height * .15f);
                    string glyph = title.Glyphs[l][c];
                    font.DrawString(batch, glyph, at + new Vector2(unit * 2f, unit * 2f), shadow * (line * .8f), 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
                    font.DrawString(batch, glyph, at, ink * line, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
                }
                typed -= text.Length;
                y += height * 1.2f;
            }
        }

        private static void MeasureTitle(Title title, GUIFont font) {
            ScalableFont selected = font.Value;
            uint size = selected?.Size ?? 0;
            bool upperCase = selected?.ForceUpperCase ?? false;
            if (title.MetricsReady && ReferenceEquals(title.MeasuredFont, selected) && title.MeasuredSize == size && title.MeasuredUpperCase == upperCase) return;
            title.RowHeight = font.MeasureString("Ag").Y;
            for (int line = 0; line < title.Lines.Length; line++) {
                string text = title.Lines[line];
                title.Widths[line] = font.MeasureString(text).X;
                for (int c = 0; c < text.Length; c++) title.PrefixWidths[line][c] = font.MeasureString(text.Substring(0, c)).X;
            }
            title.MeasuredFont = selected;
            title.MeasuredSize = size;
            title.MeasuredUpperCase = upperCase;
            title.MetricsReady = true;
        }

        internal static void Clear() {
            if (!Live) return;
            Drive(bars, 0f, 1.2f); Drive(fade, 0f, .9f);
            for (int i = titles.Count - 1; i >= 0; i--) Release(titles[i], false);
        }

        internal static void Reset() {
            foreach (Title title in titles) title.Scope.Dispose();
            titles.Clear();
            if (scope != null) { scope.Dispose(); scope = null; }
        }
    }
}
