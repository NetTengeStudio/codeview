using System;
using System.Collections.Generic;
using ScpItdMod.Paper;
using ScpItdMod.Pc;
using ScpItdMod.Ui;

namespace ScpItdMod.Xp {
    public interface IXpTyping {
        InkEdit Edit { get; }

        bool Command(XpShell shell, char control);
    }

    public sealed class XpShortcut {
        public string Name;
        public string Icon;
        public string Large;
        public string App;
        public object Arg;
        public bool Bold;
        private string labelName;
        private IFace labelFace;
        private float labelWidth;
        internal string FirstLine { get; private set; }
        internal string SecondLine { get; private set; }

        internal void LayoutLabel(IFace face, float width) {
            if (ReferenceEquals(labelName, Name) && ReferenceEquals(labelFace, face) && labelWidth == width) { return; }
            labelName = Name;
            labelFace = face;
            labelWidth = width;
            FirstLine = Name ?? "";
            SecondLine = null;
            if (FirstLine.Length == 0 || Paint.Measure(face, FirstLine) <= width) { return; }
            int split = FirstLine.LastIndexOf(' ', Math.Min(FirstLine.Length - 1, Paint.Fit(face, FirstLine, width)));
            if (split > 0) {
                SecondLine = FirstLine.Substring(split + 1);
                FirstLine = FirstLine.Substring(0, split);
            }
        }
    }

    public sealed class XpShell {
        public const double DoubleClick = 0.45;
        private const int ShellId = 1 << 20;
        private static readonly Ramp StartSeparator = new Ramp((0, 0xF8F6EE), (0.5f, 0xDA884A), (1, 0x5D8DEB));

        public readonly XpDesk Desk = new XpDesk();
        public readonly UiState Ui = new UiState();
        private readonly UiState startInput = new UiState();
        private readonly UiState popupInput = new UiState();
        public readonly List<XpShortcut> Icons = new List<XpShortcut>();
        public readonly List<XpShortcut> StartLeft = new List<XpShortcut>();
        public readonly List<XpShortcut> StartRight = new List<XpShortcut>();
        private readonly Dictionary<string, Func<XpShell, object, IXpApp>> apps = new Dictionary<string, Func<XpShell, object, IXpApp>>(StringComparer.Ordinal);
        private readonly Dictionary<string, XpWindow> single = new Dictionary<string, XpWindow>(StringComparer.Ordinal);

        private readonly OpacityCanvas motionCanvas = new OpacityCanvas();
        private MotionVisual startMotion;
        public XpShell() { Desk.Motion = Motion.MotionClient.Runtime; }

        private XpWindow pointerOwner;
        private XpWindow captionWindow;
        private WinPart captionPart;
        private int selectedIcon = -1;
        private int lastClickId = -1;
        private double lastClickTime = double.NegativeInfinity;
        private bool startPressed;

        private string[] popupItems;
        private bool[] popupEnabled;
        private string[] popupKeys;
        private Action<int> popupPick;
        private float popupX;
        private float popupY;
        private XpWindow popupOwner;
        private bool popupFresh;
        private Box popupBox;
        private bool suspended;

        public PcSession Pc;
        public Durak.DurakSession Cards;
        public Chess.ChessSession Chess;
        public Func<string> PasteText;
        public Action<string> CopyText;
        public object Cams;
        public string Clock = "";
        public bool StartOpen;
        public bool Speaking;
        public bool ExitRequested;
        public UiInput In;
        public double Now;

        public XpWindow FocusedWindow => Desk.Focused;

        public double VisualNow => In.Now;

        public IXpFullscreen Program { get; private set; }

        public IXpApp FocusedApp => Program ?? Desk.Focused?.App;

        public bool PopupOpen => popupItems != null;

        public void Register(string id, Func<XpShell, object, IXpApp> factory, bool singleInstance = false) {
            apps[id] = factory;
            if (singleInstance) { single[id] = null; }
        }

        public XpWindow Launch(string id, object arg = null, Box rect = default) {
            if (!apps.TryGetValue(id, out var factory)) { return null; }
            if (single.TryGetValue(id, out XpWindow existing) && existing != null && existing.Open) {
                Desk.ActivateFromTask(existing);
                if (existing.State == WinState.Minimized) { Desk.Focus(existing); }
                if (existing.App is IXpArg reuse) { reuse.Argue(this, arg); }
                return existing;
            }
            IXpApp app = factory(this, arg);
            if (app == null) { return null; }
            if (app is IXpFullscreen full) { Program = full; StartOpen = false; ClosePopup(); return null; }
            var w = new XpWindow { App = app, Rect = rect };
            if (rect.Empty && app is IXpSized sized) { sized.Size(w); }
            if (Desk.Open(w) == null) { return null; }
            if (single.ContainsKey(id)) { single[id] = w; }
            StartOpen = false;
            return w;
        }

