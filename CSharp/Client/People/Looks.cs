using System;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;
using ScpItdMod.Hud;
using ScpItdMod.Inv;
using ScpItdMod.Ui;

namespace ScpItdMod.People {
    public static class Looks {
        public const float HoverDelay = 0.6f;
        private const float WidthPx = 320.0f;
        private const float PadPx = 6.0f;
        private const float BelowWorld = 40.0f;
        private const float OpenOmega = 14.0f;
        private const float CloseOmega = 22.0f;
        private const int MaxLines = 10;

        private static Character hovered;
        private static Character shown;
        private static double hoverSince;
        private static Motion.MotionScope motion;
        private static Motion.MotionHandle<float> open;
        private static Motion.MotionHandle<float> height;
        private static Vector2 anchor;
        private static string source;
        private static string label = "";
        private static float labelFor = -1.0f;
        private static TypeText text;
        private static readonly TypeLayout layout = new TypeLayout();
        private static string layoutKey;
        private static float layoutWidth;
        private static readonly BatchCanvas canvas = new BatchCanvas();

        public static Character Shown => shown;

        public static void Attach(Harmony harmony) => HudFrame.Register(HudFrame.Meters - 3, Paint);

        public static void Reset() {
            hovered = null;
            shown = null;
            EnsureMotion(); open.Snap(0.0f); height.Snap(0.0f);
            text = null;
            source = null;
            layoutKey = null;
        }

        private static void Paint(SpriteBatch spriteBatch) {
            Camera cam = GameMain.GameScreen?.Cam;
            if (cam == null) { return; }
            float dt = FrameClock.Step;
            Track(cam, HudClock.Now, dt);
            if (shown == null || open.Value < 0.01f) { return; }
            ScalableFont font = GUIStyle.SmallFont.Value;
            if (font == null) { return; }
            GameFace face = Faces.Of(font, MathF.Ceiling(font.Size * 1.25f));
            float scale = GUI.Scale;
            canvas.Begin(spriteBatch, scale);
            try {
                Draw(cam, face, scale, dt);
            } finally {
                canvas.End();
            }
        }

        private static void Track(Camera cam, double now, float dt) {
            EnsureMotion();
            Character candidate = Blocked() ? null : Under(cam);
            if (!ReferenceEquals(candidate, hovered)) {
                hovered = candidate;
                hoverSince = now;
            }
            bool alive = shown != null && !shown.Removed && !shown.IsDead && shown.hudInfoVisible;
            if (shown != null && !alive) { hovered = ReferenceEquals(hovered, shown) ? null : hovered; }
            bool want = hovered != null && now - hoverSince >= HoverDelay && PeopleClient.Look(hovered.ID, out string look) && look.Length > 0;
            if (want && !ReferenceEquals(shown, hovered)) {
                if (open.Value < 0.05f) {
                    shown = hovered;
                    source = null;
                    open.Snap(0.0f);
                } else {
                    want = false;
                }
            }
            if (want && shown != null && !shown.Removed) {
                Limb torso = shown.AnimController?.GetLimb(LimbType.Torso);
                anchor = torso?.body != null ? torso.body.DrawPosition : shown.DrawPosition;
            }
            EnsureMotion();
            open.To(want ? 1.0f : 0.0f, Motion.MotionTransition.Spring(want ? OpenOmega : CloseOmega, 1.0f));
            if (!want && open.Value < 0.01f) { shown = null; }
        }

        private static void EnsureMotion() {
            if (motion?.Alive == true) { return; }
            motion?.Dispose();
            motion = Motion.MotionClient.Runtime.Scope();
            open = motion.Scalar();
            height = motion.Scalar();
        }

        private static bool Blocked() =>
            InvUi.Active || GUI.MouseOn != null || PlayView.Busy || Character.Controlled == null ;

