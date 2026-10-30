using System;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;
using ScpItdMod.Motion;

namespace ScpItdMod.Paper {
    // The hand-held stamp. Its three spring values (sway, lift, size) are Motion
    // handles now instead of private spring integration, so the same engine that
    // animates every other SCPITD surface drives the stamp, including interrupted
    // presses (the spring keeps its velocity when a new target is set).
    public static class StampHand {
        private const string ThumpAsset = "Content/Items/Paper/automatic_stamp.ogg";
        private const string BodyAsset = "Content/Items/Paper/thick_stamp_sub.ogg";
        private const float FootWidth = 0.95f;
        private const float FootBottom = 0.985f;
        private const float Pivot = 0.12f;
        private const float WorldHeight = 150.0f;
        private const float Hover = 0.22f;
        private const float MaxSway = 0.3f;

        private static readonly MotionTransition LiftRest = MotionTransition.Spring(18, 0.55f);
        private static readonly MotionTransition LiftDown = MotionTransition.Spring(60, 1);
        private static readonly MotionTransition SwayIdle = MotionTransition.Spring(9, 0.32f);
        private static readonly MotionTransition SwayPress = MotionTransition.Spring(40, 1);
        private static readonly MotionTransition Grow = MotionTransition.Spring(22, 0.8f);

        private enum Phase : byte { Idle, Down, Hold, Up }

        private static MotionScope motion;
        private static MotionHandle<float> sway;
        private static MotionHandle<float> lift;
        private static MotionHandle<float> size;
        private static Phase phase;
        private static float hold;
        private static bool shown;
        private static Func<Vector2, float, bool> contact;

        public static bool Shown => shown;

        public static bool Busy => phase != Phase.Idle;

        public static float Turn => motion?.Alive == true ? sway.Value : 0.0f;

        public static void Reset() {
            motion?.Dispose();
            motion = null;
            phase = Phase.Idle;
            shown = false;
            contact = null;
        }

        private static void EnsureMotion() {
            if (motion?.Alive == true) { return; }
            motion?.Dispose();
            motion = MotionClient.Runtime.Scope();
            sway = motion.Scalar();
            lift = motion.Scalar(1.0f);
            size = motion.Scalar();
        }

        public static Texture2D Texture(Item stamp, out Rectangle source) {
            Sprite sprite = stamp?.Prefab.Sprite;
            source = sprite?.SourceRect ?? Rectangle.Empty;
            return sprite?.Texture;
        }

        public static bool Press(Func<Vector2, float, bool> onContact) {
            if (phase != Phase.Idle) { return false; }
            contact = onContact;
            phase = Phase.Down;
            SoundStore.File(ThumpAsset)?.Play(0.7f);
            return true;
        }

        public static void Thump(Vector2 worldPosition) {
            SoundStore.File(ThumpAsset)?.Play(0.7f, 800.0f, worldPosition);
            SoundStore.File(BodyAsset)?.Play(0.8f, 800.0f, worldPosition);
        }

