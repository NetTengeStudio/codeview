using System;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;
using ScpItdMod.Motion;
using ScpItdMod.Rendering;

namespace ScpItdMod.Ui {
    public static class NativeTooltipMotion {
        private const string Feature = "native tooltip";
        private static readonly SurfaceBudget budget = new SurfaceBudget(8L << 20);
        private static readonly MotionShutter cardShutter = new MotionShutter(1d / 360, 4, MotionShutter.MaxSamples);
        private static readonly BakeJob bakeJob = new BakeJob();
        private static GUITextBlock card, bakedCard, failedCard;
        private static RichString cardText;
        private static Color? cardTextColor, cardBackground;
        private static float cardScale;
        private static RenderSurface surface;
        private static Point bakedSize;
        private static MotionVisual visual;
        private static double lastDrawn = double.NegativeInfinity;
        private static bool requested, exiting;

        public static long Bakes { get; private set; }
        public static long Allocations { get; private set; }
        public static long Draws { get; private set; }

        private sealed class BakeJob : IOffscreenJob {
            private const int Attempts = 120;
            public GUITextBlock Card;
            private GUITextBlock waiting;
            private int tries;
            public bool Queued { get; set; }
            public bool Run(SpriteBatch batch) {
                if (!ReferenceEquals(waiting, Card)) {
                    waiting = Card;
                    tries = 0;
                }
                try {
                    if (Bake(batch, Card)) { return true; }
                    if (++tries >= Attempts) { failedCard = Card; }
                    return false;
                }
                catch (Exception error) {
                    Contained.Fail(Feature, error);
                    failedCard = Card;
                    return false;
                }
            }
        }

        public static void Attach(Harmony harmony) {
            harmony.Patch(AccessTools.Method(typeof(GUIComponent), nameof(GUIComponent.DrawToolTip),
                new[] { typeof(SpriteBatch), typeof(RichString), typeof(Rectangle), typeof(Anchor), typeof(Pivot) }),
                prefix: ModuleHost.Bind(typeof(NativeTooltipMotion), nameof(AtTargetPrefix)));
            harmony.Patch(AccessTools.Method(typeof(GUIComponent), nameof(GUIComponent.DrawToolTip),
                new[] { typeof(SpriteBatch), typeof(RichString), typeof(Vector2), typeof(Color?), typeof(Color?) }),
                prefix: ModuleHost.Bind(typeof(NativeTooltipMotion), nameof(AtPositionPrefix)));
            harmony.Patch(AccessTools.Method(typeof(GameMain), "Draw", new[] { typeof(GameTime) }),
                prefix: ModuleHost.Bind(typeof(NativeTooltipMotion), nameof(BeginFrame)));
            harmony.Patch(AccessTools.Method(typeof(GUI), nameof(GUI.Draw), new[] { typeof(Camera), typeof(SpriteBatch) }),
                postfix: ModuleHost.Bind(typeof(NativeTooltipMotion), nameof(DrawExit)));
        }

        public static bool AtTargetPrefix(SpriteBatch spriteBatch, RichString toolTip, Rectangle targetElement, Anchor anchor, Pivot pivot) {
            if (Contained.IsDisabled(Feature)) { return TooltipRoute.RunVanilla; }
            try {
                if (ObjectiveManager.ContentRunning) { return TooltipRoute.SkipVanilla; }
                GUITextBlock text = Card(toolTip, null, null);
                Rectangle rect = PlaceAround(text, targetElement, anchor, pivot);
                return Present(spriteBatch, text, rect);
            }
            catch (Exception error) {
                return Fallback(error);
            }
        }

        public static bool AtPositionPrefix(SpriteBatch spriteBatch, RichString toolTip, Vector2 pos, Color? textColor, Color? backgroundColor) {
            if (Contained.IsDisabled(Feature)) { return TooltipRoute.RunVanilla; }
            try {
                if (ObjectiveManager.ContentRunning) { return TooltipRoute.SkipVanilla; }
                GUITextBlock text = Card(toolTip, textColor, backgroundColor);
                text.RectTransform.AbsoluteOffset = pos.ToPoint();
                text.SetTextPos();
                if (text.Rect.Right > GameMain.GraphicsWidth - 10) { text.RectTransform.AbsoluteOffset -= new Point(text.Rect.Width, 0); }
                if (text.Rect.Bottom > GameMain.GraphicsHeight - 10) { text.RectTransform.AbsoluteOffset -= new Point(0, text.Rect.Height); }
                return Present(spriteBatch, text, text.Rect);
            }
            catch (Exception error) {
                return Fallback(error);
            }
        }

