using Barotrauma;
using ScpItdMod.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ScpItdMod.Blink {
    public static class BlinkOverlay {
        public static void Draw(SpriteBatch spriteBatch) {
            Character character = Character.Controlled;
            if (!PlayView.Active(character)) {
                if (BlinkClient.Tracking) { BlinkClient.Reset(); }
                return;
            }
            float darkness = BlinkClient.Darkness;
            if (darkness <= HudFade.Cutoff) { return; }

            HudPaint.BeginScreen(spriteBatch);
            HudPaint.Fill(spriteBatch, new Rectangle(0, 0, GameMain.GraphicsWidth, GameMain.GraphicsHeight),
                HudPaint.Alpha(Color.Black, darkness));
            HudPaint.End(spriteBatch);
        }
    }
}
