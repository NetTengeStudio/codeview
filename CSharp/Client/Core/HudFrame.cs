using System;
using System.Collections.Generic;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;

namespace ScpItdMod.Core {
    public static class HudFrame {
        public const int Vignette = 0;
        public const int Meters = 10;

        private static readonly List<int> layers = new List<int>();
        private static readonly List<Action<SpriteBatch>> painters = new List<Action<SpriteBatch>>();

        public static void Attach(Harmony harmony) =>
            harmony.Patch(
                AccessTools.Method(typeof(Character), nameof(Character.DrawHUD)),
                postfix: ModuleHost.Bind(typeof(HudFrame), nameof(DrawHudPostfix)));

        public static void Register(int layer, Action<SpriteBatch> paint) {
            if (paint == null) { return; }
            int at = layers.Count;
            while (at > 0 && layers[at - 1] > layer) { at--; }
            layers.Insert(at, layer);
            painters.Insert(at, paint);
        }

        public static void Clear() {
            layers.Clear();
            painters.Clear();
        }

        public static void DrawHudPostfix(Character __instance, SpriteBatch spriteBatch) {
            if (GUI.DisableHUD || !ReferenceEquals(__instance, Character.Controlled)) { return; }
            // Characters that predate this plugin never run the CharacterHealth
            // constructor postfix, so the vanilla corner bar would otherwise stay
            // visible for the whole round. Re-asserting the hidden state here is
            // the draw-boundary guarantee; the visibility setter is also patched.
            CharacterCornerPresentation.Enforce(__instance);
            for (int i = 0; i < painters.Count; i++) {
                try {
                    painters[i](spriteBatch);
                } catch (Exception error) {
                    HudPaint.Rescue(spriteBatch);
                    ModLog.Once($"{painters[i].Method.Name} cannot draw: {error.Message}");
                }
            }
        }
    }
}
