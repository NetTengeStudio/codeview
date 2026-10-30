using System;
using System.Collections.Generic;
using ScpItdMod.Ui;
using ScpItdMod.Motion;

namespace ScpItdMod.Xp {
    public enum WinState : byte { Normal, Minimized, Maximized }

    public enum WinPart : byte { None, Client, Title, Close, Max, Min, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

    public interface IXpApp {
        string Title { get; }

        string Icon { get; }

        void Paint(ICanvas c, UiState ui, XpFaces f, Box client, XpShell shell, XpWindow self);

        bool WantsClose(XpShell shell, XpWindow self);

        void Closed(XpShell shell);
    }

    public interface IXpFullscreen : IXpApp {
        bool Done { get; }

        void Frame(ICanvas c, XpFaces f, in UiInput input, XpShell shell);

        bool Back();
    }

    public interface IXpSessionLifecycle {
        void OnSessionSuspending(XpShell shell);

        void OnSessionResumed(XpShell shell);
    }

    public sealed class XpWindow {
        public int Id { get; internal set; }
        public IXpApp App;
        public Box Rect;
        public WinState State { get; internal set; }
        public bool Resizable = true;
        public bool Minimizable = true;
        public bool Dialog;
        public float MinW = 220;
        public float MinH = 140;
        public XpWindow Owner { get; internal set; }
        public readonly UiState Ui = new UiState();
        internal bool WasMax;
        internal int Order;
        internal MotionVisual Visual;

        public bool Open { get; internal set; }

        public string Title => App?.Title ?? "";
    }

    public readonly struct XpDeparture {
        public readonly string Title, Icon;
        public readonly MotionVisual Visual;
        internal XpDeparture(XpWindow window, MotionVisual visual) { Title = window.Title; Icon = window.App?.Icon; Visual = visual; }
    }

    public sealed class XpDesk {
        public const float Grip = 4.0f;
        public const float Cascade = 26.0f;
        public const int MaxWindows = 24;

        private readonly List<XpWindow> z = new List<XpWindow>(MaxWindows);
        private readonly List<XpWindow> task = new List<XpWindow>(MaxWindows);
        private readonly List<XpDeparture> departures = new List<XpDeparture>(MaxWindows);
        public MotionRuntime Motion { get; set; }
        public IReadOnlyList<XpDeparture> Departures => departures;
        private int nextId = 1;
        private int nextOrder;
        private XpWindow captured;
        private WinPart capturePart;
        private float grabX;
        private float grabY;
        private Box grabRect;

        public Box Work = new Box(0, 0, XpLook.ScreenW, XpLook.ScreenH - XpLook.TaskbarH);

        public IReadOnlyList<XpWindow> Stack => z;

        public XpWindow Focused { get; private set; }

        public XpWindow Captured => captured;

        public WinPart CapturePart => capturePart;

        public int Count => z.Count;

        public XpWindow Open(XpWindow w, XpWindow owner = null) {
            if (w == null || w.Open || z.Count >= MaxWindows) { return null; }
            w.Id = nextId++;
            w.Order = nextOrder++;
            w.Owner = owner;
            w.Open = true;
            w.State = WinState.Normal;
            if (w.Rect.Empty) { w.Rect = new Box(0, 0, 480, 340); }
            if (owner != null) {
                w.Dialog = true;
                Box o = Frame(owner);
                w.Rect = new Box(MathF.Round(o.CenterX - w.Rect.W * 0.5f), MathF.Round(o.CenterY - w.Rect.H * 0.5f), w.Rect.W, w.Rect.H);
            } else if (w.Rect.X == 0 && w.Rect.Y == 0) {
                int slot = 0;
                for (int i = 0; i < z.Count; i++) {
                    if (z[i].Owner == null) { slot++; }
                }
                float off = (slot % 8) * Cascade;
                w.Rect = new Box(Work.X + 40 + off, Work.Y + 30 + off, w.Rect.W, w.Rect.H);
            }
            w.Rect = Clamp(w.Rect);
            Frame(w);
            z.Add(w);
            Focus(w);
            return w;
        }

        public void Close(XpWindow w) {
            if (w == null || !w.Open) { return; }
            for (int i = z.Count - 1; i >= 0; i--) {
                if (i < z.Count && ReferenceEquals(z[i].Owner, w)) { Close(z[i]); }
            }
            Depart(w);
            w.Open = false;
            z.Remove(w);
            if (ReferenceEquals(captured, w)) { captured = null; }
            if (ReferenceEquals(Focused, w)) {
                Focused = null;
                XpWindow next = w.Owner != null && w.Owner.Open ? w.Owner : TopVisible();
                if (next != null) { Focus(next); }
            }
        }

        public XpWindow ModalOf(XpWindow w) {
            for (int i = z.Count - 1; i >= 0; i--) {
                if (ReferenceEquals(z[i].Owner, w) && z[i].Dialog) { return ModalOf(z[i]) ?? z[i]; }
            }
            return null;
        }

