using System;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ScpItdMod.Rendering {
    // Blurs the world as it is drawn right now, every frame the backdrop is visible.
    // The source is the game's own world target, GameScreen.renderTargetFinal. The game samples it for its post
    // pass right after the world is composed and draws the HUD later in the same frame, so its contents are valid
    // while the inventory draws. Nothing is read back from the GPU.
    // The blur runs in quarter resolution as three additive SpriteBatch passes: a 4-tap box downsample, then a
    // 5-tap horizontal and a 5-tap vertical box. Every pass sums weighted taps, so the result is a normalised box
    // blur, close to a Gaussian after the two passes. No new shader is needed.
    public sealed class LiveBackdrop : IDisposable {
        public const int Factor = 4;
        private const int BlurRadius = 2;
        private const float BlurWeight = 1f / (2 * BlurRadius + 1);

        private static readonly AccessTools.FieldRef<GameScreen, RenderTarget2D> WorldTarget =
            AccessTools.FieldRefAccess<GameScreen, RenderTarget2D>("renderTargetFinal");

        // Taps sum with One/One so each pass adds its weighted samples; colour and alpha both accumulate.
        private static readonly BlendState Accumulate = new BlendState {
            ColorSourceBlend = Blend.One, ColorDestinationBlend = Blend.One,
            AlphaSourceBlend = Blend.One, AlphaDestinationBlend = Blend.One
        };

        private RenderSurface first, second;
        private SpriteBatch batch;
        private bool disposed;

        public bool Ready { get; private set; }

        // Rebuilds the blurred copy from the world target of this frame. `hud` is the batch that is currently
        // drawing the HUD: it is paused for the duration of the render-target passes and restarted afterwards.
        public void Build(SpriteBatch hud) {
            if (disposed) { throw new ObjectDisposedException(nameof(LiveBackdrop)); }
            Ready = false;
            GameScreen screen = GameMain.GameScreen;
            RenderTarget2D world = screen == null ? null : WorldTarget(screen);
            if (world == null || world.IsDisposed || !SpriteBatchPause.Supported(hud)) { return; }
            GraphicsDevice device = world.GraphicsDevice;
            int width = Math.Max(1, world.Width / Factor), height = Math.Max(1, world.Height / Factor);
            EnsureTargets(device, width, height);
            SpriteBatches.Ensure(ref batch, device);
            float scale = width / (float)world.Width;
            float texel = scale;

            using var pause = new SpriteBatchPause(hud);
            using var state = GraphicsPass.Capture(device);

            device.SetRenderTarget(first.Target); device.Clear(Color.Transparent);
            batch.Begin(SpriteSortMode.Deferred, Accumulate, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone);
            Color quarter = Color.White * 0.25f;
            // Four taps one world texel away from the centre average a 4 x 4 block of the world.
            batch.Draw(world, new Vector2(-texel, -texel), null, quarter, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
            batch.Draw(world, new Vector2(texel, -texel), null, quarter, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
            batch.Draw(world, new Vector2(-texel, texel), null, quarter, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
            batch.Draw(world, new Vector2(texel, texel), null, quarter, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
            batch.End();

            device.SetRenderTarget(second.Target); device.Clear(Color.Transparent);
            batch.Begin(SpriteSortMode.Deferred, Accumulate, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone);
            for (int k = -BlurRadius; k <= BlurRadius; k++) {
                batch.Draw(first.Target, new Vector2(k, 0f), null, Color.White * BlurWeight, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0f);
            }
            batch.End();

            device.SetRenderTarget(first.Target); device.Clear(Color.Transparent);
            batch.Begin(SpriteSortMode.Deferred, Accumulate, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone);
            for (int k = -BlurRadius; k <= BlurRadius; k++) {
                batch.Draw(second.Target, new Vector2(0f, k), null, Color.White * BlurWeight, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0f);
            }
            batch.End();
            Ready = true;
        }

        // Draws the last blurred frame over the screen. Alpha is the reveal of the ring.
        public void Draw(SpriteBatch hud, float alpha) {
            if (!Ready || alpha <= 0.001f || disposed) { return; }
            hud.Draw(first.Target, new Rectangle(0, 0, GameMain.GraphicsWidth, GameMain.GraphicsHeight), Color.White * Math.Min(alpha, 1f));
        }

        public void Reset() => Ready = false;

        private void EnsureTargets(GraphicsDevice device, int width, int height) {
            if (first != null && first.Target.Width == width && first.Target.Height == height) { return; }
            first?.Dispose(); second?.Dispose();
            first = new RenderSurface(device, width, height, RenderSurface.Cameras);
            second = new RenderSurface(device, width, height, RenderSurface.Cameras);
        }

        public void Dispose() {
            if (disposed) { return; }
            disposed = true; Ready = false;
            first?.Dispose(); second?.Dispose(); batch?.Dispose();
            first = second = null; batch = null;
        }
    }
}