        public XpWindow Dialog(XpWindow owner, IXpApp app, float width, float height) {
            var w = new XpWindow { App = app, Rect = new Box(0, 0, width, height), Resizable = false, Minimizable = false };
            return Desk.Open(w, owner);
        }

        public XpWindow Ask(XpWindow owner, string title, string text, string[] buttons, Action<int> pick, string icon = null) {
            var msg = new XpMessage(title, text, buttons, pick, icon);
            return Dialog(owner, msg, msg.Width, msg.Height);
        }

        public void Close(XpWindow w) {
            if (w == null || !w.Open) { return; }
            if (popupOwner == w) { ClosePopup(); }
            for (int i = Desk.Stack.Count - 1; i >= 0; i--) {
                if (i < Desk.Stack.Count && ReferenceEquals(Desk.Stack[i].Owner, w)) { Close(Desk.Stack[i]); }
            }
            if (!w.Open) { return; }
            string registration = null;
            foreach (var pair in single) { if (ReferenceEquals(pair.Value, w)) { registration = pair.Key; break; } }
            if (registration != null) { single[registration] = null; }
            Desk.Close(w);
            if (!suspended) { w.App?.Closed(this); }
        }

        public void RequestClose(XpWindow w) {
            if (w == null || !w.Open || Desk.Blocked(w)) { return; }
            if (w.App == null || w.App.WantsClose(this, w)) { Close(w); }
        }

        public void Popup(XpWindow owner, float x, float y, string[] items, bool[] enabled, string[] keys, Action<int> pick) {
            popupInput.ResetInput();
            popupItems = items;
            popupEnabled = enabled;
            popupKeys = keys;
            popupPick = pick;
            popupX = x;
            popupY = y;
            popupOwner = owner;
            popupFresh = true;
        }

        public void ClosePopup() {
            popupInput.ResetInput();
            popupItems = null; popupEnabled = null; popupKeys = null;
            popupPick = null;
            popupOwner = null;
        }

        public void CloseProgram() {
            IXpFullscreen program = Program;
            Program = null;
            program?.Closed(this);
            ResetTransientInput();
        }

        public bool Escape() {
            if (Program != null) {
                if (!Program.Back()) { CloseProgram(); }
                return true;
            }
            if (PopupOpen) {
                ClosePopup();
                return true;
            }
            if (StartOpen) {
                StartOpen = false;
                return true;
            }
            return false;
        }

        public void Shutdown() {
            ClosePopup();
            if (Program != null) { CloseProgram(); }
            for (int i = Desk.Stack.Count - 1; i >= 0; i--) {
                if (i < Desk.Stack.Count) { Close(Desk.Stack[i]); }
            }
            Desk.CloseAll();
            startMotion?.Dispose(); startMotion = null;
            StartOpen = false;
            suspended = true;
        }

        public void Suspend() {
            if (suspended) { return; }
            ClosePopup();
            if (Program is IXpSessionLifecycle program) { program.OnSessionSuspending(this); }
            ResetTransientInput();
            for (int i = 0; i < Desk.Stack.Count; i++) {
                if (Desk.Stack[i].App is IXpSessionLifecycle lifecycle) { lifecycle.OnSessionSuspending(this); }
            }
            StartOpen = false;
            ExitRequested = false;
            suspended = true;
        }

        public void Resume() {
            if (!suspended) { return; }
            suspended = false;
            if (Program is IXpSessionLifecycle program) { program.OnSessionResumed(this); }
            ExitRequested = false;
            ResetTransientInput();
            for (int i = 0; i < Desk.Stack.Count; i++) {
                if (Desk.Stack[i].App is IXpSessionLifecycle lifecycle) { lifecycle.OnSessionResumed(this); }
            }
        }

        private void ResetTransientInput() {
            Desk.Release();
            Ui.ResetInput();
            startInput.ResetInput(); popupInput.ResetInput();
            In = UiInput.None;
            pointerOwner = null;
            captionWindow = null;
            captionPart = WinPart.None;
            startPressed = false;
            lastClickId = -1;
            lastClickTime = double.NegativeInfinity;
            for (int i = 0; i < Desk.Stack.Count; i++) { Desk.Stack[i].Ui.ResetInput(); }
        }

