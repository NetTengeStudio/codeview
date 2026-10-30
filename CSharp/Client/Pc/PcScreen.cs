using ScpItdMod.Chess;
using ScpItdMod.Durak;
using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ScpItdMod.Core;
using ScpItdMod.Paper;
using ScpItdMod.Rec;
using ScpItdMod.Ui;
using ScpItdMod.Xp;
using ScpItdMod.Meteo;
using ScpItdMod.Rendering;

namespace ScpItdMod.Pc {
    public static class PcScreen {
        private static readonly PcSession session = new PcSession();
        private static readonly DurakSession cards = new DurakSession();
        private static readonly Chess.ChessSession chess = new Chess.ChessSession();
        private static readonly BatchCanvas batch = new BatchCanvas();
        private static readonly ScaledCanvas canvas = new ScaledCanvas();
        private static readonly Pen pen = new Pen(null) { Suppress = PushToTalkHeld };
        private static GUICustomComponent host;
        private static XpShell shell;
        private static readonly DesktopStore desktops = new DesktopStore();
        private static Character operatorCharacter;
        private static XpFaces faces;
        private static int facesGeneration = -1;
        private static readonly Identifier TerminalTag = PcWire.TerminalTag.ToIdentifier();
        private static Item computer;
        private static Item leaving;
        private static readonly ClickLatch clicks = new ClickLatch();
        private static double nextHello;
        private static double opened;
        private static double escapedAt = double.NegativeInfinity;
        private static int clockMinute = -1;
        private static PcApp shownApp = (PcApp)255;
        private static ushort shownCamera;
        private static double nextPresence;
        private static int frames;
        private static long ticks;
        private static RenderSurface desktopSurface;
        private const double DesktopHeartbeat = 0.05;
        private static bool desktopPainted;
        private static double nextDesktopRepaint;
        private static float lastInputX, lastInputY;
        private static RenderTarget2D desktop => desktopSurface?.Target;
        private static SpriteBatch renderer;

        public static bool Open => computer != null;
        internal static XpShell Desktop => shell;
        internal static DesktopIdentity Identity => session.Identity;
        internal static int RetainedDesktops => desktops.Count;

