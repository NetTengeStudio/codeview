using System;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;
using ScpItdMod.Motion;
using ScpItdMod.Rendering;

namespace ScpItdMod.Ui {
    public static class StatsPanel {
        private const string Feature = "stats panel";
        private const double Hold = 12;
        private static readonly MotionTextCard card = new MotionTextCard(Feature, new SurfaceBudget(4L << 20), new MotionShutter(1d / 240, 8));

        public static long Bakes => card.Bakes;

        public static long Draws => card.Draws;

        public static void Attach(Harmony harmony) {
            harmony.Patch(AccessTools.Method(typeof(GameMain), "Draw", new[] { typeof(GameTime) }),
                prefix: ModuleHost.Bind(typeof(StatsPanel), nameof(Update)));
            harmony.Patch(AccessTools.Method(typeof(GUI), nameof(GUI.Draw), new[] { typeof(Camera), typeof(SpriteBatch) }),
                postfix: ModuleHost.Bind(typeof(StatsPanel), nameof(Draw)));
        }

        public static void Show(string[] lines) {
            if (Contained.IsDisabled(Feature) || lines == null) { return; }
            try {
                string[] text = new string[lines.Length + 1];
                Array.Copy(lines, text, lines.Length);
                text[lines.Length] = "card: " + card.Describe();
                card.Show(text, CardPlacement.ScreenTopRight, default);
                card.Hold(Hold);
            }
            catch (Exception error) { Contained.Fail(Feature, error); }
        }

        public static void Update() {
            if (Contained.IsDisabled(Feature)) { return; }
            try { card.Update(); }
            catch (Exception error) { Contained.Fail(Feature, error); }
        }

        public static void Draw(SpriteBatch spriteBatch) {
            if (Contained.IsDisabled(Feature)) { return; }
            try { card.Draw(spriteBatch); }
            catch (Exception error) { Contained.Fail(Feature, error); }
        }

        public static void Reset() => card.Reset();
    }
}