        private Box StartButtonBox => new Box(0, XpLook.ScreenH - XpLook.TaskbarH, XpLook.StartW, XpLook.TaskbarH);

        private Box TaskbarBox => new Box(0, XpLook.ScreenH - XpLook.TaskbarH, XpLook.ScreenW, XpLook.TaskbarH);

        private Box StartMenuBox {
            get {
                float h = StartMenuHeight();
                return startMotion?.Alive == true ? startMotion.Pose.Bounds : new Box(0, XpLook.ScreenH - XpLook.TaskbarH - h, XpLook.StartMenuW, h);
            }
        }

        private float StartMenuHeight() {
            float left = 6 + StartLeft.Count * (XpLook.StartLeftRow + 4) + 10;
            float right = 6 + StartRight.Count * (XpLook.StartRightRow + 4) + 10;
            return XpLook.StartHeaderH + MathF.Max(left, right) + XpLook.StartFooterH;
        }

        public void Frame(ICanvas c, XpFaces f, in UiInput input, double? sessionTime = null) {
            In = input;
            Now = sessionTime ?? input.Now;
            if (Program != null) {
                if (Program.Done) { CloseProgram(); }
                else { Program.Frame(c, f, input, this); return; }
            }
            Desk.UpdateMotion();
            float menuHeight = StartMenuHeight();
            Box menuTarget = new Box(0, XpLook.ScreenH - XpLook.TaskbarH - menuHeight, XpLook.StartMenuW, menuHeight);
            if (StartOpen && startMotion?.Alive != true) { startMotion?.Dispose(); startMotion = new MotionVisual(Desk.Motion, menuTarget.Offset(0, 12), true); }
            if (startMotion?.Alive == true) { startMotion.Place(StartOpen ? menuTarget : menuTarget.Offset(0, 12), Motion.MotionPresets.Window); startMotion.Opacity.To(StartOpen ? 1 : 0, Motion.MotionPresets.Fade); }
            IReadOnlyList<XpWindow> stack = Desk.Stack;
            for (int i = 0; i < stack.Count; i++) { if (stack[i].App is XpMessage message) { message.Fit(f.Body, stack[i], Desk.Work); } }
            Route(input, out bool overShell);
            c.Fill(new Box(0, 0, XpLook.ScreenW, XpLook.ScreenH), XpLook.Desktop);
            Ui.Begin(overShell ? input : UiInput.None);
            Desktop(c, f);
            for (int i = 0; i < stack.Count; i++) {
                XpWindow w = stack[i];
                if (!Desk.Visible(w)) { continue; }
                PaintWindow(c, f, w, input);
            }
            for (int i = 0; i < Desk.Departures.Count; i++) {
                XpDeparture departure = Desk.Departures[i];
                if (!departure.Visual.Alive) { continue; }
                VisualPose pose = departure.Visual.Pose; motionCanvas.Inner = c; motionCanvas.Alpha = pose.Opacity;
                XpPaint.Window(motionCanvas, f, pose.Bounds, departure.Title, departure.Icon, false, false, false, false, false, -1, false);
            }
            Taskbar(c, f, overShell ? input : UiInput.None);
            if (startMotion?.Alive == true && (StartOpen || startMotion.Opacity.Running)) {
                motionCanvas.Inner = c; motionCanvas.Alpha = startMotion.Opacity.Value;
                StartMenu(motionCanvas, f, StartOpen ? input : UiInput.None);
            }
            if (!StartOpen) { startInput.ResetInput(); }
            if (PopupOpen) { PaintPopup(c, f, input); }
            if (input.Released) {
                pointerOwner = null;
                captionWindow = null;
                startPressed = false;
            }
        }

