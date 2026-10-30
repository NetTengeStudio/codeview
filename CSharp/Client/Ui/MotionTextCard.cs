using System;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;
using ScpItdMod.Motion;
using ScpItdMod.Rendering;

namespace ScpItdMod.Ui {
    public enum CardPlacement : byte {
        ScreenTopRight,
        AboveRect
    }

    public sealed class MotionTextCard {
        private const float Pad = 10, LineGap = 2, Margin = 16, Gap = 6, Shade = .82f, Lift = 12, Shrink = .94f;
        private static readonly string[] none = Array.Empty<string>();

        private readonly string feature;
        private readonly SurfaceBudget budget;
        private readonly MotionShutter shutter;
        private readonly Baker baker;
        private string[] lines = none;
        private Point size, bakedSize;
        private bool dirty, exiting;
        private RenderSurface surface;
        private MotionVisual visual;
        private double until = double.NegativeInfinity;

        public MotionTextCard(string feature, SurfaceBudget budget, MotionShutter shutter) {
            this.feature = feature;
            this.budget = budget;
            this.shutter = shutter;
            baker = new Baker(this);
        }

        public long Bakes { get; private set; }

        public long Draws { get; private set; }

        public bool Visible => visual != null && visual.Alive && !exiting;

        private sealed class Baker : IOffscreenJob {
            private readonly MotionTextCard owner;

            public Baker(MotionTextCard owner) => this.owner = owner;

            public bool Queued { get; set; }

            public bool Run(SpriteBatch batch) {
                if (Contained.IsDisabled(owner.feature)) { return false; }
                try { return owner.Bake(batch); }
                catch (Exception error) {
                    Contained.Fail(owner.feature, error);
                    return false;
                }
            }
        }

        public void Show(string[] text, CardPlacement placement, Rectangle reference) {
            if (text == null || text.Length == 0) {
                Hide();
                return;
            }
            if (!SameLines(text)) {
                lines = (string[])text.Clone();
                size = Measure(lines);
                dirty = true;
            }
            if (dirty) { OffscreenPhase.Enqueue(baker); }
            Box target = Target(placement, reference);
            bool reduced = MotionClient.Runtime.ReducedMotion;
            if (visual == null || !visual.Alive) {
                visual?.Dispose();
                visual = new MotionVisual(MotionClient.Runtime, target, hidden: true);
                exiting = false;
                if (reduced) {
                    visual.Opacity.Snap(1); visual.Scale.Snap(1); visual.Lift.Snap(0);
                } else {
                    visual.Opacity.Snap(0); visual.Scale.Snap(Shrink); visual.Lift.Snap(Lift);
                    visual.Opacity.To(1, MotionPresets.Fade); visual.Scale.To(1, MotionPresets.Window); visual.Lift.To(0, MotionPresets.Window);
                }
                return;
            }
            if (exiting) {
                exiting = false;
                if (reduced) {
                    visual.Opacity.Snap(1); visual.Scale.Snap(1); visual.Lift.Snap(0);
                } else {
                    visual.Opacity.To(1, MotionPresets.Fade); visual.Scale.To(1, MotionPresets.Window); visual.Lift.To(0, MotionPresets.Window);
                }
            }
            visual.Place(target, MotionPresets.Hover, direct: reduced);
        }

        public void Hold(double seconds) => until = Timing.TotalTime + seconds;

        public void Hide() {
            if (visual == null || !visual.Alive || exiting) { return; }
            if (MotionClient.Runtime.ReducedMotion) {
                Retire();
                return;
            }
            exiting = true;
            visual.Opacity.To(0, MotionPresets.Fade);
            visual.Lift.To(Lift, MotionPresets.Window);
            visual.Scale.To(Shrink, MotionPresets.Window);
        }

        public void Update() {
            if (visual == null || !visual.Alive) { return; }
            if (!exiting && Timing.TotalTime > until) { Hide(); }
            if (exiting && visual.Opacity.Value < .01f) { Retire(); }
        }

