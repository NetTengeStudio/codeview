using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Emit;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ScpItdMod.Weather {
    internal static class WeatherLight {
        private static WeatherGrid grid;
        private static WeatherRect view;
        private static SkyLightRenderer renderer;
        private static bool prepared;
        private static SkyLightEnvironment frameLight;

        internal static void Prepare(WeatherGrid field, in WeatherRect bounds) { prepared = WeatherService.Active; grid = field; view = bounds; }

        internal static bool Halo(Character __0) => !prepared || __0 == null || !WeatherService.InZone(__0.WorldPosition);
        internal static bool IgnoreNativeGapGlow(Gap gap) => gap.IsRoomToRoom || prepared && WeatherService.InZone(gap.WorldPosition);
        internal static void EndFrame() { prepared = false; grid = null; }
        internal static void Suspend() => EndFrame();
        internal static void Release() { Suspend(); renderer?.Dispose(); renderer = null; }

        internal static void BeforeLightMap(GraphicsDevice graphics, Camera cam) {
            if (!prepared || grid == null || !GameMain.LightManager.LightingEnabled || !grid.Bounds.Overlaps(view)) return;
            renderer ??= new SkyLightRenderer(graphics, File.ReadAllBytes(Core.ModAssets.Resolve("Shaders/skylight_" + (OperatingSystem.IsWindows() ? "dx11" : "opengl") + ".mgfxo")));
            frameLight = WeatherLighting.Environment(WeatherService.Current, WeatherService.Sun, WeatherService.OutdoorLight);
            float scale = GameSettings.CurrentConfig.Graphics.LightMapScale;
            renderer.Prepare(grid, cam.Transform * Matrix.CreateScale(scale, scale, 1), WeatherService.UnitsPerMetre, (int)(GameMain.GraphicsWidth * scale), (int)(GameMain.GraphicsHeight * scale), frameLight);
        }

        internal static void Composite(GraphicsDevice graphics, Camera cam) {
            if (!prepared || grid == null || !GameMain.LightManager.LightingEnabled || !grid.Bounds.Overlaps(view)) return;
            if (renderer == null) throw new InvalidOperationException("Sky-light composition requires a prepared renderer");
            float scale = GameSettings.CurrentConfig.Graphics.LightMapScale;
            Matrix transform = cam.Transform * Matrix.CreateScale(scale, scale, 1);
            renderer.Composite(grid, view, transform, frameLight, WeatherService.UnitsPerMetre);
        }

        internal static IEnumerable<CodeInstruction> InsertComposite(IEnumerable<CodeInstruction> instructions) {
            var code = new List<CodeInstruction>(instructions);
            var setTarget = AccessTools.Method(typeof(GraphicsDevice), nameof(GraphicsDevice.SetRenderTarget), new[] { typeof(RenderTarget2D) });
            var roomGap = AccessTools.PropertyGetter(typeof(Gap), nameof(Gap.IsRoomToRoom));
            int gaps = 0;
            for (int i = 0; i < code.Count; i++) if (code[i].Calls(roomGap)) {
                code[i].opcode = OpCodes.Call;
                code[i].operand = AccessTools.Method(typeof(WeatherLight), nameof(IgnoreNativeGapGlow));
                gaps++;
            }
            if (gaps != 1) throw new InvalidOperationException("RenderLightMap gap-glow scope is incompatible.");
            int insertion = -1;
            for (int i = 2; i < code.Count; i++) {
                if (!code[i].Calls(setTarget) || code[i - 1].opcode != OpCodes.Ldnull || code[i - 2].opcode != OpCodes.Ldarg_1) continue;
                if (insertion >= 0) throw new InvalidOperationException("RenderLightMap has multiple target-release sites.");
                insertion = i - 2;
            }
            if (insertion < 0) throw new InvalidOperationException("RenderLightMap target-release site was not found.");
            var entry = new CodeInstruction(OpCodes.Ldarg_1);
            entry.labels.AddRange(code[insertion].labels); code[insertion].labels.Clear();
            entry.blocks.AddRange(code[insertion].blocks); code[insertion].blocks.Clear();
            code.InsertRange(insertion, new[] { entry, new CodeInstruction(OpCodes.Ldarg_3), CodeInstruction.Call(typeof(WeatherLight), nameof(Composite)) });
            return code;
        }
    }
}