        public bool Blocked(XpWindow w) => ModalOf(w) != null;

        public void Focus(XpWindow w) {
            if (w == null || !w.Open) {
                Focused = null;
                return;
            }
            XpWindow root = w;
            while (root.Owner != null) { root = root.Owner; }
            if (root.State == WinState.Minimized) { root.State = root.WasMax ? WinState.Maximized : WinState.Normal; }
            Raise(root);
            XpWindow modal = ModalOf(w);
            Focused = modal ?? w;
        }

        private void Raise(XpWindow root) {
            z.Remove(root);
            z.Add(root);
            for (int i = 0; i < z.Count - 1;) {
                if (OwnedBy(z[i], root)) {
                    XpWindow d = z[i];
                    z.RemoveAt(i);
                    z.Add(d);
                    continue;
                }
                i++;
            }
        }

        private static bool OwnedBy(XpWindow w, XpWindow root) {
            for (XpWindow o = w.Owner; o != null; o = o.Owner) {
                if (ReferenceEquals(o, root)) { return true; }
            }
            return false;
        }

        private XpWindow TopVisible() {
            for (int i = z.Count - 1; i >= 0; i--) {
                if (Visible(z[i])) { return z[i]; }
            }
            return null;
        }

        public bool Visible(XpWindow w) {
            for (XpWindow o = w; o != null; o = o.Owner) {
                if (o.State == WinState.Minimized) { return false; }
            }
            return w.Open;
        }

        public void Minimize(XpWindow w) {
            if (w == null || !w.Minimizable || w.Owner != null) { return; }
            Depart(w);
            w.WasMax = w.State == WinState.Maximized;
            w.State = WinState.Minimized;
            if (ReferenceEquals(Focused, w) || Focused != null && OwnedBy(Focused, w)) {
                Focused = null;
                XpWindow next = TopVisible();
                if (next != null) { Focus(next); }
            }
        }

        public void ToggleMax(XpWindow w) {
            if (w == null || !w.Resizable || w.Owner != null) { return; }
            if (w.State == WinState.Maximized) {
                w.State = WinState.Normal;
            } else {
                w.State = WinState.Maximized;
            }
            Focus(w);
        }

        public void ActivateFromTask(XpWindow w) {
            if (w == null) { return; }
            if (ReferenceEquals(Focused, w) && w.State != WinState.Minimized) {
                Minimize(w);
                return;
            }
            if (w.State == WinState.Minimized) { w.State = w.WasMax ? WinState.Maximized : WinState.Normal; }
            Focus(w);
        }

        public IReadOnlyList<XpWindow> Tasks() {
            task.Clear();
            for (int i = 0; i < z.Count; i++) {
                if (z[i].Owner == null) { task.Add(z[i]); }
            }
            task.Sort((a, b) => a.Order.CompareTo(b.Order));
            return task;
        }

        public Box Frame(XpWindow w) {
            Box target = w.State == WinState.Maximized
                ? new Box(Work.X - XpLook.Border, Work.Y - XpLook.Border, Work.W + XpLook.Border * 2, Work.H + XpLook.Border * 2) : w.Rect;
            if (Motion == null || !w.Open) { return target; }
            if (w.Visual?.Alive != true) {
                w.Visual?.Dispose();
                w.Visual = new MotionVisual(Motion, target.Offset(0, 10), true);
                w.Visual.Play(MotionPatterns.Visibility(w.Visual, true, MotionStyle.Window));
            }
            w.Visual.Place(target, MotionPresets.Window, ReferenceEquals(captured, w));
            return w.Visual.Pose.Bounds;
        }

        private void Depart(XpWindow window) {
            if (window.Visual?.Alive != true) { return; }
            MotionVisual visual = window.Visual; window.Visual = null;
            Box frame = visual.Pose.Bounds;
            visual.Place(new Box(frame.CenterX - 40, Work.Bottom, 80, XpLook.TitleH), MotionPresets.Window);
            visual.Play(MotionPatterns.Visibility(visual, false, MotionStyle.Window));
            if (departures.Count == MaxWindows * 2) { departures[0].Visual.Dispose(); departures.RemoveAt(0); }
            departures.Add(new XpDeparture(window, visual));
        }

        public void UpdateMotion() {
            for (int i = departures.Count - 1; i >= 0; i--) {
                MotionVisual visual = departures[i].Visual;
                if (!visual.Alive || !visual.Opacity.Running && visual.Opacity.Value <= 0) { visual.Dispose(); departures.RemoveAt(i); }
            }
        }

        public static Box Client(in Box frame) =>
            new Box(frame.X + XpLook.Border, frame.Y + XpLook.TitleH, frame.W - XpLook.Border * 2, frame.H - XpLook.TitleH - XpLook.Border);

