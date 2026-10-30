using Barotrauma;
using ScpItdMod.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ScpItdMod.Blink {
    public static class BlinkHud {
        private const string IconAsset = "Content/HUD/blinkicon.png";
        private const string SegmentAsset = "Content/HUD/blinkmeter.jpg";
        private const int StackRow = 1;

        private static readonly Vector2 GroupSize = new Vector2(257.0f, 36.0f);
        private static readonly Rectangle IrritationBorder = new Rectangle(0, 0, 36, 36);
        private static readonly Rectangle IconBorder = new Rectangle(2, 2, 32, 32);
        private static readonly Rectangle IconArea = new Rectangle(3, 3, 30, 30);
        private static readonly Rectangle BarBorder = new Rectangle(53, 3, 204, 20);
        private static readonly Rectangle SegmentArea = new Rectangle(56, 6, 8, 14);
        private static readonly HudMeter Meter = new HudMeter(IconBorder, IconArea, BarBorder, SegmentArea);
        private static readonly Color IrritationInk = new Color(200, 0, 0, 255);

        private static HudSlot slot;
        private static Focus focus;
        private static HudFade fade;

        public static void Register() {
            slot = HudDock.Register(HudCorner.BottomRight, GroupSize, StackRow, Occupying);
            HudFrame.Register(HudFrame.Meters, Paint);
        }

        public static void Reset() {
            focus.Clear();
            fade.Reset();
        }

        public static void Paint(SpriteBatch spriteBatch) {
            bool active = focus.Follow(Character.Controlled, out bool renewed);
            if (renewed) { fade.Reset(); }
            if (!active) { return; }

            fade.Advance(!PlayView.Busy && BlinkClient.Engaged, FrameClock.Step);
            if (!fade.Painted) { return; }

            HudDock.Layout();
            HudPaint.BeginPixels(spriteBatch);
            DrawMeter(spriteBatch);
            HudPaint.End(spriteBatch);
        }

        private static bool Occupying() => fade.Painted;

        private static void DrawMeter(SpriteBatch spriteBatch) {
            if (BlinkClient.Irritated) {
                HudPaint.Fill(spriteBatch, HudPaint.Project(IrritationBorder, slot.Position, slot.Scale),
                    HudPaint.Alpha(IrritationInk, fade.Opacity));
            }
            Meter.Draw(spriteBatch, slot, TextureStore.File(IconAsset), TextureStore.File(SegmentAsset),
                BlinkClient.Fill, fade.Opacity);
        }
    }
}
