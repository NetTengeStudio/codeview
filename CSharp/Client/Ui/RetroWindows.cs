using System;
using System.Collections.Generic;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;
using ScpItdMod.Motion;

namespace ScpItdMod.Ui {
    public abstract class RetroWindow {
        private const int CloseId = 1;
        private const int TitleId = 2;

        internal GUICustomComponent Host;
        internal readonly UiState State = new UiState();
        internal bool Dragging;
        internal Vector2 Grab;
        internal MotionVisual Visual;
        internal bool Closing;
        internal double CloseAt;

        public Box Rect;

        public bool IsOpen { get; internal set; }

        public abstract string Title { get; }

        public virtual bool Modal => false;

        protected virtual bool Custom => false;

        protected virtual void Full(ICanvas c, UiState ui, RetroFaces f) { }

        protected virtual void Opened() { }

        protected virtual void Closed() { }

        public abstract void Body(ICanvas c, UiState ui, RetroFaces f, Box body);

        public virtual void Tick(float dt) { }

        public void Open() => RetroWindows.Open(this);

        public void Close() => RetroWindows.Close(this);

        internal void FireOpened() => Opened();

        internal void FireClosed() => Closed();

        internal void Render(BatchCanvas canvas, RetroFaces f, bool top, in Box frame, ICanvas faded) {
            UiState ui = State;
            if (Custom) {
                Full(faded, ui, f);
                return;
            }
            Box body = Retro.Window(faded, f, frame, Title, 1.0f);
            float e = Retro.Px(faded, Retro.EdgePx);
            float th = Retro.Px(faded, Retro.TitlePx);
            var close = new Box(frame.Right - e - th, frame.Y + e, th, th);
            if (!Closing && ui.Over(CloseId, close)) { faded.Fill(close, Retro.Bad); }
            Paint_Cross(faded, close.Inset(Retro.Px(faded, 6.0f)), Retro.PlateSolid);
            if (!Closing && ui.Click(CloseId, close)) {
                Close();
                return;
            }
            var titleBar = new Box(frame.X, frame.Y, frame.W - th - e, th + e);
            if (!Closing && ui.Over(TitleId, titleBar) && ui.In.Pressed && !Dragging) {
                Dragging = true;
                Grab = new Vector2(ui.In.X - Rect.X, ui.In.Y - Rect.Y);
            }
            faded.PushClip(body);
            Body(faded, ui, f, body);
            faded.PopClip();
        }

        private static void Paint_Cross(ICanvas c, Box b, Rgba ink) {
            float t = Retro.Px(c, 2.0f);
            int n = (int)MathF.Max(1, b.W / t);
            for (int i = 0; i < n; i++) {
                float k = i / (float)Math.Max(1, n - 1);
                c.Fill(new Box(b.X + k * (b.W - t), b.Y + k * (b.H - t), t, t), ink);
                c.Fill(new Box(b.X + k * (b.W - t), b.Bottom - t - k * (b.H - t), t, t), ink);
            }
        }
    }

    public static class RetroWindows {
        // A closing window is removed once its opacity and scale have settled. The limit only guards against a motion that never settles.
        private const double CloseLimit = 0.6;

        private static readonly List<RetroWindow> open = new List<RetroWindow>();
        private static readonly BatchCanvas canvas = new BatchCanvas();
        private static readonly OpacityCanvas faded = new OpacityCanvas();
        private static RetroFaces faces;
        private static float facesScale;
        private static int facesGeneration = -1;

        public static bool Any {
            get {
                for (int i = 0; i < open.Count; i++) { if (!open[i].Closing) { return true; } }
                return false;
            }
        }

        internal static IReadOnlyList<RetroWindow> OpenWindows => open;

        public static RetroWindow Top {
            get {
                for (int i = open.Count - 1; i >= 0; i--) { if (!open[i].Closing) { return open[i]; } }
                return null;
            }
        }

        public static void Attach(Harmony harmony) {
            harmony.Patch(AccessTools.Method(typeof(GameScreen), nameof(GameScreen.AddToGUIUpdateList), Type.EmptyTypes),
                postfix: ModuleHost.Bind(typeof(RetroWindows), nameof(AddPostfix)));
            harmony.Patch(AccessTools.Method(typeof(GUI), nameof(GUI.TogglePauseMenu), Type.EmptyTypes),
                prefix: ModuleHost.Bind(typeof(RetroWindows), nameof(PausePrefix)));
        }

        public static void Release() {
            for (int i = open.Count - 1; i >= 0; i--) { Finish(open[i], fire: false); }
            open.Clear();
            canvas.Release();
            faces = null;
        }

        public static bool Owns(GUIComponent component) {
            for (int i = 0; i < open.Count; i++) {
                if (ReferenceEquals(open[i].Host, component)) { return true; }
            }
            return false;
        }

        public static bool MouseOver => GUI.MouseOn != null && Owns(GUI.MouseOn);

