using System;
using System.Collections.Generic;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;
using ScpItdMod.Motion;
using ScpItdMod.Ui;

namespace ScpItdMod.Hud {
    public static class Speech {
        public const int MaxTalkers = 24;
        public const int PerTalker = 3;
        public const int MaxLines = 6;
        private const float WidthPx = 300.0f;
        private const float PadPx = 5.0f;
        private const float GapPx = 4.0f;
        private const float HeadWorld = 34.0f;
        private const float FadeSeconds = 0.45f;
        private const float StackOmega = 22.0f;
        private const float ShowOmega = 16.0f;

        private sealed class Line {
            public TypeText Text;
            public readonly TypeLayout Layout = new TypeLayout();
            public double Received;
            public TypePresentation Presentation;
            public TypeClickCursor Clicks;
            public Rgba Ink;
            public MotionScope Motion;
            public MotionHandle<float> Bottom;
            public bool Placed;
            public float Width;
            public float MaxWidth;
            public GameFace Face;
        }

        private sealed class Talker {
            public Character Who;
            public object Key;
            public bool Seen;
            public Vector2 Anchor;
            public bool Gone;
            public double GoneAt;
            public MotionScope Motion;
            public MotionHandle<float> Shown;
            public readonly Line[] Lines = new Line[PerTalker];
            public int Count;
        }

        private static readonly List<Talker> talkers = new List<Talker>(MaxTalkers);
        private static readonly BatchCanvas canvas = new BatchCanvas();
        private static double lastPaint;

        public static int Active => talkers.Count;