        private static Character Under(Camera cam) {
            Vector2 cursor = cam.ScreenToWorld(PlayerInput.MousePosition);
            Character best = null;
            float bestDistance = float.MaxValue;
            foreach (ushort id in PeopleClient.LookIds) {
                if (Entity.FindEntityByID(id) is not Character character) { continue; }
                if (ReferenceEquals(character, Character.Controlled) || character.Removed || !character.Enabled || character.IsDead) { continue; }
                if (!character.IsHuman || !character.hudInfoVisible) { continue; }
                if (!Rp.CharacterPicking.Contains(character, cursor)) { continue; }
                float d = Vector2.DistanceSquared(cursor, character.WorldPosition);
                if (d < bestDistance) {
                    bestDistance = d;
                    best = character;
                }
            }
            return best;
        }

        private static void Draw(Camera cam, GameFace face, float scale, float dt) {
            if (shown == null) { return; }
            if (!PeopleClient.Look(shown.ID, out string look) || look.Length == 0) { look = source ?? ""; }
            if (!ReferenceEquals(look, source)) {
                source = look;
                text = TypeText.Parse(look, new TypePace { Speed = 1.0f, Instant = true }, markup: false);
                layoutKey = null;
            }
            float pad = MathF.Round(PadPx * scale);
            float width = MathF.Min(MathF.Round(WidthPx * scale), MathF.Max(1, GameMain.GraphicsWidth - pad * 2));
            if (!ReferenceEquals(layoutKey, face.Key) || layoutWidth != width) {
                layout.Wrap(text, face, width, face.LineHeight, MaxLines);
                layoutKey = face.Key;
                layoutWidth = width;
                EnsureMotion(); height.Snap(0.0f);
            }
            if (labelFor != Time() || label.Length == 0) {
                label = Names.Sighted(shown);
                labelFor = Time();
            }
            float e = Scalar.Clamp01(open.Value);
            float full = face.LineHeight + pad + layout.Height;
            EnsureMotion();
            height.To(full * e, Motion.MotionTransition.Spring(OpenOmega, 1.0f));
            float shownH = MathF.Max(0.0f, height.Value);
            Vector2 at = cam.WorldToScreen(anchor - new Vector2(0.0f, BelowWorld));
            float w = MathF.Min(GameMain.GraphicsWidth, MathF.Max(layout.Width, Ui.Paint.Measure(face, label)) + pad * 2.0f);
            float h = MathF.Min(GameMain.GraphicsHeight, MathF.Round(shownH) + pad * 2.0f);
            var box = new Box(Math.Clamp(MathF.Round(at.X - w * 0.5f), 0, GameMain.GraphicsWidth - w),
                Math.Clamp(MathF.Round(at.Y + (1.0f - e) * 8.0f * scale), 0, GameMain.GraphicsHeight - h), w, h);
            canvas.Fill(box, Retro.Plate.Fade(0.85f * e));
            Ui.Paint.Frame(canvas, box, 1.0f, Retro.Edge.Fade(e));
            canvas.PushClip(box);
            Ui.Paint.Clipped(canvas, face, label, box.Inset(pad).TakeTop(face.LineHeight), Retro.Bright.Fade(e), Align.Left);
            canvas.Fill(new Box(box.X + pad, box.Y + pad + face.LineHeight + pad * 0.5f - 1.0f, box.W - pad * 2.0f, 1.0f), Retro.Rule.Fade(e));
            TypeDraw.Draw(canvas, text, layout, face, box.X + pad, box.Y + pad * 1.5f + face.LineHeight, text.Duration, Retro.Ink, default, e, 0.0f);
            canvas.PopClip();
            if ((layout.Truncated || full + pad * 2 > h) && e > 0.99f) {
                Ui.Paint.Text(canvas, face, "…", box.Right - pad - face.Advance('…'), box.Bottom - pad - face.LineHeight, Retro.Ink);
            }
        }

        private static float Time() => (float)Math.Floor(HudClock.Now * 4.0);
    }
}