        public static bool DrawCursor(SpriteBatch spriteBatch) {
            if (GUI.MouseOn != null) { Reset(); return false; }
            Character me = Character.Controlled;
            Item stamp = PlayView.Active(me) && !me.IsIncapacitated && me.Stun <= 0 && !PlayView.Busy ? PaperModule.HeldStamp(me) : null;
            Texture2D texture = Texture(stamp, out Rectangle source);
            float dt = FrameClock.Step;
            EnsureMotion();
            Vector2 mouse = PlayerInput.LatestMousePosition;
            bool overSheet = SheetView.Covers(mouse, out float sheetScale);
            float targetSize = texture == null ? 0.0f
                : overSheet ? Sheet.MarkWidth * sheetScale * 1.1f / (source.Width * FootWidth)
                : WorldHeight * GUI.Scale / source.Height;
            if (!shown && texture != null) { size.Snap(targetSize * 0.6f); }
            shown = texture != null && !Inv.InvUi.Active && !Rp.ActPanel.Shown;
            if (!shown) {
                phase = Phase.Idle;
                return false;
            }
            size.To(targetSize, Grow);
            float drag = Scalar.Clamp01(1.0f - MathF.Abs(lift.Value - 1.0f));
            float swayTarget = phase == Phase.Idle ? Math.Clamp(-PlayerInput.MouseSpeed.X * 0.012f, -MaxSway, MaxSway) * drag : 0.0f;
            sway.To(swayTarget, phase == Phase.Idle ? SwayIdle : SwayPress);
            Advance(dt, mouse);

            float s = size.Value;
            float w = source.Width * s;
            float h = source.Height * s;
            float raise = lift.Value * Hover * h;
            float squash = phase == Phase.Hold ? 0.95f : 1.0f;
            var foot = new Vector2(mouse.X, mouse.Y + Sheet.MarkHeight * sheetScale * 0.5f * (overSheet ? 1.0f : 0.0f));

            Texture2D soft = SoftDot();
            if (soft != null) {
                float spread = 1.0f + lift.Value * 0.35f;
                float alpha = (overSheet ? 0.42f : 0.25f) * (1.0f - lift.Value * 0.55f);
                float depth = (overSheet ? Sheet.MarkHeight * sheetScale : w * 0.24f) * spread;
                float width = w * FootWidth * spread;
                float y = overSheet ? mouse.Y : foot.Y;
                var shadow = new Rectangle((int)(mouse.X - width * 0.5f), (int)(y - depth * 0.5f), (int)width, (int)depth);
                spriteBatch.Draw(soft, shadow, Script.Fade(Color.Black, alpha));
            }
            var pivot = new Vector2(foot.X, foot.Y - h * FootBottom - raise + h * Pivot + h * (1.0f - squash) * (FootBottom - Pivot));
            spriteBatch.Draw(texture, pivot, source, Color.White, sway.Value,
                new Vector2(source.Width * 0.5f, source.Height * Pivot), new Vector2(s, s * squash), SpriteEffects.None, 0.0f);
            return true;
        }

        private static void Advance(float dt, Vector2 mouse) {
            switch (phase) {
                case Phase.Idle:
                    lift.To(1.0f, LiftRest);
                    break;
                case Phase.Down:
                    lift.To(-0.02f, LiftDown);
                    if (lift.Value <= 0.04f) {
                        lift.Snap(0.0f);
                        contact?.Invoke(mouse, sway.Value);
                        contact = null;
                        SoundStore.File(BodyAsset)?.Play(0.8f);
                        phase = Phase.Hold;
                        hold = 0.07f;
                    }
                    break;
                case Phase.Hold:
                    hold -= dt;
                    if (hold <= 0.0f) {
                        phase = Phase.Up;
                        lift.Impulse(9.0f);
                        lift.To(1.0f, LiftRest);
                    }
                    break;
                case Phase.Up:
                    lift.To(1.0f, LiftRest);
                    if (lift.Value > 0.9f) { phase = Phase.Idle; }
                    break;
            }
        }

        private static Texture2D SoftDot() => TextureStore.Generated("paper.soft", 64, BuildSoft);

        private static Texture2D BuildSoft(int side) {
            GraphicsDevice device = GameMain.Instance?.GraphicsDevice;
            if (device == null) { return null; }
            var pixels = new Color[side * side];
            float half = side * 0.5f;
            for (int y = 0; y < side; y++) {
                for (int x = 0; x < side; x++) {
                    float dx = (x + 0.5f - half) / half;
                    float dy = (y + 0.5f - half) / half;
                    float d = MathF.Sqrt(dx * dx + dy * dy);
                    float a = Scalar.Smooth(Scalar.Clamp01(1.0f - d));
                    pixels[y * side + x] = new Color((byte)255, (byte)255, (byte)255, Scalar.ToByte(a));
                }
            }
            var texture = new Texture2D(device, side, side);
            texture.SetData(pixels);
            return texture;
        }
    }
}