        public void Draw(SpriteBatch batch) {
            if (visual == null || !visual.Alive || surface == null || bakedSize.X < 1 || bakedSize.Y < 1) { return; }
            VisualPose pose = visual.Pose;
            if (pose.Opacity <= 0.001f) { return; }
            Draws++;
            MotionCard.Draw(batch, surface, bakedSize, pose, shutter);
        }

        public string Describe() {
            if (visual == null || !visual.Alive) { return "hidden"; }
            VisualPose pose = visual.Pose;
            return $"opacity={pose.Opacity:F2} bounds=({pose.Bounds.X:F0},{pose.Bounds.Y:F0},{pose.Bounds.W:F0}x{pose.Bounds.H:F0}) exiting={exiting} baked={bakedSize.X}x{bakedSize.Y} draws={Draws} bakes={Bakes}";
        }

        public void Reset() {
            visual?.Dispose();
            visual = null;
            surface?.Dispose();
            surface = null;
            lines = none;
            size = default;
            bakedSize = default;
            dirty = false;
            exiting = false;
            until = double.NegativeInfinity;
            Contained.Revive(feature);
        }

        private bool Bake(SpriteBatch batch) {
            if (lines.Length == 0 || size.X < 1 || size.Y < 1) { return false; }
            GraphicsDevice device = batch.GraphicsDevice;
            if (!MotionCard.Fit(device, ref surface, size, budget, out _)) { return false; }
            GUIFont font = GUIStyle.SmallFont;
            float scale = GUI.Scale, lineHeight = font.MeasureString("Ag").Y + LineGap * scale;
            bakedSize = size;
            device.SetRenderTarget(surface.Target);
            device.ScissorRectangle = surface.Target.Bounds;
            device.Clear(Color.Transparent);
            batch.Begin(SpriteSortMode.Deferred, MotionCard.Bake, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone);
            GUI.DrawRectangle(batch, new Rectangle(0, 0, size.X, size.Y), Color.Black * Shade, true);
            float y = Pad * scale;
            for (int i = 0; i < lines.Length; i++) {
                GUI.DrawString(batch, new Vector2(Pad * scale, y), lines[i], Color.White, font: font);
                y += lineHeight;
            }
            batch.End();
            dirty = false;
            Bakes++;
            return true;
        }

        private bool SameLines(string[] text) {
            if (text.Length != lines.Length) { return false; }
            for (int i = 0; i < text.Length; i++) {
                if (!string.Equals(text[i], lines[i], StringComparison.Ordinal)) { return false; }
            }
            return true;
        }

        private Point Measure(string[] text) {
            GUIFont font = GUIStyle.SmallFont;
            float scale = GUI.Scale, width = 0;
            for (int i = 0; i < text.Length; i++) { width = MathF.Max(width, font.MeasureString(text[i]).X); }
            float lineHeight = font.MeasureString("Ag").Y + LineGap * scale;
            return new Point(
                (int)MathF.Ceiling(width + 2 * Pad * scale),
                (int)MathF.Ceiling(text.Length * lineHeight + 2 * Pad * scale));
        }

        private Box Target(CardPlacement placement, Rectangle reference) {
            float scale = GUI.Scale, width = size.X, height = size.Y;
            float screenWidth = GameMain.GraphicsWidth, screenHeight = GameMain.GraphicsHeight;
            if (placement == CardPlacement.ScreenTopRight) {
                return new Box(screenWidth - width - Margin * scale, Margin * scale, width, height);
            }
            float x = Math.Clamp((float)reference.X, 0f, MathF.Max(0f, screenWidth - width));
            float y = reference.Y - height - Gap * scale;
            if (y < 0) { y = reference.Bottom + Gap * scale; }
            return new Box(x, y, width, height);
        }

        private void Retire() {
            visual?.Dispose();
            visual = null;
            exiting = false;
        }
    }
}