        private void Route(in UiInput input, out bool overShell) {
            overShell = false;
            float x = input.X;
            float y = input.Y;
            if (PopupOpen) {
                bool insidePopup = popupFresh || popupBox.Contains(x, y);
                if (input.Pressed && !insidePopup) {
                    ClosePopup();
                    return;
                }
                if (PopupOpen) { return; }
            }
            if (StartOpen) {
                bool inside = StartMenuBox.Contains(x, y) || StartButtonBox.Contains(x, y);
                if (input.Pressed && !inside) {
                    StartOpen = false;
                    return;
                }
                if (StartMenuBox.Contains(x, y)) { return; }
            }
            if (TaskbarBox.Contains(x, y)) {
                overShell = true;
                if (input.Pressed && StartButtonBox.Contains(x, y)) { startPressed = true; }
                return;
            }
            if (input.Pressed) {
                if (Desk.Press(x, y, out XpWindow w, out WinPart part)) {
                    if (part == WinPart.Client) {
                        pointerOwner = w;
                    } else if (part == WinPart.Close || part == WinPart.Max || part == WinPart.Min) {
                        captionWindow = w;
                        captionPart = part;
                    } else if (part == WinPart.Title && input.Now - lastClickTime < DoubleClick && lastClickId == -100 - w.Id) {
                        Desk.ToggleMax(w);
                        Desk.Release();
                    }
                    if (part == WinPart.Title) {
                        lastClickId = -100 - w.Id;
                        lastClickTime = input.Now;
                    }
                    return;
                }
                overShell = true;
                selectedIcon = -1;
                return;
            }
            if (input.Down && Desk.Captured != null) {
                Desk.Drag(x, y);
                return;
            }
            if (input.Released) {
                Desk.Release();
                if (captionWindow != null) {
                    XpWindow hit = Desk.Hit(x, y, out WinPart part);
                    if (ReferenceEquals(hit, captionWindow) && part == captionPart) {
                        switch (part) {
                            case WinPart.Close: RequestClose(hit); break;
                            case WinPart.Max: Desk.ToggleMax(hit); break;
                            case WinPart.Min: Desk.Minimize(hit); break;
                        }
                    }
                }
            }
            if (pointerOwner == null && Desk.Hit(x, y, out _) == null) { overShell = true; }
        }

        private UiInput InputFor(XpWindow w, in UiInput input) {
            if (PopupOpen || StartOpen && StartMenuBox.Contains(input.X, input.Y)) { return UiInput.None; }
            if (pointerOwner != null) { return ReferenceEquals(pointerOwner, w) ? input : UiInput.None; }
            if (Desk.Captured != null || captionWindow != null) { return UiInput.None; }
            XpWindow hit = Desk.Hit(input.X, input.Y, out WinPart part);
            if (!ReferenceEquals(hit, w) || part != WinPart.Client || Desk.Blocked(w)) { return UiInput.None; }
            return input;
        }

        private void PaintWindow(ICanvas c, XpFaces f, XpWindow w, in UiInput input) {
            Box frame = Desk.Frame(w);
            if (w.Visual?.Alive == true) { motionCanvas.Inner = c; motionCanvas.Alpha = w.Visual.Opacity.Value; c = motionCanvas; }
            bool active = ReferenceEquals(Desk.Focused, w);
            int hot = -1;
            XpWindow hit = Desk.Hit(input.X, input.Y, out WinPart part);
            if (ReferenceEquals(hit, w)) {
                hot = part switch { WinPart.Close => 0, WinPart.Max => 1, WinPart.Min => w.Resizable ? 2 : 1, _ => -1 };
            }
            bool pressed = ReferenceEquals(captionWindow, w) && input.Down;
            XpPaint.Window(c, f, frame, w.Title, w.App?.Icon, active, w.State == WinState.Maximized, w.Dialog, w.Resizable && !w.Dialog, w.Minimizable && !w.Dialog, hot, pressed);
            Box client = XpDesk.Client(frame);
            w.Ui.Begin(InputFor(w, input));
            c.PushClip(client);
            w.App?.Paint(c, w.Ui, f, client, this, w);
            c.PopClip();
        }

        public bool Clicked(int id, out bool twice) {
            twice = false;
            if (!In.Released) { return false; }
            twice = lastClickId == id && In.Now - lastClickTime < DoubleClick;
            lastClickId = id;
            lastClickTime = In.Now;
            return true;
        }

        private void Desktop(ICanvas c, XpFaces f) {
            float x = 6;
            float y = 6;
            for (int i = 0; i < Icons.Count; i++) {
                XpShortcut s = Icons[i];
                var cell = new Box(x, y, XpLook.IconCell, XpLook.IconCellH);
                var icon = new Box(x + (XpLook.IconCell - 32) * 0.5f, y + 4, 32, 32);
                bool sel = selectedIcon == i;
                c.Image(XpLook.Image(s.Large ?? s.Icon), icon, sel ? new Rgba(170, 190, 240) : XpLook.White);
                var label = new Box(x + 2, y + 40, XpLook.IconCell - 4, 26);
                IconLabel(c, f, s, label, sel);
                if (Ui.Click(ShellId + i, cell)) {
                    selectedIcon = i;
                    if (Clicked(ShellId + i, out bool twice) && twice) { Launch(s.App, s.Arg); }
                }
                y += XpLook.IconCellH + 4;
                if (y + XpLook.IconCellH > Desk.Work.Bottom) {
                    y = 6;
                    x += XpLook.IconCell + 4;
                }
            }
        }