        private static bool Fallback(Exception error) {
            Contained.Fail(Feature, error);
            Retire();
            return TooltipRoute.RunVanilla;
        }

        public static void BeginFrame() {
            if (Contained.IsDisabled(Feature)) { return; }
            try {
                if (TooltipTiming.PhaseOf(visual != null, exiting, lastDrawn, Timing.TotalTime) == TooltipPhase.ExitDue) { StartExit(); }
                requested = false;
            }
            catch (Exception error) { Contained.Fail(Feature, error); }
        }

        public static void DrawExit(SpriteBatch spriteBatch) {
            if (Contained.IsDisabled(Feature)) { return; }
            try {
                if (requested || surface == null) { return; }
                if (!TooltipTiming.DrawsWithoutRequest(TooltipTiming.PhaseOf(visual != null, exiting, lastDrawn, Timing.TotalTime))) { return; }
                if (!visual.Alive || exiting && visual.Opacity.Value < .01f) {
                    Retire();
                    return;
                }
                Draw(spriteBatch);
            }
            catch (Exception error) { Contained.Fail(Feature, error); }
        }

        private static bool Present(SpriteBatch spriteBatch, GUITextBlock text, Rectangle rect) {
            using var span = FrameProfiler.Section(ProfileSection.Tooltip);
            if (ReferenceEquals(failedCard, text)) {
                Retire();
                return TooltipRoute.RunVanilla;
            }
            if (!Ready(text)) {
                bakeJob.Card = text;
                OffscreenPhase.Enqueue(bakeJob);
            }
            if (rect.Width < 1 || rect.Height < 1 || !Ready(text)) {
                if (Showing && surface != null) {
                    requested = true;
                    lastDrawn = Timing.TotalTime;
                    Draw(spriteBatch);
                }
                return TooltipRoute.SkipVanilla;
            }
            Show(spriteBatch, rect);
            return TooltipRoute.SkipVanilla;
        }

        private static bool Showing => TooltipTiming.PhaseOf(visual != null, exiting, lastDrawn, Timing.TotalTime) == TooltipPhase.Held;

        private static Rectangle PlaceAround(GUITextBlock text, Rectangle target, Anchor anchor, Pivot pivot) {
            CalculateOffset();
            if (text.Rect.Right > GameMain.GraphicsWidth - 10) {
                anchor = RectTransform.MoveAnchorLeft(anchor);
                pivot = (Pivot)RectTransform.MoveAnchorRight((Anchor)pivot);
                CalculateOffset();
            }
            if (text.Rect.Bottom > GameMain.GraphicsHeight - 10) {
                anchor = RectTransform.MoveAnchorTop(anchor);
                pivot = (Pivot)RectTransform.MoveAnchorBottom((Anchor)pivot);
                CalculateOffset();
            }
            text.SetTextPos();
            return text.Rect;

            void CalculateOffset() {
                text.RectTransform.AbsoluteOffset = RectTransform.CalculateAnchorPoint(anchor, target) +
                    RectTransform.CalculatePivotOffset(pivot, text.RectTransform.NonScaledSize);
            }
        }

