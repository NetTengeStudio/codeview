using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;
using ScpItdMod.Motion;
using ScpItdMod.Rendering;

namespace ScpItdMod.Ui {
    // A card that is baked once into an off-screen surface and then drawn through the exposure blur. The bake blend turns
    // straight alpha premultiplied on the transparent target, which is what the exposure shader expects; the fallback
    // draws the surface without blur when the exposure pass is unavailable.
    public static class MotionCard {
        public static readonly BlendState Bake = new BlendState {
            ColorSourceBlend = Blend.SourceAlpha, ColorDestinationBlend = Blend.InverseSourceAlpha,
            AlphaSourceBlend = Blend.One, AlphaDestinationBlend = Blend.InverseSourceAlpha };
        private static readonly BlendState over = new BlendState {
            ColorSourceBlend = Blend.One, ColorDestinationBlend = Blend.InverseSourceAlpha,
            AlphaSourceBlend = Blend.One, AlphaDestinationBlend = Blend.InverseSourceAlpha };

        // Surfaces grow in 64 px steps and never shrink, so a change of size does not reallocate GPU memory.
        public static bool Fit(GraphicsDevice device, ref RenderSurface surface, Point size, SurfaceBudget budget, out bool allocated) {
            allocated = false;
            if (surface != null && surface.Target.Width >= size.X && surface.Target.Height >= size.Y) { return true; }
            Point capacity = new Point((size.X + 63) / 64 * 64, (size.Y + 63) / 64 * 64);
            try {
                surface?.Dispose();
                surface = null;
                surface = new RenderSurface(device, capacity.X, capacity.Y, budget);
                allocated = true;
                return true;
            } catch (InvalidOperationException) { return false; }
        }

        public static void Draw(SpriteBatch batch, RenderSurface surface, Point size, in VisualPose pose, MotionShutter shutter) {
            Color tint = Color.White * pose.Opacity;
            var region = new Rectangle(0, 0, size.X, size.Y);
            var path = new VisualMotionPath(pose);
            if (MotionBlur.Draw(batch, surface.Target, region, tint, path, new Vec2(.5f, .5f), shutter)) { return; }
            Box b = pose.Bounds;
            var bounds = new Rectangle((int)MathF.Round(b.X), (int)MathF.Round(b.Y), (int)MathF.Round(b.W), (int)MathF.Round(b.H));
            using (new SpriteBatchPause(batch)) {
                batch.Begin(SpriteSortMode.Deferred, over, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone);
                batch.Draw(surface.Target, bounds, region, tint);
                batch.End();
            }
        }
    }
}
