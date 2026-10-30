using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;
using HarmonyLib;
using ScpItdMod.Core;
using ScpItdMod.Pc;
using ScpItdMod.Rendering;

namespace ScpItdMod.Ui {
    internal static class UiDiagnostics {
        private static StreamWriter output;
        private static int remaining, stride, frame;
        private static string destination;
        private static readonly FieldInfo nativeUpdateList = AccessTools.Field(typeof(GUI), "updateList");
        private static readonly List<object> hitEvents = new List<object>(), clipEvents = new List<object>();
        private static readonly Dictionary<UiState, int> inputIds = new Dictionary<UiState, int>(ReferenceEqualityComparer.Instance);
        public static void Attach(Harmony harmony) {
            harmony.Patch(AccessTools.Method(typeof(GameMain), "Draw", new[] { typeof(Microsoft.Xna.Framework.GameTime) }), postfix: ModuleHost.Bind(typeof(UiDiagnostics), nameof(Frame)));
            ModCommands.Register("scpitd_uicapture", "client: capture GUI/render state: [frames 1..600] [stride 1..600]; no game launch", Start);
        }
        private static void Start(string[] args) {
            int count = 120, interval = 1;
            if (args.Length > 2 || args.Length > 0 && (!int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out count) || count < 1 || count > 600) || args.Length > 1 && (!int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out interval) || interval < 1 || interval > 600)) {
                ModLog.Error("Usage: scpitd_uicapture [frames 1..600] [stride 1..600]"); return;
            }
            Stop();
            if (nativeUpdateList == null) { ModLog.Error("GUI.updateList is unavailable; diagnostic export cannot report actual GUI order."); return; }
            string directory = Path.Combine(AppContext.BaseDirectory, "Docs", "SCPITD", "LiveDiagnostics");
            Directory.CreateDirectory(directory);
            destination = Path.Combine(directory, "ui-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture) + ".jsonl");
            output = new StreamWriter(new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
            remaining = count; stride = interval; frame = 0;
            UiState.HitObserver = Hit;
            BatchCanvas.ClipObserver = Clip;
            ModLog.Message("GUI capture started: " + destination);
        }
        public static void Stop() {
            UiState.HitObserver = null; BatchCanvas.ClipObserver = null;
            hitEvents.Clear(); clipEvents.Clear(); inputIds.Clear();
            StreamWriter owned = output; output = null;
            if (owned != null) { owned.Dispose(); ModLog.Message("GUI capture saved: " + destination); }
        }
        public static void Frame() {
            if (output == null || frame++ % stride != 0) { return; }
            try {
                var device = GameMain.Instance.GraphicsDevice;
                PcScreen.Layout(out float pcScale, out float pcX, out float pcY);
                var windows = new List<object>();
                int z = 0;
                foreach (RetroWindow w in RetroWindows.OpenWindows) { windows.Add(new { Kind = w.GetType().Name, Space = "Screen", Input = InputId(w.State), Z = z++, w.Title, w.IsOpen, w.Rect.X, w.Rect.Y, w.Rect.W, w.Rect.H }); }
                if (PcScreen.Desktop != null) {
                    z = 0;
                    foreach (var w in PcScreen.Desktop.Desk.Stack) { Box pose = w.Visual?.Alive == true ? w.Visual.Pose.Bounds : w.Rect; windows.Add(new { Kind = "XP:" + w.App.GetType().Name, Space = "PcDesktop", Input = InputId(w.Ui), Z = z++, w.Title, w.Open, State = w.State.ToString(), Logical = new { w.Rect.X, w.Rect.Y, w.Rect.W, w.Rect.H }, Draw = new { pose.X, pose.Y, pose.W, pose.H } }); }
                }
                var components = new List<object>();
                var order = (List<GUIComponent>)nativeUpdateList.GetValue(null);
                var indices = new Dictionary<GUIComponent, int>(order.Count, ReferenceEqualityComparer.Instance);
                for (int i = 0; i < order.Count; i++) { indices.Add(order[i], i); }
                for (int i = 0; i < order.Count; i++) {
                    GUIComponent component = order[i];
                    components.Add(new { Z = i, component.UpdateOrder, Kind = component.GetType().FullName, component.Visible, component.CanBeFocused,
                        component.ClampMouseRectToParent, Draw = Rect(component.Rect), Hit = Rect(component.MouseRect), Parent = component.Parent != null && indices.TryGetValue(component.Parent, out int parent) ? parent : -1 });
                }
                var characters = new List<object>();
                foreach (Character c in Character.CharacterList) { characters.Add(new { c.ID, c.Enabled, c.Removed, c.InvisibleTimer, c.hudInfoVisible, HiddenByRp = Rp.RpClient.Concealed(c), SimplePhysics = c.AnimController?.SimplePhysicsEnabled, Limbs = c.AnimController?.Limbs?.Length }); }
                output.WriteLine(JsonSerializer.Serialize(new {
                    Utc = DateTime.UtcNow, Monotonic = System.Diagnostics.Stopwatch.GetTimestamp(), Frequency = System.Diagnostics.Stopwatch.Frequency, GameTime = Timing.TotalTime, Frame = frame, Screen = Screen.Selected?.GetType().Name,
                    Width = GameMain.GraphicsWidth, Height = GameMain.GraphicsHeight, Scale = GUI.Scale,
                    Mouse = PlayerInput.MousePosition.ToString(), Hover = GUI.MouseOn?.GetType().Name, HoverBounds = GUI.MouseOn?.Rect.ToString(),
                    Keyboard = GUI.KeyboardDispatcher.Subscriber?.GetType().FullName, GUI.InputBlockingMenuOpen,
                    RenderTargets = device.RenderTargetCount, Viewport = device.Viewport.ToString(), Scissor = device.ScissorRectangle.ToString(),
                    PaperBytes = RenderSurface.Paper.Bytes, CameraBytes = RenderSurface.Cameras.Bytes, DesktopBytes = RenderSurface.Desktops.Bytes,
                    PcScreen.RetainedDesktops,
                    Desktop = new { PcScreen.Identity.Server, PcScreen.Identity.Owner, PcScreen.Identity.Station, PcScreen.Identity.Profile, PcScreen.Identity.Terminal },
                    PcTransform = new { K = pcScale, Ox = pcX, Oy = pcY },
                    MissingModules = ModuleHost.Failures, Windows = windows, Components = components, Slots = CollectSlots(), Characters = characters,
                    HitEvents = hitEvents, ClipEvents = clipEvents,
                    Fonts = FontStore.LoadedPaths, FailedFonts = FontStore.RejectedPaths, Textures = TextureStore.LoadedPaths, FailedTextures = TextureStore.RejectedPaths
                }));
                hitEvents.Clear(); clipEvents.Clear();
                if (--remaining == 0) { Stop(); }
            } catch (Exception error) {
                Stop(); ModLog.Error("GUI diagnostic capture failed: " + error);
            }
        }
        private static int InputId(UiState state) {
            if (!inputIds.TryGetValue(state, out int id)) { id = inputIds.Count + 1; inputIds.Add(state, id); }
            return id;
        }
        private static void Hit(UiState state, int id, Box box, float angle) {
            if (output == null || frame % stride != 0) { return; }
            hitEvents.Add(new { Input = InputId(state), Id = id, box.X, box.Y, box.W, box.H, Angle = angle,
                Mouse = new { state.In.X, state.In.Y }, state.In.Pressed, state.In.Down, state.In.Released, state.Hot, state.Active });
        }
        private static void Clip(BatchCanvas canvas, Rectangle rect, bool target) {
            if (output == null || frame % stride != 0) { return; }
            clipEvents.Add(new { Space = target ? "RenderTarget" : "Screen", Scissor = Rect(rect), canvas.Scale });
        }
        private static object Rect(Rectangle r) => new { r.X, r.Y, W = r.Width, H = r.Height };
        private static List<object> CollectSlots() {
            var slots = new List<object>();
            var pending = new Stack<Inventory>();
            var visited = new HashSet<Inventory>(ReferenceEqualityComparer.Instance);
            if (Character.Controlled?.Inventory != null) { pending.Push(Character.Controlled.Inventory); }
            if (Inventory.SelectedSlot?.ParentInventory != null) { pending.Push(Inventory.SelectedSlot.ParentInventory); }
            while (pending.Count != 0) {
                Inventory inventory = pending.Pop();
                if (!visited.Add(inventory)) { continue; }
                if (inventory.visualSlots != null) {
                    for (int i = 0; i < inventory.visualSlots.Length; i++) {
                        VisualSlot slot = inventory.visualSlots[i]; if (slot == null) { continue; }
                        Rectangle draw = slot.Rect, hit = slot.InteractRect;
                        draw.Offset(slot.DrawOffset.ToPoint()); hit.Offset(slot.DrawOffset.ToPoint());
                        slots.Add(new { Owner = (inventory.Owner as Entity)?.ID, Index = i, Base = Rect(slot.Rect), Draw = Rect(draw), Hit = Rect(hit), slot.Disabled,
                            DrawOffset = new { slot.DrawOffset.X, slot.DrawOffset.Y }, Background = Rect(inventory.BackgroundFrame) });
                    }
                }
                for (int i = 0; i < inventory.Capacity; i++) {
                    foreach (Item item in inventory.GetItemsAt(i)) {
                        if (item == null) { continue; }
                        foreach (ItemContainer container in item.GetComponents<ItemContainer>()) { if (container.Inventory != null) { pending.Push(container.Inventory); } }
                    }
                }
            }
            return slots;
        }
    }
}