        private static GUITextBlock Card(RichString toolTip, Color? textColor, Color? backgroundColor) {
            if (card != null && cardText == toolTip && cardTextColor == textColor && cardBackground == backgroundColor && cardScale == GUI.Scale) { return card; }
            int width = (int)(400 * GUI.Scale);
            int height = (int)(18 * GUI.Scale);
            Point padding = new Point((int)(10 * GUI.Scale));
            card = new GUITextBlock(new RectTransform(new Point(width, height), null), toolTip, font: GUIStyle.SmallFont, wrap: true, style: "GUIToolTip");
            if (textColor != null) { card.TextColor = textColor.Value; }
            if (backgroundColor != null) { card.Color = backgroundColor.Value; }
            card.RectTransform.NonScaledSize = new Point(
                (int)(GUIStyle.SmallFont.MeasureString(card.WrappedText).X + padding.X + card.Padding.X + card.Padding.Z),
                (int)(GUIStyle.SmallFont.MeasureString(card.WrappedText).Y + padding.Y + card.Padding.Y + card.Padding.W));
            cardText = toolTip;
            cardTextColor = textColor;
            cardBackground = backgroundColor;
            cardScale = GUI.Scale;
            return card;
        }

        private static bool Ready(GUITextBlock text) => ReferenceEquals(bakedCard, text) && surface != null;

        private static void Show(SpriteBatch batch, Rectangle rect) {
            requested = true;
            double now = Timing.TotalTime;
            bool appearing = !TooltipTiming.Holds(lastDrawn, now);
            lastDrawn = now;
            Box target = new Box(rect.X, rect.Y, rect.Width, rect.Height);
            bool reduced = MotionClient.Runtime.ReducedMotion;
            if (visual == null || !visual.Alive) {
                visual?.Dispose();
                visual = new MotionVisual(MotionClient.Runtime, target, hidden: true);
                appearing = true;
            }
            bool reversing = exiting;
            exiting = false;
            if (appearing) {
                visual.Place(target, MotionPresets.Window, direct: true);
                visual.Opacity.Snap(0);
                visual.Lift.Snap(8);
                visual.Scale.Snap(.96f);
            } else {
                visual.Place(target, MotionPresets.Hover, direct: reduced);
            }
            if (appearing || reversing) {
                if (reduced) {
                    visual.Opacity.Snap(1);
                    visual.Lift.Snap(0);
                    visual.Scale.Snap(1);
                } else {
                    visual.Opacity.To(1, MotionPresets.Fade);
                    visual.Lift.To(0, MotionPresets.Window);
                    visual.Scale.To(1, MotionPresets.Window);
                }
            }
            Draw(batch);
        }

        private static void Draw(SpriteBatch batch) {
            VisualPose pose = visual.Pose;
            if (pose.Opacity <= 0.001f) { return; }
            Draws++;
            MotionCard.Draw(batch, surface, bakedSize, pose, cardShutter);
        }

        private static void StartExit() {
            if (MotionClient.Runtime.ReducedMotion) {
                Retire();
                return;
            }
            exiting = true;
            visual.Opacity.To(0, MotionPresets.Fade);
            visual.Lift.To(8, MotionPresets.Window);
            visual.Scale.To(.96f, MotionPresets.Window);
        }

        private static void Retire() {
            visual?.Dispose();
            visual = null;
            exiting = false;
        }

        private static bool Bake(SpriteBatch batch, GUITextBlock text) {
            Point size = text.Rect.Size;
            if (size.X < 1 || size.Y < 1) { return false; }
            GraphicsDevice device = batch.GraphicsDevice;
            if (!MotionCard.Fit(device, ref surface, size, budget, out bool allocated)) {
                throw new InvalidOperationException("tooltip surface budget exhausted");
            }
            if (allocated) { Allocations++; }
            bakedSize = size;
            Bakes++;
            text.RectTransform.AbsoluteOffset = Point.Zero;
            text.SetTextPos();
            device.SetRenderTarget(surface.Target);
            device.ScissorRectangle = surface.Target.Bounds;
            device.Clear(Color.Transparent);
            batch.Begin(SpriteSortMode.Deferred, MotionCard.Bake, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone);
            text.DrawManually(batch);
            batch.End();
            bakedCard = text;
            return true;
        }

        public static void Reset() {
            visual?.Dispose();
            visual = null;
            surface?.Dispose();
            surface = null;
            card = null;
            bakedCard = null;
            failedCard = null;
            lastDrawn = double.NegativeInfinity;
            requested = false;
            exiting = false;
            Contained.Revive(Feature);
        }
    }
}
