using Barotrauma;
using Barotrauma.Items.Components;
using ScpItdMod.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ScpItdMod.Hand {
    public static class HandHud {
        private const string UseAsset = "Content/HUD/handsymbol.png";
        private const string TakeAsset = "Content/HUD/handsymbol2.png";
        private const float IconSize = 64.0f;

        private static Focus focus;
        private static HudFade fade;
        private static Item target;
        private static bool takes;

        public static void Register() => HudFrame.Register(HudFrame.Meters, Paint);

        public static void Reset() {
            focus.Clear();
            fade.Reset();
            target = null;
        }

        public static void Paint(SpriteBatch spriteBatch) {
            Character character = Character.Controlled;
            bool active = focus.Follow(character, out bool renewed);
            if (renewed) { Reset(); }
            if (!active) { return; }

            Item focused = PlayView.Busy ? null : character.FocusedItem;
            if (focused != null && !focused.Removed) {
                target = focused;
                takes = focused.GetComponent<Pickable>() != null;
            }
            fade.Advance(focused != null && !focused.Removed, FrameClock.Step);
            if (!fade.Painted || target == null || target.Removed) { return; }

            Texture2D icon = TextureStore.File(takes ? TakeAsset : UseAsset);
            if (icon == null) { return; }

            Vector2 centre = GameMain.GameScreen.Cam.WorldToScreen(target.DrawPosition);
            int side = Scalar.Round(IconSize * GUI.Scale);
            Rectangle area = new Rectangle(Scalar.Round(centre.X) - side / 2, Scalar.Round(centre.Y) - side / 2, side, side);

            HudPaint.BeginScreen(spriteBatch);
            HudPaint.Blit(spriteBatch, icon, area, HudPaint.Alpha(HudPaint.Ink, fade.Opacity));
            HudPaint.End(spriteBatch);
        }
    }
}