        public static void Attach(Harmony harmony) {
            ModNet.ReceiveBytes(PcWire.Channel, PcLimits.MaxReply, OnReply);
            session.Send = bytes => ModNet.SendBytes(PcWire.Channel, bytes.Span, DeliveryMethod.Reliable);
            ModNet.ReceiveBytes(DurakWire.Channel, DurakWire.MaxReply, bytes => cards.Apply(bytes));
            cards.Send = SendCards;
            ModNet.ReceiveBytes(Chess.ChessWire.Channel, Chess.ChessWire.MaxReply, bytes => chess.Apply(bytes));
            chess.Send = bytes => ModNet.SendBytes(Chess.ChessWire.Channel, bytes, DeliveryMethod.Reliable);
            pen.Command = c => shell != null && shell.FocusedApp is IXpTyping t && t.Command(shell, c);
            harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.ControlLocalPlayer), new[] { typeof(float), typeof(Camera), typeof(bool) }),
                prefix: ModuleHost.Bind(typeof(PcScreen), nameof(ControlPrefix)),
                postfix: ModuleHost.Bind(typeof(PcScreen), nameof(ControlPostfix)));
            harmony.Patch(AccessTools.Method(typeof(GameScreen), nameof(GameScreen.AddToGUIUpdateList), Type.EmptyTypes),
                postfix: ModuleHost.Bind(typeof(PcScreen), nameof(GuiPostfix)));
            harmony.Patch(AccessTools.Method(typeof(GUI), nameof(GUI.TogglePauseMenu), Type.EmptyTypes),
                prefix: ModuleHost.Bind(typeof(PcScreen), nameof(PausePrefix)));
            harmony.Patch(AccessTools.Method(typeof(GUI), "UpdateMouseCursorState", new[] { typeof(GUIComponent) }),
                postfix: ModuleHost.Bind(typeof(PcScreen), nameof(CursorPostfix)));
            harmony.Patch(AccessTools.Method(typeof(GameMain), "Draw", new[] { typeof(GameTime) }),
                prefix: ModuleHost.Bind(typeof(PcScreen), nameof(Prepare)));
            PcMonitors.Attach(harmony);
            ChessTables.Attach(chess);
            harmony.Patch(AccessTools.Method(typeof(Item), nameof(Item.Draw), new[] { typeof(SpriteBatch), typeof(bool), typeof(bool), typeof(Color?), typeof(float?) }),
                postfix: ModuleHost.Bind(typeof(PcScreen), nameof(DrawItemPostfix)));
            ModCommands.Register("scpitd_pcstats", "client: computer screen frame cost", _ =>
                ModLog.Message($"computer screen: {(frames == 0 ? 0 : ticks * 1e6 / System.Diagnostics.Stopwatch.Frequency / frames):F1} us per frame over {frames} frames"));
        }

        public static void Release() { Reset(); desktops.Clear(); }

        public static void Reset() {
            Close(false);
            ChessTables.Release();
            PrinterDraw.Clear();
            leaving = null;
            escapedAt = double.NegativeInfinity;
            PcMonitors.Release();
            desktopSurface?.Dispose();
            desktopSurface = null;
            desktopPainted = false;
            renderer?.Dispose();
            renderer = null;
            batch.Release();
            faces = null;
        }

        private static void OnReply(byte[] payload) {
            if (computer == null) { return; }
            session.Apply(payload);
        }

        private static void SendCards(ReadOnlySpan<byte> bytes) => ModNet.SendBytes(DurakWire.Channel, bytes, DeliveryMethod.Reliable);

        public static void ControlPrefix(Character __instance) {
            if (ReferenceEquals(__instance, Character.Controlled) && (computer != null || ChessTables.Open)) { Character.DisableControls = true; }
        }

        public static void CursorPostfix(GUIComponent c, ref CursorState __result) {
            if (computer != null && ReferenceEquals(c, host)) { __result = CursorState.Default; }
        }

        public static void ControlPostfix(Character __instance) {
            if (!ReferenceEquals(__instance, Character.Controlled)) { return; }
            if (leaving != null) {
                if (ReferenceEquals(__instance.SelectedItem, leaving)) {
                    __instance.EmulateInput(InputType.Deselect);
                    __instance.SelectedItem = null;
                }
                leaving = null;
            }
            Item selected = __instance.SelectedItem;
            if (computer == null && PcWorld.Available(__instance, selected)) {
                Begin(selected);
            } else if (computer != null && (!PcWorld.Available(__instance, computer) || selected != null && !ReferenceEquals(selected, computer))) {
                Close(false);
            }
            if (computer != null) {
                __instance.ClearInput(InputType.Use);
                __instance.ClearInput(InputType.Shoot);
                __instance.ClearInput(InputType.Aim);
            }
            ChessTables.Control(__instance);
        }

        public static bool PausePrefix() {
            if (ChessTables.Escape()) { return false; }
            if (escapedAt == Timing.TotalTime) { return false; }
            if (computer == null) { return true; }
            Escape();
            return false;
        }

        private static void Escape() {
            if (escapedAt == Timing.TotalTime) { return; }
            escapedAt = Timing.TotalTime;
            if (shell == null || !shell.Escape()) { Close(true); }
        }

        private static void Begin(Item item) {
            Close(false);
            if (GameMain.Client == null) {
                RpClientSay("Компьютеры работают только в сетевой игре.");
                leaving = item;
                return;
            }
            computer = item; operatorCharacter = Character.Controlled;
            leaving = null;
            clicks.Clear();
            opened = Timing.TotalTime;
            clockMinute = -1;
            nextHello = Timing.TotalTime + 3.0;
            session.Begin(item.ID, Timing.TotalTime);
            host = new GUICustomComponent(new RectTransform(new Point(GameMain.GraphicsWidth, GameMain.GraphicsHeight), GUI.Canvas), (sb, _) => Draw(sb), (dt, _) => Update(dt)) { CanBeFocused = true, AlwaysOverrideCursor = true, HoverCursor = CursorState.Default };

        }

        private static XpShell CreateDesktop() {
            XpShell created = XpSetup.Create(session, new CamFeed(computer.ID), cards, chess);
            created.PasteText = () => Clipboard.GetText(); created.CopyText = text => Clipboard.SetText(text);
            return created;
        }

        private static void RpClientSay(string text) => Rp.RpClient.Say(text, Color.LightGray);

        public static void Close(bool deselect) {
            if (computer == null) { return; }
            desktopPainted = false;
            XpShell parked = shell;
            parked?.Suspend();
            CamClient.Close();
            session.End();
            cards.End();
            chess.End();
            if (ReferenceEquals(GUI.KeyboardDispatcher.Subscriber, pen)) {
                GUI.KeyboardDispatcher.Subscriber = null;
                TextInput.StopTextInput();
            }
            pen.Target = null;
            host?.RectTransform.Parent?.GUIComponent?.RemoveChild(host);
            host = null;
            Item was = computer;
            computer = null;
            shell = null;
            operatorCharacter = null;
            shownApp = (PcApp)255;
            clicks.Clear();
            if (deselect && Character.Controlled != null && ReferenceEquals(Character.Controlled.SelectedItem, was)) { leaving = was; }
        }

        public static void GuiPostfix() {
            ChessTables.Gui();
            if (host == null) { return; }
            host.RectTransform.AbsoluteOffset = Point.Zero;
            host.RectTransform.NonScaledSize = new Point(GameMain.GraphicsWidth, GameMain.GraphicsHeight);
            host.AddToGUIUpdateList(order: 2);
        }

        public static void DrawItemPostfix(Item __instance, SpriteBatch spriteBatch, bool editing, float? overrideDepth) {
            if (!__instance.Removed && __instance.Visible && (!editing || Item.ShowItems) && SubEditorScreen.IsLayerVisible(__instance)) {
                ChessTables.DrawItem(__instance, spriteBatch, overrideDepth);
                PrinterDraw.Draw(__instance, spriteBatch, overrideDepth);
            }
        }

        internal static void Layout(out float k, out float ox, out float oy) {
            float sx = GameMain.GraphicsWidth / (float)XpLook.ScreenW;
            float sy = GameMain.GraphicsHeight / (float)XpLook.ScreenH;
            k = MathF.Min(sx, sy);
            if (k >= 1.0f) { k = MathF.Floor(k); }
            ox = MathF.Round((GameMain.GraphicsWidth - XpLook.ScreenW * k) * 0.5f);
            oy = MathF.Round((GameMain.GraphicsHeight - XpLook.ScreenH * k) * 0.5f);
        }

        private static bool PushToTalkHeld() {
            if (PlayerInput.KeyDown(InputType.LocalVoice) || PlayerInput.KeyDown(InputType.RadioVoice)) { return true; }
            return GameSettings.CurrentConfig.Audio.VoiceSetting == VoiceMode.PushToTalk && PlayerInput.KeyDown(InputType.Voice);
        }

        private static void Update(float dt) {
            if (computer == null) { return; }
            if (GameMain.Client == null || !ReferenceEquals(operatorCharacter, Character.Controlled) || !PcWorld.Available(Character.Controlled, computer)) {
                Close(true);
                return;
            }
            session.Tick(Timing.TotalTime);
            if (!session.Ready && Timing.TotalTime >= nextHello) {
                nextHello = Timing.TotalTime + 3;
                if (session.Pending == 0 || session.Fault != PcError.Ok) { session.Hello(); }
            }
            if (shell == null && session.Ready) { shell = desktops.Open(session.Identity, CreateDesktop); shell.Now = Timing.TotalTime; }
            if (shell == null) {
                if (PlayerInput.KeyHit(Keys.Escape)) { Escape(); }
                return;
            }
            VoipCapture voice = VoipCapture.Instance;
            shell.Speaking = voice != null && (DateTime.Now - voice.LastEnqueueAudio).TotalSeconds < 0.35;
            InkEdit edit = shell.FocusedApp is IXpTyping typing ? typing.Edit : null;
            pen.Target = edit;
            if (edit == null || PlayView.Busy || PushToTalkHeld()) { pen.Blur(); }
            else if (GUI.KeyboardDispatcher.Subscriber == null) { pen.Focus(new Rectangle(0, 0, GameMain.GraphicsWidth, GameMain.GraphicsHeight)); }
            if (PlayerInput.KeyHit(Keys.Escape) && (GUI.KeyboardDispatcher.Subscriber == null || ReferenceEquals(GUI.KeyboardDispatcher.Subscriber, pen))) {
                Escape();
                if (computer == null) { return; }
            }
            if (shell.ExitRequested) {
                Close(true);
                return;
            }
            clicks.Feed(PlayerInput.PrimaryMouseButtonDown(), PlayerInput.PrimaryMouseButtonClicked(), PlayerInput.SecondaryMouseButtonClicked(), PlayerInput.ScrollWheelSpeed);
            cards.Tick(Timing.TotalTime);
            chess.Tick(Timing.TotalTime);
            PcApp app = shell.FocusedApp switch {
                DurakApp => PcApp.Durak, ChessApp => PcApp.Chess, Cameras => PcApp.Cameras,
                Printers => PcApp.Printers, Dossiers => PcApp.Dossiers, Notepad => PcApp.Notes,
                Explorer => PcApp.Files, MeteoScreen => PcApp.Meteo, _ => PcApp.Desktop
            };
            ushort camera = CamClient.Selected?.Item ?? 0;
            if (session.Ready && (app != shownApp || (app == PcApp.Cameras && camera != shownCamera) || Timing.TotalTime >= nextPresence)) {
                shownApp = app; shownCamera = camera; nextPresence = Timing.TotalTime + 0.5;
                Playback playback = CamClient.Archive.View;
                session.Present(app, camera, playback.Position, (byte)playback.SpeedIndex, playback.Live, playback.Playing);
            }
            if (CamClient.Open) { CamClient.Tick(dt); }
            int minutes = (int)((GameMain.GameSession?.RoundDuration ?? Timing.TotalTime) / 60.0) + 8 * 60;
            if (minutes != clockMinute) {
                clockMinute = minutes;
                shell.Clock = $"{minutes / 60 % 24}:{minutes % 60:00}";
            }
        }

        private static void Surround(XpFaces f, float k, float ox, float oy) {
            float sw = XpLook.ScreenW * k;
            float sh = XpLook.ScreenH * k;
            float gw = GameMain.GraphicsWidth;
            float gh = GameMain.GraphicsHeight;
            var dim = new Rgba(0, 0, 0, 185);
            batch.Fill(new Box(0, 0, gw, oy), dim);
            batch.Fill(new Box(0, oy + sh, gw, gh - oy - sh), dim);
            batch.Fill(new Box(0, oy, ox, sh), dim);
            batch.Fill(new Box(ox + sw, oy, gw - ox - sw, sh), dim);
            for (int i = 1; i <= 6; i++) {
                var edge = new Rgba(0, 0, 0, 70 - i * 10);
                batch.Fill(new Box(ox - i, oy - i, sw + i * 2, 1), edge);
                batch.Fill(new Box(ox - i, oy + sh + i - 1, sw + i * 2, 1), edge);
                batch.Fill(new Box(ox - i, oy - i + 1, 1, sh + i * 2 - 2), edge);
                batch.Fill(new Box(ox + sw + i - 1, oy - i + 1, 1, sh + i * 2 - 2), edge);
            }
            if (oy < 22 * k) { return; }
            Ui.Paint.Text(canvas, f.Body, "Esc — отойти от компьютера", new Box(0, XpLook.ScreenH + 6, XpLook.ScreenW, 16), new Rgba(200, 200, 200, 190), Align.Center);
        }

        internal static XpFaces Faces() {
            int generation = FontStore.Generation;
            if (faces != null && facesGeneration == generation) { return faces; }
            facesGeneration = generation;
            GameFace body = Ui.Faces.System("tahoma.ttf", Ui.Faces.Body, 11);
            GameFace bold = Ui.Faces.System("tahomabd.ttf", Ui.Faces.Body, 11);
            GameFace title = Ui.Faces.System("trebucbd.ttf", Ui.Faces.Body, 13);
            GameFace start = Ui.Faces.System("framdit.ttf", Ui.Faces.Body, 19);
            GameFace header = Ui.Faces.System("tahomabd.ttf", Ui.Faces.Body, 14);
            if (body == null || bold == null || title == null || start == null || header == null) { return null; }
            faces = new XpFaces {
                Body = body, Bold = bold, Title = title, Start = start, Header = header
            };
            return faces;
        }

        public static void Prepare() {
            using var span = FrameProfiler.Section(ProfileSection.Computer);
            if (computer != null && (computer.Removed || !ReferenceEquals(Entity.FindEntityByID(computer.ID), computer))) { Close(false); }
            if (Screen.Selected != GameMain.GameScreen || GameMain.GameSession == null) {
                if (computer != null) { Close(false); }
                PcMonitors.Hide();
                return;
            }
            GraphicsDevice device = GameMain.Instance.GraphicsDevice;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            using (GraphicsPass.Capture(device)) {
                ScreenCameras.Prepare(device);
                if (computer != null && shell != null) {
                    if (desktop == null || desktop.IsDisposed || desktop.IsContentLost || !ReferenceEquals(desktop.GraphicsDevice, device)) {
                        desktopSurface?.Dispose();
                        desktopSurface = new RenderSurface(device, XpLook.ScreenW, XpLook.ScreenH, RenderSurface.Desktops);
                        desktopPainted = false;
                    }
                    Layout(out float k, out float ox, out float oy);
                    var input = new UiInput { X = -1e6f, Y = -1e6f, Now = Motion.MotionClient.InputTime };
                    clicks.Take(PlayerInput.PrimaryMouseButtonHeld(), ref input);
                    if (session.Ready && ReferenceEquals(GUI.MouseOn, host)) {
                        Vector2 m = PlayerInput.MousePosition;
                        input.X = (m.X - ox) / k;
                        input.Y = (m.Y - oy) / k;
                    } else {
                        input = new UiInput { X = -1e6f, Y = -1e6f, Now = Motion.MotionClient.InputTime };
                    }
                    // An idle desktop is not laid out and drawn every frame. It repaints when input or motion can change it,
                    // on a fresh surface, and on a heartbeat that picks up content changing on its own (clock, caret).
                    bool interacted = input.Pressed || input.Released || input.Down || input.AltPressed || input.Wheel != 0 || input.Up || input.DownKey || input.Enter || input.Back || input.X != lastInputX || input.Y != lastInputY;
                    lastInputX = input.X; lastInputY = input.Y;
                    double now = Timing.TotalTime;
                    if (!desktopPainted || interacted || Motion.MotionClient.Runtime.Animating || now >= nextDesktopRepaint) {
                        RenderDesktop(shell, desktop, input);
                        desktopPainted = true;
                        nextDesktopRepaint = now + DesktopHeartbeat;
                    }
                }
                ChessTables.Sweep();
                PrinterDraw.Sweep();
                PcMonitors.Frame();
            }
            ticks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
            frames++;
        }

        internal static bool RenderDesktop(XpShell content, RenderTarget2D target, in UiInput input) {
            XpFaces f = Faces();
            if (f == null) {
                ModLog.Once("computer desktop cannot render: font assets unavailable");
                return false;
            }
            GraphicsDevice device = target.GraphicsDevice;
            SpriteBatches.Ensure(ref renderer, device);
            device.SetRenderTarget(target);
            device.Clear(Color.Black);
            device.ScissorRectangle = target.Bounds;
            batch.BeginTarget(renderer);
            try {
                canvas.Inner = batch;
                canvas.K = 1.0f;
                canvas.Ox = canvas.Oy = 0;
                canvas.PushClip(new Box(0, 0, XpLook.ScreenW, XpLook.ScreenH));
                content.Frame(canvas, f, input, Timing.TotalTime);
                if (ReferenceEquals(content, shell) && !session.Ready && Timing.TotalTime - opened > 0.3) {
                    canvas.Fill(new Box(0, 0, XpLook.ScreenW, XpLook.ScreenH), new Rgba(0, 0, 0, 200));
                    string why = session.Fault != PcError.Ok ? PcWire.Explain(session.Fault) : "Загрузка Windows...";
                    Ui.Paint.Text(canvas, f.Body, why, new Box(0, 0, XpLook.ScreenW, XpLook.ScreenH), XpLook.White, Align.Center);
                }
                MonitorFx.Paint(canvas, new Box(0, 0, XpLook.ScreenW, XpLook.ScreenH));
                canvas.PopClip();
            } finally {
                batch.End();
            }
            return true;
        }

        internal static bool RenderDisplay(DisplayView view, RenderTarget2D target) {
            XpFaces f = Faces();
            if (f == null) {
                ModLog.Once("computer monitor cannot render: font assets unavailable");
                return false;
            }
            GraphicsDevice device = target.GraphicsDevice;
            SpriteBatches.Ensure(ref renderer, device);
            device.SetRenderTarget(target); device.Clear(Color.Black); device.ScissorRectangle = target.Bounds;
            batch.BeginTarget(renderer, smooth: true);
            try {
                canvas.Inner = batch; canvas.K = target.Width / (float)XpLook.ScreenW; canvas.Ox = canvas.Oy = 0;
                DisplayPaint.Draw(canvas, f, view, new Box(0, 0, XpLook.ScreenW, XpLook.ScreenH));
                if (view.Ready && view.State.App == PcApp.Cameras) {
                    var picture = new Box(16, 48, XpLook.ScreenW - 32, XpLook.ScreenH - 64);
                    ScreenCameras.Picture(canvas, picture, view.State.Item);
                    Ui.Paint.Text(canvas, f.Header, ScreenCameras.Status(view.State.Item), picture, XpLook.White, Align.Center);
                }
            } finally { batch.End(); }
            return true;
        }

        private static void Draw(SpriteBatch sb) {
            if (computer == null) { return; }
            if (shell == null) {
                GUI.DrawRectangle(sb, new Rectangle(0, 0, GameMain.GraphicsWidth, GameMain.GraphicsHeight), Color.Black * 0.95f, isFilled: true);
                GUI.DrawString(sb, new Vector2(32, 32), session.Fault == PcError.Ok ? "Загрузка Windows..." : PcWire.Explain(session.Fault), Color.White);
                return;
            }
            if (desktop == null || desktop.IsDisposed) { return; }
            XpFaces f = Faces();
            if (f == null) { return; }
            Layout(out float k, out float ox, out float oy);
            batch.Begin(sb, 1.0f);
            try {
                canvas.Inner = batch;
                canvas.K = k;
                canvas.Ox = ox;
                canvas.Oy = oy;
                Surround(f, k, ox, oy);
                sb.Draw(desktop, new Rectangle((int)ox, (int)oy, (int)(XpLook.ScreenW * k), (int)(XpLook.ScreenH * k)), Color.White);
            } finally {
                batch.End();
            }
        }
    }
}
