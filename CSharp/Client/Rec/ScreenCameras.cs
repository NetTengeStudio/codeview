using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;
using ScpItdMod.Net;
using ScpItdMod.Rendering;
using ScpItdMod.Ui;

namespace ScpItdMod.Rec {
    public static class ScreenCameras {
        private sealed class View : IDisposable {
            public ushort Terminal, Camera;
            public uint Session, Painted, Revision;
            public ScreenFrame Frame;
            public readonly CameraScene Scene = new CameraScene();
            public readonly CameraOcclusion Occlusion = new CameraOcclusion();
            public RenderSurface Surface;
            public TextureImage Image;
            public double Wanted, RequestAt, RetryAt;
            public bool Stale, Fault;
            public void Dispose() { Occlusion.Dispose(); Surface?.Dispose(); Surface = null; Image = null; }
            public void Changed() { if (++Revision == 0) { Revision++; } }
        }
        private static readonly Dictionary<ushort, View> views = new Dictionary<ushort, View>();
        private static readonly List<ushort> removed = new List<ushort>();
        private static readonly WireWriter writer = new WireWriter(16);
        private static SpriteBatch batch;
        private static uint session = (uint)System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue);
        public static void Attach(Harmony harmony) => harmony.Patch(AccessTools.Method(typeof(GameScreen), nameof(GameScreen.Update)), postfix: ModuleHost.Bind(typeof(ScreenCameras), nameof(Tick)));
        private static View Create(ushort terminal, ushort camera = 0) {
            if (++session == 0) { session++; }
            var view = new View { Terminal = terminal, Camera = camera, Session = session, Frame = new ScreenFrame(terminal, session, camera), Wanted = Timing.TotalTime };
            views.Add(terminal, view); return view;
        }
        public static void Expect(ushort terminal, ushort camera) {
            if (views.TryGetValue(terminal, out View old)) {
                if (old.Camera == camera) { return; }
                old.Dispose(); views.Remove(terminal);
            }
            if (terminal != 0 && views.Count < ScreenFrameWire.MaxViews) { Create(terminal, camera); }
        }
        public static void Want(ushort terminal) {
            if (terminal == 0 || GameMain.Client == null) { return; }
            if (!views.TryGetValue(terminal, out View view)) {
                if (views.Count >= ScreenFrameWire.MaxViews) { return; }
                view = Create(terminal);
            }
            view.Wanted = Timing.TotalTime;
        }
        public static void Tick() {
            CamClient.ConsentTick();
            double now = Timing.TotalTime; removed.Clear();
            foreach (var pair in views) {
                View view = pair.Value;
                if (now - view.Wanted > 2 || !(Entity.FindEntityByID(view.Terminal) is Item item) || item.Removed) { removed.Add(pair.Key); continue; }
                if (now - view.Wanted > 0.5 || now < view.RequestAt) { continue; }
                view.RequestAt = now + 0.65;
                CamWire.Header(writer, CamMsg.ScreenWatch); writer.U32(view.Session); writer.U32(view.Terminal);
                ModNet.SendBytes(CamWire.Channel, writer.Span, DeliveryMethod.Unreliable);
            }
            for (int i = 0; i < removed.Count; i++) { views[removed[i]].Dispose(); views.Remove(removed[i]); }
        }
        public static void Receive(ReadOnlySpan<byte> data) {
            if (data.Length < 4) { return; }
            ushort terminal = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(2));
            if (views.TryGetValue(terminal, out View view) && view.Frame.Apply(data, Timing.TotalTime)) { view.Changed(); }
        }
        public static uint Version(ushort terminal) {
            if (!views.TryGetValue(terminal, out View view)) { return 0; }
            bool stale = view.Frame.Ready && Timing.TotalTime - view.Frame.ReceivedAt > 2;
            if (view.Stale != stale) { view.Stale = stale; view.Changed(); }
            return view.Revision;
        }
        public static bool Observes(Item item) {
            Rectangle bounds = item.WorldRect; double now = Timing.TotalTime;
            foreach (View view in views.Values) {
                if (now - view.Wanted <= 0.5 && view.Frame.Ready && now - view.Frame.ReceivedAt <= 2 &&
                    view.Frame.Meta.Live && view.Frame.Meta.Intersects(bounds.X, bounds.Y, bounds.Width, bounds.Height)) { return true; }
            }
            return false;
        }
        public static void Prepare(GraphicsDevice device) {
            foreach (View view in views.Values) {
                ScreenFrame frame = view.Frame;
                if (Timing.TotalTime - view.Wanted > 0.5 || !frame.Ready || frame.Meta.State != ScreenState.Picture ||
                    !frame.HistoricalSceneReady || Timing.TotalTime - frame.ReceivedAt > 2 || Timing.TotalTime < view.RetryAt) { continue; }
                RenderTarget2D texture = view.Surface?.Target;
                bool lost = texture == null || texture.IsDisposed || texture.IsContentLost || !ReferenceEquals(texture.GraphicsDevice, device);
                if (!lost && view.Painted == frame.Version && !view.Fault) { continue; }
                try {
                    using (GraphicsPass.Capture(device)) {
                        if (lost) {
                            view.Dispose(); view.Surface = new RenderSurface(device, 640, 384, RenderSurface.Cameras);
                            texture = view.Surface.Target; view.Image = new TextureImage(texture);
                        }
                        device.SetRenderTarget(texture); device.ScissorRectangle = texture.Bounds; device.Clear(new Color(12, 15, 17));
                        ScreenMeta meta = frame.Meta;
                        float scale = MathF.Min(texture.Width / (meta.HalfW * 2), texture.Height / (meta.HalfH * 2));
                        Matrix transform = Matrix.CreateTranslation(-meta.X, meta.Y, 0) * Matrix.CreateScale(scale) * Matrix.CreateTranslation(texture.Width / 2f, texture.Height / 2f, 0);
                        SpriteBatches.Ensure(ref batch, device);
                        batch.Begin(SpriteSortMode.BackToFront, BlendState.NonPremultiplied, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone, transformMatrix: transform);
                        try { view.Scene.Draw(batch, frame); } finally { batch.End(); }
                        view.Occlusion.Draw(device,transform,meta.X,meta.Y,meta.HalfW,meta.HalfH,frame.Scene);
                    }
                    view.Painted = frame.Version; view.Fault = false; view.Changed();
                } catch (Exception error) {
                    view.Fault = true; view.RetryAt = Timing.TotalTime + 1; view.Changed();
                    ModLog.Once($"camera terminal {view.Terminal} render failed: {error}");
                }
            }
        }
        public static void Picture(ICanvas canvas, in Box viewport, ushort terminal) {
            Want(terminal); canvas.Fill(viewport, Rgba.Hex(0x0C0F11));
            if (!views.TryGetValue(terminal, out View view) || !view.Frame.Ready || Timing.TotalTime - view.Frame.ReceivedAt > 2 ||
                view.Frame.Meta.State != ScreenState.Picture || !view.Frame.HistoricalSceneReady || view.Fault ||
                view.Image == null || view.Image.Texture.IsDisposed || view.Surface.Target.IsContentLost || view.Painted != view.Frame.Version) { return; }
            if (!CanvasImages.Draw(canvas, view.Image, CanvasImages.Fit(view.Image, viewport), new Rgba(255, 255, 255))) {
                view.Fault = true; view.Changed(); ModLog.Once("camera canvas does not support images");
            }
        }
        public static string Status(ushort terminal) {
            if (!views.TryGetValue(terminal, out View view) || !view.Frame.Ready) { return "Ожидание синхронизации"; }
            if (Timing.TotalTime - view.Frame.ReceivedAt > 2) { return "Нет связи — восстановление"; }
            if (view.Fault) { return "Ошибка отображения — повторная попытка"; }
            if (view.Frame.Meta.State == ScreenState.Picture) {
                if (!view.Frame.HistoricalSceneReady) { return view.Frame.Meta.Live ? "Ожидание окружения камеры" : "Архив окружения недоступен"; }
                if (view.Painted != view.Frame.Version) { return "Подготовка изображения"; }
            }
            return view.Frame.Meta.State switch { ScreenState.NoPower => "Нет питания камеры", ScreenState.NoRights => "Нет допуска к камере", ScreenState.Lost => "Камера недоступна", ScreenState.Gap => "Пробел в записи", ScreenState.Waiting => "Выберите камеру", _ => "" };
        }
        public static void Clear() {
            foreach (View view in views.Values) { view.Dispose(); }
            views.Clear(); removed.Clear(); batch?.Dispose(); batch = null;
        }
    }
}