        public static void Open(RetroWindow w) {
            if (w == null) { return; }
            open.Remove(w);
            open.Add(w);
            if (w.IsOpen) {
                w.Closing = false;
                w.Visual?.Opacity.To(1.0f, MotionPresets.Fade);
                return;
            }
            w.IsOpen = true;
            w.Dragging = false;
            w.Closing = false;
            if (w.Rect.Empty) { w.Rect = new Box(GameMain.GraphicsWidth * 0.3f, GameMain.GraphicsHeight * 0.2f, GameMain.GraphicsWidth * 0.4f, GameMain.GraphicsHeight * 0.55f); }
            w.Visual?.Dispose();
            w.Visual = new MotionVisual(MotionClient.Runtime, w.Rect);
            w.Visual.Scale.Snap(0.94f);
            w.Visual.Opacity.Snap(0.0f);
            w.Visual.Opacity.To(1.0f, MotionPresets.Fade);
            w.Visual.Scale.To(1.0f, MotionPresets.Window);
            w.Host = new GUICustomComponent(new RectTransform(new Point(1, 1), GUI.Canvas), (sb, _) => Draw(sb, w), (dt, _) => Update(w, dt)) { CanBeFocused = true };
            w.FireOpened();
        }

        public static void Close(RetroWindow w) {
            if (w == null || !w.IsOpen || w.Closing) { return; }
            // The window keeps drawing while its opacity settles; input stops at
            // once, so a closing window can never be clicked or dragged.
            w.Closing = true;
            w.CloseAt = MotionClient.InputTime + CloseLimit;
            w.Visual?.Opacity.To(0.0f, MotionPresets.Fade);
            w.Visual?.Scale.To(0.97f, MotionPresets.Window);
            w.Dragging = false;
        }

        private static void Finish(RetroWindow w, bool fire) {
            if (w == null) { return; }
            bool wasOpen = w.IsOpen;
            w.IsOpen = false;
            w.Closing = false;
            open.Remove(w);
            w.Host?.RectTransform.Parent?.GUIComponent?.RemoveChild(w.Host);
            w.Host = null;
            w.Visual?.Dispose();
            w.Visual = null;
            if (fire && wasOpen) { w.FireClosed(); }
        }

        public static void AddPostfix() {
            for (int i = 0; i < open.Count; i++) {
                RetroWindow w = open[i];
                if (w.Host == null) { continue; }
                Box r = w.Rect.Snap();
                w.Host.RectTransform.AbsoluteOffset = new Point((int)r.X, (int)r.Y);
                w.Host.RectTransform.NonScaledSize = new Point(Math.Max(1, (int)r.W), Math.Max(1, (int)r.H));
                w.Host.AddToGUIUpdateList(order: 1);
            }
        }

        public static bool PausePrefix() {
            RetroWindow top = Top;
            if (top == null) { return true; }
            Close(top);
            return false;
        }

        private static void Update(RetroWindow w, float dt) {
            if (w.Closing) {
                bool settled = w.Visual?.Alive != true || !w.Visual.Opacity.Running && !w.Visual.Scale.Running;
                if (settled || MotionClient.InputTime >= w.CloseAt) { Finish(w, fire: true); }
                return;
            }
            bool mouseOn = ReferenceEquals(GUI.MouseOn, w.Host);
            if (mouseOn && PlayerInput.PrimaryMouseButtonDown() && !ReferenceEquals(Top, w)) { Open(w); }
            Vector2 m = PlayerInput.MousePosition;
            if (w.Dragging) {
                if (!PlayerInput.PrimaryMouseButtonHeld()) {
                    w.Dragging = false;
                } else {
                    float x = Math.Clamp(m.X - w.Grab.X, -w.Rect.W + 60, GameMain.GraphicsWidth - 60);
                    float y = Math.Clamp(m.Y - w.Grab.Y, 0, GameMain.GraphicsHeight - 40);
                    w.Rect = new Box(x, y, w.Rect.W, w.Rect.H);
                }
            }
            w.Visual?.Place(w.Rect, MotionPresets.Layout);
            UiInput input = mouseOn || w.Dragging ? new UiInput {
                X = m.X,
                Y = m.Y,
                Down = PlayerInput.PrimaryMouseButtonHeld(),
                Pressed = PlayerInput.PrimaryMouseButtonDown(),
                Released = PlayerInput.PrimaryMouseButtonClicked(),
                AltPressed = PlayerInput.SecondaryMouseButtonClicked(),
                Wheel = PlayerInput.ScrollWheelSpeed,
                Now = Timing.TotalTime
            } : UiInput.None;
            w.State.Begin(input);
            w.Tick(dt);
        }

        private static void Draw(SpriteBatch sb, RetroWindow w) {
            float scale = GUI.Scale;
            if (faces == null || facesScale != scale || faces.Body == null || faces.Small == null || faces.Head == null || facesGeneration != FontStore.Generation) {
                facesGeneration = FontStore.Generation;
                faces = Faces.Retro(scale);
                facesScale = scale;
            }
            if (faces.Body == null || faces.Small == null || faces.Head == null) { return; }
            VisualPose pose = w.Visual?.Pose ?? new VisualPose(w.Rect);
            float alpha = w.Visual?.Alive == true ? Scalar.Clamp01(pose.Opacity) : 1.0f;
            if (alpha <= 0.01f) { return; }
            canvas.Begin(sb, scale);
            try {
                faded.Inner = canvas;
                faded.Alpha = alpha;
                w.Render(canvas, faces, ReferenceEquals(Top, w), pose.Bounds, faded);
            } finally {
                canvas.End();
            }
        }
    }
}