        private static void IconLabel(ICanvas c, XpFaces f, XpShortcut shortcut, in Box label, bool sel) {
            shortcut.LayoutLabel(f.Body, label.W);
            for (int line = 0; line < 2; line++) {
                string text = line == 0 ? shortcut.FirstLine : shortcut.SecondLine;
                if (text == null) { break; }
                float tw = MathF.Min(label.W, Paint.Measure(f.Body, text) + 4);
                var tb = new Box(MathF.Round(label.CenterX - tw * 0.5f), label.Y + line * 13, MathF.Round(tw), 13);
                if (sel) { c.Fill(tb, XpLook.Select); }
                if (!sel) { Paint.Clipped(c, f.Body, text, tb.Offset(1, 1), XpLook.Black, Align.Center); }
                Paint.Clipped(c, f.Body, text, tb, XpLook.White, Align.Center);
            }
        }

        private void Taskbar(ICanvas c, XpFaces f, in UiInput input) {
            Box bar = TaskbarBox;
            c.Shade(bar, XpLook.Taskbar, true, 1.0f);
            Box start = StartButtonBox;
            bool hot = start.Contains(input.X, input.Y);
            for (int row = 0; row < (int)start.H; row++) {
                float t = row / (start.H - 1);
                float inset = 0;
                if (row < 10) { inset = 10 - MathF.Sqrt(MathF.Max(0, 100 - (10 - row) * (10 - row))); }
                if (row > start.H - 10) { inset = 10 - MathF.Sqrt(MathF.Max(0, 100 - (row - (start.H - 10)) * (row - (start.H - 10)))); }
                Rgba color = (hot || StartOpen ? XpLook.StartButtonHot : XpLook.StartButton).Sample(t);
                if (StartOpen) { color = Rgba.Mix(color, XpLook.Black, 0.15f); }
                c.Fill(new Box(start.X, start.Y + row, start.W - inset, 1), color);
            }
            c.Image(XpLook.Image("start16"), new Box(start.X + 8, start.Y + 7, 16, 16), XpLook.White);
            XpPaint.Shadowed(c, f.Start, "пуск", new Box(start.X + 30, start.Y, 66, start.H - 2), XpLook.White, new Rgba(0, 0, 0, 140));
            if (input.Released && startPressed && start.Contains(input.X, input.Y)) { StartOpen = !StartOpen; }

            float trayW = Paint.Measure(f.Body, Clock) + XpLook.TrayPad * 2 + (Printing() ? 22 : 0) + (Speaking ? 38 : 0);
            var tray = new Box(bar.Right - trayW, bar.Y, trayW, bar.H);
            c.Shade(tray, XpLook.Tray, true, 1.0f);
            c.Fill(new Box(tray.X, tray.Y, 1, tray.H), XpLook.TrayEdge);
            c.Fill(new Box(tray.X + 1, tray.Y, 1, tray.H), XpLook.TrayGlow);
            Paint.Text(c, f.Body, Clock, new Box(tray.Right - XpLook.TrayPad - Paint.Measure(f.Body, Clock), tray.Y, 200, tray.H), XpLook.White, Align.Left);
            if (Speaking) { Paint.Text(c, f.Bold, "МИК", new Box(tray.X + 8, tray.Y, 36, tray.H), new Rgba(150, 255, 150), Align.Left); }
            if (Printing()) {
                c.Image(XpLook.Image("printer16"), new Box(tray.X + (Speaking ? 44 : 8), tray.Y + 7, 16, 16), XpLook.White);
            }

            IReadOnlyList<XpWindow> tasks = Desk.Tasks();
            float left = start.Right + 10;
            float room = tray.X - 10 - left;
            if (tasks.Count == 0) { return; }
            float w = MathF.Min(XpLook.TaskBtnMaxW, MathF.Floor(room / tasks.Count) - 2);
            for (int i = 0; i < tasks.Count; i++) {
                XpWindow t = tasks[i];
                var b = new Box(left + i * (w + 2), bar.Y + XpLook.TaskBtnTop - 1, w, XpLook.TaskBtnH);
                bool focus = ReferenceEquals(Desk.Focused, t) || Desk.Focused != null && ReferenceEquals(Desk.Focused.Owner, t);
                focus &= t.State != WinState.Minimized;
                bool over = Ui.Over(ShellId + 500 + i, b);
                Rgba face = focus ? over ? XpLook.TaskBtnFocusHot : XpLook.TaskBtnFocus : over ? XpLook.TaskBtnHot : XpLook.TaskBtn;
                c.Fill(b.Inset(1, 0, 1, 0), face);
                c.Fill(new Box(b.X, b.Y + 1, 1, b.H - 2), face);
                c.Fill(new Box(b.Right - 1, b.Y + 1, 1, b.H - 2), Rgba.Mix(face, XpLook.Black, 0.3f));
                c.Fill(new Box(b.X + 1, b.Y, b.W - 2, 1), focus ? Rgba.Mix(face, XpLook.Black, 0.3f) : Rgba.Mix(face, XpLook.White, 0.25f));
                if (!string.IsNullOrEmpty(t.App?.Icon)) { c.Image(XpLook.Image(t.App.Icon), new Box(b.X + 6, b.Y + 3, 16, 16), XpLook.White); }
                Paint.Clipped(c, f.Body, t.Title, new Box(b.X + 27, b.Y, b.W - 33, b.H), XpLook.White, Align.Left);
                if (Ui.Click(ShellId + 500 + i, b)) { Desk.ActivateFromTask(t); }
            }
        }

