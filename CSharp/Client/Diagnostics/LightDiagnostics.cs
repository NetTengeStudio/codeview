using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Lights;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ScpItdMod.Diagnostics {
    internal static class LightDiagnostics {
        internal static void Capture(string folder) {
            GraphicsDevice graphics = GameMain.Instance.GraphicsDevice;
            if (graphics.RenderTargetCount != 0) throw new InvalidOperationException("The completed frame is not on the backbuffer");
            int width = graphics.PresentationParameters.BackBufferWidth, height = graphics.PresentationParameters.BackBufferHeight;
            var pixels = new Color[checked(width * height)]; graphics.GetBackBufferData(pixels);
            using var texture = new Texture2D(graphics, width, height, false, SurfaceFormat.Color);
            texture.SetData(pixels);
            using FileStream stream = File.Create(Path.Combine(folder, "game-frame.png")); texture.SaveAsPng(stream, width, height);
        }
        internal static void Write(string folder) {
            LightManager manager = GameMain.LightManager;
            var registered = new HashSet<LightSource>(); var active = new HashSet<LightSource>();
            Collect(manager, "lights", registered); Collect(manager, "activeLights", active);
            var rows = new List<object>();
            foreach (Item item in Item.ItemList) foreach (ItemComponent component in item.Components) if (component is LightComponent light) {
                LightSource source = light.Light;
                rows.Add(new { id = item.ID, prefab = item.Prefab.Identifier.ToString(), position = item.WorldPosition, hull = item.CurrentHull?.ID, condition = item.Condition, hidden = item.IsHidden, on = light.IsOn, activeComponent = light.IsActive, watts = light.PowerConsumption, voltage = light.Voltage, minimumVoltage = light.MinVoltage,
                    source = source == null ? null : new { enabled = source.Enabled, colour = source.Color, range = source.Range, brightness = source.CurrentBrightness, shadows = source.CastShadows, background = source.IsBackground, aboveLevel = Level.IsPositionAboveLevel(source.WorldPosition), position = source.Position, world = source.WorldPosition, parentSub = source.ParentSub?.ID, recalculate = source.NeedsRecalculation, registered = registered.Contains(source), selectedForDraw = active.Contains(source) } });
            }
            Camera camera = GameMain.GameScreen?.Cam;
            var graphics = GameMain.Instance.GraphicsDevice;
            File.WriteAllText(Path.Combine(folder, "lights.json"), JsonSerializer.Serialize(new { enabled = manager?.LightingEnabled, visibleLimit = GameSettings.CurrentConfig.Graphics.VisibleLightLimit, lightmapScale = GameSettings.CurrentConfig.Graphics.LightMapScale, registered = registered.Count, selected = active.Count, camera = camera == null ? null : new { camera.Position, camera.WorldView, camera.Zoom, camera.Rotation, camera.Transform }, viewport = graphics?.Viewport, lightMap = manager?.LightMap == null ? null : new { manager.LightMap.Width, manager.LightMap.Height, manager.LightMap.IsContentLost }, metrics = graphics == null ? null : graphics.GetType().GetProperty("Metrics")?.GetValue(graphics), rows, limits = "Snapshot at command time; selected list describes the most recently completed lightmap; IsActive=false may be a valid sleeping static lamp" }, new JsonSerializerOptions { IncludeFields = true, WriteIndented = true }));
        }
        private static void Collect(object manager, string name, HashSet<LightSource> output) {
            if (manager == null) return;
            FieldInfo field = AccessTools.Field(manager.GetType(), name) ?? throw new MissingFieldException(manager.GetType().FullName, name);
            if (field.GetValue(manager) is IEnumerable values) foreach (object value in values) if (value is LightSource light) output.Add(light);
        }
    }
}
