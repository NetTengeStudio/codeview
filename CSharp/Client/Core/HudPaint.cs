using System;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ScpItdMod.Core {
    public static class HudPaint {
        public static readonly Color Ink = new Color(255, 255, 255, 255);
        public static readonly Color Plate = new Color(0, 0, 0, 255);
        public static readonly Color Alarm = new Color(243, 109, 109, 255);
        public static readonly Color Rail = new Color(41, 41, 41, 255);

        private static Rectangle heldScissor;
        private static bool clipping;
        private static int depth;

        public static Color Alpha(Color color, float alpha) =>
            new Color(color.R, color.G, color.B, Scalar.ToByte(alpha));

        public static Color Premultiplied(Color color) => Shade(color, color.A / 255.0f);

        public static Color Shade(Color color, float shade) =>
            new Color(Scalar.ToByte(color.R / 255.0f * shade),
                Scalar.ToByte(color.G / 255.0f * shade),
                Scalar.ToByte(color.B / 255.0f * shade), color.A);

        public static float Hairline(float scale) {
            int thickness = Scalar.Round(scale);
            return thickness < 1 ? 1.0f : thickness;
        }

        public static Rectangle Slice(Rectangle area, float x, float y, float width, float height) =>
            new Rectangle(
                area.X + Scalar.Round(area.Width * x),
                area.Y + Scalar.Round(area.Height * y),
                Scalar.Round(area.Width * width),
                Scalar.Round(area.Height * height));

        public static Rectangle Contain(Rectangle area, int width, int height) {
            if (width <= 0 || height <= 0) { return area; }
            float scale = Math.Min((float)area.Width / width, (float)area.Height / height);
            int fittedWidth = Scalar.Round(width * scale);
            int fittedHeight = Scalar.Round(height * scale);
            return new Rectangle(
                area.X + (area.Width - fittedWidth) / 2,
                area.Y + (area.Height - fittedHeight) / 2,
                fittedWidth,
                fittedHeight);
        }

        public static Rectangle Project(Rectangle design, Vector2 origin, float scale) =>
            new Rectangle(
                Scalar.Round(origin.X + design.X * scale),
                Scalar.Round(origin.Y + design.Y * scale),
                Scalar.Round(design.Width * scale),
                Scalar.Round(design.Height * scale));

        public static void BeginScreen(SpriteBatch spriteBatch) => Begin(spriteBatch, SamplerState.LinearClamp);

        public static void BeginPixels(SpriteBatch spriteBatch) => Begin(spriteBatch, SamplerState.PointClamp);

        public static void BeginAdditive(SpriteBatch spriteBatch) =>
            Begin(spriteBatch, SamplerState.LinearClamp, BlendState.Additive);

        public static void Clip(SpriteBatch spriteBatch, Rectangle area) {
            GraphicsDevice device = spriteBatch.GraphicsDevice;
            if (!clipping) {
                clipping = true;
                Hold(device);
            }
            spriteBatch.End();
            device.ScissorRectangle = Rectangle.Intersect(area,
                new Rectangle(0, 0, GameMain.GraphicsWidth, GameMain.GraphicsHeight));
            spriteBatch.Begin(SpriteSortMode.Deferred, null, GUI.SamplerState, null,
                GameMain.ScissorTestEnable);
        }

        public static void Unclip(SpriteBatch spriteBatch) {
            if (!clipping) { return; }
            clipping = false;
            End(spriteBatch);
        }

        public static void End(SpriteBatch spriteBatch) {
            if (depth == 0) { return; }
            depth--;
            spriteBatch.End();
            if (depth == 0) { spriteBatch.GraphicsDevice.ScissorRectangle = heldScissor; }
            spriteBatch.Begin(SpriteSortMode.Deferred, null, GUI.SamplerState, null, GameMain.ScissorTestEnable);
        }

        public static void Rescue(SpriteBatch spriteBatch) {
            clipping = false;
            while (depth > 0) { End(spriteBatch); }
        }

        public static void Fill(SpriteBatch spriteBatch, Rectangle area, Color color) =>
            GUI.DrawRectangle(spriteBatch, area, color, isFilled: true);

        public static void Outline(SpriteBatch spriteBatch, Rectangle area, Color color, float thickness) =>
            GUI.DrawRectangle(spriteBatch, area, color, isFilled: false, thickness: thickness);

        public static void Blit(SpriteBatch spriteBatch, Texture2D texture, Rectangle area, Color color) =>
            spriteBatch.Draw(texture, area, color);

        private static void Hold(GraphicsDevice device) {
            if (depth == 0) { heldScissor = device.ScissorRectangle; }
            depth++;
        }

        private static void Begin(SpriteBatch spriteBatch, SamplerState sampler, BlendState blend = null) {
            GraphicsDevice device = spriteBatch.GraphicsDevice;
            Hold(device);
            spriteBatch.End();
            device.ScissorRectangle = new Rectangle(0, 0, GameMain.GraphicsWidth, GameMain.GraphicsHeight);
            spriteBatch.Begin(SpriteSortMode.Deferred, blend ?? BlendState.NonPremultiplied, sampler, null,
                GameMain.ScissorTestEnable);
        }
    }
}