        private bool Printing() {
            if (Pc == null) { return false; }
            for (int i = 0; i < Pc.Jobs.Count; i++) {
                if (Pc.Jobs[i].Mine && (Pc.Jobs[i].State == JobState.Queued || Pc.Jobs[i].State == JobState.Printing)) { return true; }
            }
            return false;
        }

        private void StartMenu(ICanvas c, XpFaces f, in UiInput input) {
            Box m = StartMenuBox;
            UiState ui = startInput;
            ui.Begin(m.Contains(input.X, input.Y) ? input : UiInput.None);
            c.Fill(m.Offset(2, 4), new Rgba(0, 0, 0, 90));
            var header = new Box(m.X, m.Y, m.W, XpLook.StartHeaderH);
            for (int row = 0; row < (int)header.H; row++) {
                float inset = row < 6 ? 6 - MathF.Sqrt(MathF.Max(0, 36 - (6 - row) * (6 - row))) : 0;
                c.Fill(new Box(header.X + inset, header.Y + row, header.W - inset * 2, 1), XpLook.StartHeader.Sample(row / (header.H - 1)));
            }
            c.Fill(new Box(header.X + 4, header.Y + 1, header.W - 8, 1), new Rgba(255, 255, 255, 110));
            c.Image(XpLook.Image("user32"), new Box(header.X + 11, header.Y + 10, 32, 32), XpLook.White);
            Paint.Frame(c, new Box(header.X + 5, header.Y + 4, 44, 44), 1, new Rgba(222, 222, 222, 200));
            XpPaint.Shadowed(c, f.Header, Pc?.User ?? "", new Box(header.X + 56, header.Y, header.W - 60, header.H), XpLook.White, new Rgba(0, 0, 0, 170));
            float bodyY = header.Bottom;
            float bodyH = m.H - XpLook.StartHeaderH - XpLook.StartFooterH;
            var leftCol = new Box(m.X + 2, bodyY, 190, bodyH);
            var rightCol = new Box(leftCol.Right, bodyY, m.W - 4 - 190, bodyH);
            c.Fill(new Box(m.X, bodyY, 2, bodyH), XpLook.StartMenuEdge);
            c.Fill(new Box(m.Right - 2, bodyY, 2, bodyH), XpLook.StartMenuEdge);
            c.Fill(leftCol, XpLook.White);
            c.Fill(rightCol, XpLook.StartRight);
            c.Fill(new Box(rightCol.X, rightCol.Y, 1, rightCol.H), XpLook.StartRightEdge);
            c.Shade(new Box(m.X + 2, bodyY, m.W - 4, 2), StartSeparator, false, 1.0f);
            float y = leftCol.Y + 6;
            for (int i = 0; i < StartLeft.Count; i++) {
                XpShortcut s = StartLeft[i];
                var row = new Box(leftCol.X + 5, y, leftCol.W - 10, XpLook.StartLeftRow);
                bool over = ui.Over(ShellId + 900 + i, row);
                if (over) { c.Fill(row, XpLook.StartMenuHot); }
                c.Image(XpLook.Image(s.Large ?? s.Icon), new Box(row.X + 2, row.Y + 1, 32, 32), XpLook.White);
                Paint.Clipped(c, s.Bold ? f.Bold : f.Body, s.Name, new Box(row.X + 38, row.Y, row.W - 40, row.H), over ? XpLook.White : XpLook.Text, Align.Left);
                if (ui.Click(ShellId + 900 + i, row)) { Launch(s.App, s.Arg); }
                y += XpLook.StartLeftRow + 4;
            }
            y = rightCol.Y + 6;
            for (int i = 0; i < StartRight.Count; i++) {
                XpShortcut s = StartRight[i];
                var row = new Box(rightCol.X + 5, y, rightCol.W - 10, XpLook.StartRightRow);
                bool over = ui.Over(ShellId + 950 + i, row);
                if (over) { c.Fill(row, XpLook.StartMenuHot); }
                c.Image(XpLook.Image(s.Icon), new Box(row.X + 3, row.Y + 5, 16, 16), XpLook.White);
                Paint.Clipped(c, s.Bold ? f.Bold : f.Body, s.Name, new Box(row.X + 26, row.Y, row.W - 28, row.H), over ? XpLook.White : XpLook.StartRightText, Align.Left);
                if (ui.Click(ShellId + 950 + i, row)) { Launch(s.App, s.Arg); }
                y += XpLook.StartRightRow + 4;
            }
            var footer = new Box(m.X, m.Bottom - XpLook.StartFooterH, m.W, XpLook.StartFooterH);
            c.Shade(footer, XpLook.StartFooter, true, 1.0f);
            var sources = new Box(footer.X + 8, footer.Y + 5, 116, 26);
            var leave = new Box(footer.Right - 88, footer.Y + 5, 80, 26);
            if (ui.Over(ShellId + 990, sources)) { c.Fill(sources, new Rgba(60, 80, 210, 128)); }
            if (ui.Over(ShellId + 991, leave)) { c.Fill(leave, new Rgba(60, 80, 210, 128)); }
            Paint.Text(c, f.Body, "Источники", sources, XpLook.White, Align.Center);
            Paint.Text(c, f.Body, "Выйти", leave, XpLook.White, Align.Center);
            if (ui.Click(ShellId + 990, sources)) {
                StartOpen = false;
                Ask(null, "Источники", "Карты допуска: Destroyanad, CC BY-SA 4.0. SCP-логотип: Aelanna, Dr. Mackenzie's Sketchbook, CC BY-SA 3.0.\n" +
                    "Иконки: Tango Desktop Project, Public Domain. Шахматы: Screaming Brain Studios, CC0.\n" +
                    "Бильярдный стол: BrightRetro, CC BY 3.0. Шары и кий: Chequered Ink. Принтер: MrEliptik, CC0.\n" +
                    "SCP: Containment Breach: Undertow Games и участники, CC BY-SA 3.0. Оконные детали: XP.css, MIT.\n" +
                    "Ссылки, условия и изменения: Content/LICENSES.txt и Content/asset-sources.json.", new[] { "OK" }, null);
            }
            if (ui.Click(ShellId + 991, leave)) {
                StartOpen = false;
                ExitRequested = true;
            }
        }