        public static Box CaptionButton(in Box frame, int index) {
            float x = frame.Right - XpLook.CaptionRight - XpLook.CaptionBtn - index * (XpLook.CaptionBtn + XpLook.CaptionGap);
            return new Box(x, frame.Y + XpLook.CaptionTop, XpLook.CaptionBtn, XpLook.CaptionBtn);
        }

        public XpWindow Hit(float x, float y, out WinPart part) {
            part = WinPart.None;
            for (int i = z.Count - 1; i >= 0; i--) {
                XpWindow w = z[i];
                if (!Visible(w)) { continue; }
                Box f = Frame(w);
                var grip = new Box(f.X - Grip, f.Y - Grip, f.W + Grip * 2, f.H + Grip * 2);
                if (!grip.Contains(x, y)) { continue; }
                part = PartOf(w, f, x, y);
                if (part == WinPart.None) { continue; }
                return w;
            }
            return null;
        }

        private static WinPart PartOf(XpWindow w, in Box f, float x, float y) {
            bool inside = f.Contains(x, y);
            bool sizing = w.Resizable && w.State == WinState.Normal;
            if (sizing) {
                float e = XpLook.Border + 1;
                bool l = x < f.X + e;
                bool r = x >= f.Right - e;
                bool t = y < f.Y + e;
                bool b = y >= f.Bottom - e;
                if (t && l) { return WinPart.TopLeft; }
                if (t && r) { return WinPart.TopRight; }
                if (b && l) { return WinPart.BottomLeft; }
                if (b && r) { return WinPart.BottomRight; }
                if (l) { return WinPart.Left; }
                if (r) { return WinPart.Right; }
                if (t) { return WinPart.Top; }
                if (b) { return WinPart.Bottom; }
            }
            if (!inside) { return WinPart.None; }
            if (y < f.Y + XpLook.TitleH) {
                if (CaptionButton(f, 0).Contains(x, y)) { return WinPart.Close; }
                if (!w.Dialog && w.Resizable && CaptionButton(f, 1).Contains(x, y)) { return WinPart.Max; }
                int minIndex = w.Resizable ? 2 : 1;
                if (!w.Dialog && w.Minimizable && CaptionButton(f, minIndex).Contains(x, y)) { return WinPart.Min; }
                return WinPart.Title;
            }
            return WinPart.Client;
        }

        public bool Press(float x, float y, out XpWindow window, out WinPart part) {
            window = Hit(x, y, out part);
            if (window == null) { return false; }
            Focus(window);
            if (Blocked(window)) {
                part = WinPart.None;
                return true;
            }
            if (part == WinPart.Title && window.State == WinState.Normal || part >= WinPart.Left) {
                captured = window;
                capturePart = part;
                grabX = x;
                grabY = y;
                grabRect = window.Rect;
            }
            return true;
        }

        public void Drag(float x, float y) {
            if (captured == null || !captured.Open) {
                captured = null;
                return;
            }
            float dx = x - grabX;
            float dy = y - grabY;
            Box r = grabRect;
            switch (capturePart) {
                case WinPart.Title:
                    r = new Box(r.X + dx, r.Y + dy, r.W, r.H);
                    break;
                default: {
                    float l = r.X, t = r.Y, rr = r.Right, b = r.Bottom;
                    if (capturePart is WinPart.Left or WinPart.TopLeft or WinPart.BottomLeft) { l = MathF.Min(l + dx, rr - captured.MinW); }
                    if (capturePart is WinPart.Right or WinPart.TopRight or WinPart.BottomRight) { rr = MathF.Max(rr + dx, l + captured.MinW); }
                    if (capturePart is WinPart.Top or WinPart.TopLeft or WinPart.TopRight) { t = MathF.Min(t + dy, b - captured.MinH); }
                    if (capturePart is WinPart.Bottom or WinPart.BottomLeft or WinPart.BottomRight) { b = MathF.Max(b + dy, t + captured.MinH); }
                    r = new Box(l, t, rr - l, b - t);
                    break;
                }
            }
            captured.Rect = Clamp(r);
        }

        public void Release() {
            captured = null;
            capturePart = WinPart.None;
        }

        public Box Clamp(Box r) {
            float w = MathF.Min(r.W, Work.W);
            float h = MathF.Min(r.H, Work.H);
            float x = Math.Clamp(r.X, Work.X - w + 60, Work.Right - 60);
            float y = Math.Clamp(r.Y, Work.Y, Work.Bottom - XpLook.TitleH);
            return new Box(MathF.Round(x), MathF.Round(y), MathF.Round(w), MathF.Round(h));
        }

        public void CloseAll() {
            for (int i = z.Count - 1; i >= 0; i--) {
                if (i < z.Count) { Close(z[i]); }
            }
            for (int i = 0; i < departures.Count; i++) { departures[i].Visual.Dispose(); }
            departures.Clear();
            captured = null;
            Focused = null;
        }
    }
}
