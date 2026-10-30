using System;
using Barotrauma;
using ScpItdMod.Core;
using ScpItdMod.Motion;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ScpItdMod.Stamina {
    public static class StaminaHud {
        private const string SprintIconAsset = "Content/HUD/sprinticon.png";
        private const string SneakIconAsset = "Content/HUD/sneakicon.png";
        private const string SegmentAsset = "Content/HUD/staminameter.jpg";
        private const string VignetteAsset = "stamina.vignette";
        private const int StackRow = 0;

        private static readonly Vector2 GroupSize = new Vector2(255.0f, 32.0f);
        private static readonly Rectangle IconBorder = new Rectangle(0, 0, 32, 32);
        private static readonly Rectangle IconArea = new Rectangle(1, 1, 30, 30);
        private static readonly Rectangle BarBorder = new Rectangle(51, 1, 204, 20);
        private static readonly Rectangle SegmentArea = new Rectangle(54, 4, 8, 14);
        private static readonly HudMeter Meter = new HudMeter(IconBorder, IconArea, BarBorder, SegmentArea);
        private static readonly Func<GraphicsDevice, Texture2D> VignetteBuilder = BuildVignette;

        private static HudSlot slot;
        private static Focus focus;
        private static MotionScope motion;
        private static MotionHandle<float> fill;
        private static MotionHandle<float> vignette;
        private static HudFade meterFade;
        private static float holdTimer;
        private static uint syncedFrame = uint.MaxValue;
        private static bool live;

        public static void Register() {
            slot = HudDock.Register(HudCorner.BottomRight, GroupSize, StackRow, Occupying);
            HudFrame.Register(HudFrame.Vignette, PaintVignette);
            HudFrame.Register(HudFrame.Meters, PaintMeter);
        }

        public static void Reset() {
            focus.Clear();
            EnsureMotion();
            Renew();
        }

        private static bool Occupying() => meterFade.Painted;

        private static void Renew() {
            EnsureMotion();
            fill.Snap(1.0f);
            vignette.Snap(0.0f);
            meterFade.Reset();
            holdTimer = 0.0f;
            live = false;
        }

        private static void EnsureMotion() {
            if (motion?.Alive == true) { return; }
            motion?.Dispose();
            motion = MotionClient.Runtime.Scope();
            fill = motion.Scalar(1.0f);
            vignette = motion.Scalar();
        }

        private static void Sync() {
            if (syncedFrame == FrameClock.Frame) { return; }
            syncedFrame = FrameClock.Frame;
            live = focus.Follow(Character.Controlled, out bool renewed);
            if (renewed) { Renew(); }
            if (!live) { return; }
            Advance(FrameClock.Step);
        }

        public static void PaintVignette(SpriteBatch spriteBatch) {
            Sync();
            if (!live || vignette.Value <= HudFade.Cutoff) { return; }
            HudPaint.BeginScreen(spriteBatch);
            DrawVignette(spriteBatch);
            HudPaint.End(spriteBatch);
        }

        public static void PaintMeter(SpriteBatch spriteBatch) {
            Sync();
            if (!live || !meterFade.Painted) { return; }
            HudDock.Layout();
            HudPaint.BeginPixels(spriteBatch);
            DrawMeter(spriteBatch, Character.Controlled);
            HudPaint.End(spriteBatch);
        }

        private static void Advance(float dt) {
            EnsureMotion();
            float stamina = StaminaClient.Display;
            fill.To(stamina, MotionTransition.Spring(StaminaConfig.FillRate, 1.0f));

            bool inUse = stamina < StaminaConfig.FullEpsilon || StaminaApi.Exerting || StaminaApi.Locked;
            if (inUse) { holdTimer = StaminaConfig.FadeHold; }
            else if (holdTimer > 0.0f) { holdTimer -= dt; }

            bool hudBusy = PlayView.Busy;

            meterFade.Advance(!hudBusy && (inUse || holdTimer > 0.0f), dt);
            vignette.To(hudBusy ? 0.0f : StaminaApi.Exhaustion, MotionTransition.Spring(HudFade.OutRate, 1.0f));
        }

        private static void DrawVignette(SpriteBatch spriteBatch) {
            Texture2D texture = TextureStore.Generated(VignetteAsset, spriteBatch.GraphicsDevice, VignetteBuilder);
            if (texture == null) { return; }
            HudPaint.Blit(spriteBatch, texture, new Rectangle(0, 0, GameMain.GraphicsWidth, GameMain.GraphicsHeight),
                HudPaint.Alpha(Color.White, StaminaConfig.VignetteAlpha * vignette.Value));
        }

        private static void DrawMeter(SpriteBatch spriteBatch, Character character) {
            if (character == null) { return; }
            Texture2D icon = TextureStore.File(
                StaminaSim.Crouching(character) ? SneakIconAsset : SprintIconAsset);
            Meter.Draw(spriteBatch, slot, icon, TextureStore.File(SegmentAsset), fill.Value, meterFade.Opacity);
        }

        private static Texture2D BuildVignette(GraphicsDevice device) {
            if (device == null) { return null; }
            int size = StaminaConfig.VignetteResolution;
            var pixels = new Color[size * size];
            StaminaVignette.Bake(pixels, size);
            var texture = new Texture2D(device, size, size, false, SurfaceFormat.Color);
            texture.SetData(pixels);
            return texture;
        }
    }
}