        private void PaintPopup(ICanvas c, XpFaces f, in UiInput input) {
            UiState ui = popupInput;
            ui.Begin(input);
            popupBox = XpPaint.PopupBox(f, popupX, popupY, popupItems, popupKeys);
            if (popupBox.Bottom > XpLook.ScreenH - XpLook.TaskbarH) { popupY = MathF.Max(0, popupY - popupBox.H); popupBox = XpPaint.PopupBox(f, popupX, popupY, popupItems, popupKeys); }
            if (popupBox.Right > XpLook.ScreenW) { popupX = XpLook.ScreenW - popupBox.W; popupBox = XpPaint.PopupBox(f, popupX, popupY, popupItems, popupKeys); }
            int pick = XpPaint.Popup(c, ui, f, ShellId + 2000, popupX, popupY, popupItems, popupEnabled, popupKeys);
            if (input.Released && !popupFresh) {
                Action<int> act = popupPick;
                Box pb = popupBox;
                if (pick >= 0) {
                    ClosePopup();
                    act?.Invoke(pick);
                } else if (pb.Contains(input.X, input.Y)) {
                } else {
                    ClosePopup();
                }
            }
            if (input.Released) { popupFresh = false; }
        }
    }

    public interface IXpSized {
        void Size(XpWindow w);
    }