        public static void Attach(Harmony harmony) {
            harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.ShowSpeechBubble), new[] { typeof(Color), typeof(string) }),
                prefix: ModuleHost.Bind(typeof(Speech), nameof(ShowSpeechBubblePrefix)));
            harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.DrawSpeechBubbles)),
                postfix: ModuleHost.Bind(typeof(Speech), nameof(DrawSpeechBubblesPostfix)));
        }

        public static void Release() {
            for (int i = 0; i < talkers.Count; i++) { Talker t = talkers[i]; t.Motion?.Dispose(); for (int k = 0; k < t.Count; k++) { t.Lines[k]?.Motion?.Dispose(); } }
            talkers.Clear();
            canvas.Release();
            lastPaint = 0.0;
        }

        public static bool ShowSpeechBubblePrefix(Character __instance, Color color, string text) {
            if (__instance == null || !__instance.IsHuman || __instance.Removed || __instance.IsDead || !GameSettings.CurrentConfig.ChatSpeechBubbles) { return true; }
            if (Push(__instance, null, text, new Rgba(color.R, color.G, color.B, color.A)) == null) { return true; }
            __instance.textlessSpeechBubble = null;
            return false;
        }

        public static void Say(Character speaker, string text) {
            if (speaker == null) { return; }
            Push(speaker, null, text, Retro.Bright);
        }

        public static void SayAt(object key, Vector2 anchor, string text) {
            if (key == null) { return; }
            Talker t = Push(null, key, text, Retro.Bright);
            if (t == null) { return; }
            t.Anchor = anchor;
            t.Seen = true;
        }

        public static void Hold(object key, Vector2 anchor, bool seen) {
            Talker t = FindKey(key);
            if (t == null) { return; }
            t.Anchor = anchor;
            t.Seen = seen;
        }

        public static void Drop(object key) {
            Talker t = FindKey(key);
            if (t == null || t.Gone) { return; }
            t.Gone = true;
            t.GoneAt = HudClock.Now;
        }

        private static Talker FindKey(object key) {
            for (int i = 0; i < talkers.Count; i++) {
                if (key != null && ReferenceEquals(talkers[i].Key, key)) { return talkers[i]; }
            }
            return null;
        }

        private static Talker Push(Character speaker, object key, string text, Rgba ink) {
            if (string.IsNullOrWhiteSpace(text)) { return null; }
            TypeText typed = TypeText.Parse(text.Trim(), TypeSettings.Pace, markup: true);
            if (typed.Count == 0) { return null; }
            double now = HudClock.Now;
            var line = new Line { Text = typed, Received = now, Ink = ink };
            line.Motion = MotionClient.Runtime.Scope();
            line.Bottom = line.Motion.Scalar();
            Talker talker = speaker != null ? Find(speaker) : FindKey(key);
            if (talker == null) {
                if (talkers.Count >= MaxTalkers) { int oldest = Oldest(); Talker drop = talkers[oldest]; drop.Motion?.Dispose(); for (int k = 0; k < drop.Count; k++) { drop.Lines[k]?.Motion?.Dispose(); } talkers.RemoveAt(oldest); }
                talker = new Talker { Who = speaker, Key = key };
                talker.Motion = MotionClient.Runtime.Scope();
                talker.Shown = talker.Motion.Scalar();
                talkers.Add(talker);
            }
            if (talker.Count == PerTalker) {
                talker.Lines[0]?.Motion?.Dispose();
                Array.Copy(talker.Lines, 1, talker.Lines, 0, PerTalker - 1);
                talker.Count--;
            }
            talker.Lines[talker.Count++] = line;
            talker.Gone = false;
            return talker;
        }

        private static Talker Find(Character who) {
            for (int i = 0; i < talkers.Count; i++) {
                if (talkers[i].Key == null && ReferenceEquals(talkers[i].Who, who)) { return talkers[i]; }
            }
            return null;
        }

        private static int Oldest() {
            int best = 0;
            double start = double.MaxValue;
            for (int i = 0; i < talkers.Count; i++) {
                Talker t = talkers[i];
                double s = t.Count > 0 ? t.Lines[t.Count - 1].Received : double.MinValue;
                if (s < start) {
                    start = s;
                    best = i;
                }
            }
            return best;
        }

        public static void DrawSpeechBubblesPostfix(SpriteBatch spriteBatch, Camera cam) {
            if (!GUI.DisableHUD) { TypeGlyphRenderer.Ready(spriteBatch.GraphicsDevice); }
            if (talkers.Count == 0 || GUI.DisableHUD) { lastPaint = 0.0; return; }
            ScalableFont font = GUIStyle.SmallFont.Value;
            if (cam == null || font == null) { return; }
            float scale = GUI.Scale;
            GameFace face = Faces.Of(font, font.LineHeight);
            double now = HudClock.Now;
            float dt = lastPaint > 0.0 ? (float)Math.Max(0.0, now - lastPaint) : 0.0f;
            lastPaint = now;
            canvas.BeginTarget(spriteBatch, smooth: true, typewriter: true);
            canvas.Scale = scale;
            try {
                for (int i = talkers.Count - 1; i >= 0; i--) {
                    Talker t = talkers[i];
                    if (!Advance(t, now, dt)) {
                        t.Motion?.Dispose();
                        for (int k = 0; k < t.Count; k++) { t.Lines[k]?.Motion?.Dispose(); }
                        talkers.RemoveAt(i);
                        continue;
                    }
                    Draw(t, cam, face, now, dt, scale);
                }
            } finally {
                canvas.End();
            }
        }

        private static bool Advance(Talker t, double now, float dt) {
            int keep = 0;
            for (int k = 0; k < t.Count; k++) {
                Line line = t.Lines[k];
                bool alive = line.Presentation.Started ? line.Presentation.Age(now) < Life(line.Text) : now - line.Received < TypeMotion.PendingSeconds;
                if (alive) { t.Lines[k] = null; t.Lines[keep++] = line; }
                else { line.Motion?.Dispose(); t.Lines[k] = null; }
            }
            t.Count = keep;
            Character who = t.Who;
            if (t.Key != null) {
                t.Shown.To(t.Seen && !t.Gone ? 1.0f : 0.0f, MotionTransition.Spring(ShowOmega, 1.0f));
                if (t.Count == 0) { return false; }
                return !t.Gone || now - t.GoneAt < FadeSeconds;
            }
            bool exists = who != null && !who.Removed && !who.IsDead;
            if (!exists && !t.Gone) {
                t.Gone = true;
                t.GoneAt = now;
            }
            if (exists) {
                Limb head = who.AnimController?.GetLimb(LimbType.Head);
                t.Anchor = head?.body != null ? head.body.DrawPosition : who.DrawPosition + new Vector2(0.0f, 100.0f);
            }
            bool visible = exists && who.Enabled && who.InvisibleTimer <= 0.0f && (Character.Controlled == null || ReferenceEquals(who, Character.Controlled) || who.hudInfoVisible);
            t.Shown.To(visible ? 1.0f : 0.0f, MotionTransition.Spring(ShowOmega, 1.0f));
            if (t.Count == 0) { return false; }
            return !t.Gone || now - t.GoneAt < FadeSeconds;
        }

        private static void Draw(Talker t, Camera cam, GameFace face, double now, float dt, float scale) {
            float shown = Scalar.Clamp01(t.Shown.Value);
            if (t.Gone) { shown *= 1.0f - Scalar.Clamp01((float)(now - t.GoneAt) / FadeSeconds); }
            if (shown < 0.01f) { return; }
            Vector2 anchor = cam.WorldToScreen(t.Anchor + new Vector2(0.0f, HeadWorld));
            if (anchor.X < -WidthPx * scale || anchor.X > GameMain.GraphicsWidth + WidthPx * scale || anchor.Y < 0.0f || anchor.Y > GameMain.GraphicsHeight + MaxLines * face.LineHeight) { return; }
            float pad = MathF.Round(PadPx * scale);
            float gap = MathF.Round(GapPx * scale);
            float width = MathF.Round(WidthPx * scale);
            float bottom = 0.0f;
            for (int k = t.Count - 1; k >= 0; k--) {
                Line line = t.Lines[k];
                if (!ReferenceEquals(line.Face, face) || line.MaxWidth != width) {
                    line.Layout.Wrap(line.Text, face, width, face.LineHeight, MaxLines);
                    line.Width = line.Layout.Width;
                    line.Face = face;
                    line.MaxWidth = width;
                }
                line.Presentation.Present(now);
                float age = line.Presentation.Age(now);
                float textAge = age - TypeMotion.RevealLead;
                int revealed = Math.Min(line.Layout.Count, line.Text.Revealed(textAge));
                float h = line.Layout.HeightAt(line.Text, textAge) + pad * 2.0f;
                float leave = TypeMotion.Ease((Life(line.Text) - age) / FadeSeconds);
                float enter = TypeMotion.Ease(age / TypeMotion.EnterSeconds);
                float a = shown * leave * enter;
                if (!line.Placed) { line.Bottom.Snap(bottom); line.Placed = true; }
                line.Bottom.To(bottom, MotionTransition.Spring(StackOmega, 1.0f));
                var box = new Box(anchor.X - line.Width * 0.5f - pad, anchor.Y - line.Bottom.Value - h + (1.0f - enter) * pad,
                    line.Width + pad * 2.0f, h);
                canvas.Fill(box, new Rgba(0, 0, 0, 150).Fade(a));
                Ui.Paint.Frame(canvas, box, 1.0f, Retro.Edge.Fade(0.6f * a));
                TypeDraw.Draw(canvas, line.Text, line.Layout, face, box.X + pad, box.Y + pad, textAge, line.Ink, new Rgba(0, 0, 0, 220), a, MathF.Max(1.0f, MathF.Round(scale)));
                TypeSound.Present(ref line.Clicks, line.Text, line.Layout.Count, textAge, a, now);
                if (line.Layout.Truncated && revealed >= line.Layout.Count) {
                    Ui.Paint.Text(canvas, face, "…", box.Right - pad - face.Advance('…'), box.Bottom - pad - face.LineHeight, line.Ink.Fade(a));
                }
                bottom += (h + gap) * leave;
            }
        }

        private static float Life(TypeText text) => TypeMotion.RevealLead + text.Duration + TypeMotion.ReadSeconds(text) + FadeSeconds;
    }
}
