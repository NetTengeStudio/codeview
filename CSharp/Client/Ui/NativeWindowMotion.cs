using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;
using ScpItdMod.Motion;
using ScpItdMod.Rendering;

namespace ScpItdMod.Ui {
    // Appearance motion for native windows: item interfaces (charger, wardrobe, cabinets) and the tab menu.
    // A window that becomes visible springs in from a slightly smaller size and fades in. The window is drawn through a
    // transform and an opacity effect inside its own batch pass, so its children keep their colours and the GUI batch
    // state is restored afterwards. A window at rest is drawn exactly as before.
    public static class NativeWindowMotion {
        private sealed class Window {
            public GUIComponent Root;
            public bool Shown, Moving, Exiting;
            public MotionVisual Visual;

            public void Dispose() { Visual?.Dispose(); Visual = null; Moving = false; Exiting = false; }
        }

        // One batch pass around a moving window. Created by the prefix of GUIComponent.DrawAuto, ended by its postfix.
        public readonly struct Pass {
            private readonly SpriteBatch batch;
            private readonly SpriteBatchPause pause;

            private Pass(SpriteBatch batch, SpriteBatchPause pause) { this.batch = batch; this.pause = pause; }

            public static Pass Begin(SpriteBatch batch, Matrix transform, float opacity) {
                var pause = new SpriteBatchPause(batch);
                Effect effect = WindowFade.Get(batch.GraphicsDevice);
                if (effect == null) { pause.Dispose(); return default; }
                WindowFade.SetOpacity(opacity);
                // The rasterizer is the game's own scissor state, so the window keeps the clipping of the frame it is drawn in.
                batch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, GUI.SamplerState, DepthStencilState.None,
                    GameMain.ScissorTestEnable, effect, transform);
                return new Pass(batch, pause);
            }

            public void End() {
                if (batch == null) { return; }
                batch.End();
                pause.Dispose();
            }
        }

        private static readonly List<Window> windows = new List<Window>();
        private static int moving, exiting;
        private const string Feature = "native window motion";

        public static void Attach(Harmony harmony) {
            harmony.Patch(AccessTools.Method(typeof(GameMain), "Draw", new[] { typeof(GameTime) }),
                prefix: ModuleHost.Bind(typeof(NativeWindowMotion), nameof(Update)));
            harmony.Patch(AccessTools.Method(typeof(GUIComponent), nameof(GUIComponent.DrawAuto)),
                prefix: ModuleHost.Bind(typeof(NativeWindowMotion), nameof(DrawPrefix)),
                finalizer: ModuleHost.Bind(typeof(NativeWindowMotion), nameof(DrawFinalizer)));
            harmony.Patch(AccessTools.Method(typeof(GUI), nameof(GUI.Draw), new[] { typeof(Camera), typeof(SpriteBatch) }),
                postfix: ModuleHost.Bind(typeof(NativeWindowMotion), nameof(DrawExiting)));
            // The tab menu's root frame is created with the menu; the item interface root is created with its custom interface.
            harmony.Patch(AccessTools.Constructor(typeof(TabMenu), System.Type.EmptyTypes),
                postfix: ModuleHost.Bind(typeof(NativeWindowMotion), nameof(TabMenuPostfix)));
            harmony.Patch(AccessTools.Method(typeof(CustomInterface), "CreateGUI"),
                postfix: ModuleHost.Bind(typeof(NativeWindowMotion), nameof(CustomInterfacePostfix)));
        }

        public static void Track(GUIComponent root) {
            if (root == null) { return; }
            for (int i = 0; i < windows.Count; i++) { if (ReferenceEquals(windows[i].Root, root)) { return; } }
            windows.Add(new Window { Root = root });
        }

        private static readonly AccessTools.FieldRef<TabMenu, GUIFrame> tabInfoFrame = AccessTools.FieldRefAccess<TabMenu, GUIFrame>("infoFrame");

        public static void TabMenuPostfix(TabMenu __instance) => Track(tabInfoFrame(__instance));

        public static void CustomInterfacePostfix(CustomInterface __instance) => Track(__instance.GuiFrame);

        // Start of each frame, after the simulation has set visibility: starts and ends appearances.
        public static void Update() {
            moving = 0; exiting = 0;
            bool reduced = MotionClient.Runtime.ReducedMotion;
            for (int i = windows.Count - 1; i >= 0; i--) {
                Window w = windows[i];
                // A root removed from the GUI tree has no transform parent any more; its window is retired.
                if (w.Root.RectTransform.Parent == null) { w.Dispose(); windows.RemoveAt(i); continue; }
                bool visible = w.Root.Visible;
                if (visible && (!w.Shown || w.Exiting)) { Appear(w, reduced); }
                else if (!visible && w.Shown && !Retreat(w, reduced)) { w.Dispose(); }
                w.Shown = visible;
                w.Moving = w.Visual?.Alive == true && (w.Visual.Opacity.Running || w.Visual.Scale.Running || w.Visual.Lift.Running);
                if (w.Exiting && (!w.Moving || w.Visual.Opacity.Value < .01f)) { w.Dispose(); }
                if (w.Moving) { moving++; }
                if (w.Exiting) { exiting++; }
            }
        }