    public interface IXpArg {
        void Argue(XpShell shell, object arg);
    }

    public sealed class XpMessage : IXpApp {
        private readonly string text;
        private readonly string[] buttons;
        private readonly Action<int> pick;
        private readonly string icon;
        private readonly InkLayout layout = new InkLayout();
        private IFace layoutFace;
        private float layoutWidth, scroll;
        private bool answered;

        public XpMessage(string title, string text, string[] buttons, Action<int> pick, string icon) {
            Title = title;
            this.text = text ?? "";
            this.buttons = buttons == null || buttons.Length == 0 ? new[] { "OK" } : buttons;
            this.pick = pick;
            this.icon = icon;
            int lines = 1;
            int longest = 0;
            int run = 0;
            foreach (char ch in this.text) {
                if (ch == '\n') {
                    lines++;
                    run = 0;
                } else {
                    run++;
                    longest = Math.Max(longest, run);
                }
            }
            Width = Math.Clamp(longest * 6 + 110, 260, 520);
            Height = XpLook.TitleH + XpLook.Border + 30 + Math.Max(lines, 2) * 14 + XpLook.ButtonH + 30;
        }

        public float Width { get; }

        public float Height { get; }

        public string Title { get; }

        public string Icon => null;

        internal void Fit(IFace face, XpWindow window, in Box work) {
            float width = MathF.Max(20, window.Rect.W - XpLook.Border * 2 - 28 - (icon != null ? 42 : 0) - XpLook.ScrollW);
            if (ReferenceEquals(face, layoutFace) && width == layoutWidth) { return; }
            layoutFace = face; layoutWidth = width;
            layout.Build(text.AsSpan(), width, new FaceGlyphs(face));
            window.Rect.H = MathF.Min(work.H, MathF.Max(Height, XpLook.TitleH + XpLook.Border + 42 + layout.Lines * face.LineHeight + XpLook.ButtonH));
            window.Rect.Y = Math.Clamp(window.Rect.Y, work.Y, work.Bottom - window.Rect.H);
        }

        public void Paint(ICanvas c, UiState ui, XpFaces f, Box client, XpShell shell, XpWindow self) {
            c.Fill(client, XpLook.Surface);
            float tx = client.X + 14;
            if (icon != null) {
                c.Image(XpLook.Image(icon), new Box(client.X + 12, client.Y + 14, 32, 32), XpLook.White);
                tx = client.X + 56;
            }
            var area = new Box(tx, client.Y + 16, client.Right - tx - 14, client.H - XpLook.ButtonH - 42);
            Box content = XpPaint.Scroll(c, ui, 80, area, layout.Lines * f.Body.LineHeight, ref scroll);
            c.PushClip(content);
            int first = Math.Max(0, (int)(scroll / f.Body.LineHeight)), last = Math.Min(layout.Lines - 1, (int)((scroll + area.H) / f.Body.LineHeight));
            for (int row = first; row <= last; row++) {
                int from = layout.Start(row), end = row + 1 < layout.Lines ? layout.Start(row + 1) : text.Length;
                Ui.Paint.Text(c, f.Body, text.AsSpan(from, end - from).TrimEnd(), content.X, content.Y + row * f.Body.LineHeight - scroll, XpLook.Text);
            }
            c.PopClip();
            float total = buttons.Length * XpLook.ButtonW + (buttons.Length - 1) * 6;
            float x = MathF.Round(client.CenterX - total * 0.5f);
            for (int i = 0; i < buttons.Length; i++) {
                var b = new Box(x, client.Bottom - XpLook.ButtonH - 11, XpLook.ButtonW, XpLook.ButtonH);
                if (XpPaint.Button(c, ui, f, 10 + i, b, buttons[i], true, i == 0)) { Answer(shell, self, i); }
                x += XpLook.ButtonW + 6;
            }
        }

        private void Answer(XpShell shell, XpWindow self, int index) {
            if (answered) { return; }
            answered = true;
            shell.Close(self);
            pick?.Invoke(index);
        }

        public bool WantsClose(XpShell shell, XpWindow self) {
            if (!answered) {
                answered = true;
                shell.Close(self);
                pick?.Invoke(buttons.Length - 1);
            }
            return false;
        }

        public void Closed(XpShell shell) { }
    }
}