        private static bool Retreat(Window w, bool reduced) {
            if (reduced || w.Visual?.Alive != true) { return false; }
            w.Exiting = true;
            w.Visual.Opacity.To(0, MotionPresets.Fade);
            w.Visual.Scale.To(.97f, MotionPresets.Window);
            w.Visual.Lift.To(6, MotionPresets.Window);
            return true;
        }

        private static void Appear(Window w, bool reduced) {
            if (w.Exiting && w.Visual?.Alive == true && !reduced) {
                w.Exiting = false;
                w.Visual.Opacity.To(1, MotionPresets.Fade);
                w.Visual.Scale.To(1, MotionPresets.Window);
                w.Visual.Lift.To(0, MotionPresets.Window);
                return;
            }
            w.Dispose();
            Rectangle r = w.Root.Rect;
            w.Visual = new MotionVisual(MotionClient.Runtime, new Box(r.X, r.Y, r.Width, r.Height), hidden: true);
            if (reduced) {
                w.Visual.Opacity.Snap(1); w.Visual.Scale.Snap(1); w.Visual.Lift.Snap(0);
                return;
            }
            w.Visual.Opacity.Snap(0); w.Visual.Scale.Snap(.94f); w.Visual.Lift.Snap(10);
            w.Visual.Opacity.To(1, MotionPresets.Fade);
            w.Visual.Scale.To(1, MotionPresets.Window);
            w.Visual.Lift.To(0, MotionPresets.Window);
        }

        public static void DrawPrefix(GUIComponent __instance, SpriteBatch spriteBatch, out Pass __state) {
            __state = default;
            if (moving == 0) { return; }
            Window w = FindFor(__instance);
            if (w == null || !w.Moving) { return; }
            Rectangle r = w.Root.Rect;
            float cx = r.X + r.Width * .5f, cy = r.Y + r.Height * .5f;
            float scale = w.Visual.Scale.Value, lift = w.Visual.Lift.Value;
            // Scale about the window centre, then move it by the spring lift.
            Matrix transform = Matrix.CreateTranslation(-cx, -cy, 0) * Matrix.CreateScale(scale, scale, 1) * Matrix.CreateTranslation(cx, cy + lift, 0);
            __state = Pass.Begin(spriteBatch, transform, w.Visual.Opacity.Value);
        }

        public static Exception DrawFinalizer(Pass __state, Exception __exception) {
            __state.End();
            return __exception;
        }

        public static void DrawExiting(SpriteBatch spriteBatch) {
            if (exiting == 0 || Contained.IsDisabled(Feature)) { return; }
            for (int i = 0; i < windows.Count; i++) {
                Window w = windows[i];
                if (!w.Exiting || !w.Moving) { continue; }
                GUIComponent root = w.Root;
                root.Visible = true;
                try { DrawTree(root.RectTransform, root, spriteBatch); }
                catch (Exception error) {
                    Contained.Fail(Feature, error);
                    w.Dispose();
                }
                finally { root.Visible = false; }
            }
        }

        private static void DrawTree(RectTransform node, GUIComponent root, SpriteBatch spriteBatch) {
            GUIComponent component = node.GUIComponent;
            if (component != null && !ReferenceEquals(component, root) && !component.Visible) { return; }
            component?.DrawAuto(spriteBatch);
            foreach (RectTransform child in node.Children) { DrawTree(child, root, spriteBatch); }
        }

        private static Window FindFor(GUIComponent component) {
            for (RectTransform node = component.RectTransform; node != null; node = node.Parent) {
                GUIComponent owner = node.GUIComponent;
                if (owner == null) { continue; }
                Window w = Find(owner);
                if (w != null) { return w; }
            }
            return null;
        }

        private static Window Find(GUIComponent root) {
            for (int i = 0; i < windows.Count; i++) { if (ReferenceEquals(windows[i].Root, root)) { return windows[i]; } }
            return null;
        }

        public static void Reset() {
            for (int i = 0; i < windows.Count; i++) { windows[i].Dispose(); }
            windows.Clear();
            moving = 0; exiting = 0;
            WindowFade.Release();
        }
    }
}
