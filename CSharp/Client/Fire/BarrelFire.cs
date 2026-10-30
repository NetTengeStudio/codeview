using System;
using System.Collections.Generic;
using System.IO;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Lights;
using Barotrauma.Particles;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;
using ScpItdMod.Weather;

namespace ScpItdMod.Fire {
    internal static class BarrelFire {
        private const string Tag = "barreldeconstructor";
        private const string Kind = "barrel_erc";
        private const float Breath = .14f, LightFloor = .35f, KindleSeconds = 1.4f, SmotherSeconds = .7f, RimShare = .84f;
        private const float EmberChance = .7f, EmberBrightness = 5f, SmokeOpacity = .5f, MaximumTilt = .9f;
        private const int EmberSlots = 64, SmokeSlots = 18;
        private const float CullMargin = 400;
        private static readonly Identifier[] Replaced = { "flame_barrel".ToIdentifier(), "DarkSmoke".ToIdentifier() };

        private sealed class Burn {
            internal Item Item;
            internal float BaseRange, Vigour, Seed;
            internal double Offset;
        }
        private static readonly Dictionary<Item, Burn> burning = new Dictionary<Item, Burn>();
        private static readonly List<Burn> drawn = new List<Burn>();
        private static readonly List<Item> stale = new List<Item>();
        private static FlameRenderer renderer;
        private static bool broken, smoked, glowed;
        private static int watch = -1;
        private static double previous = double.NaN;

        internal static void Step() {
            if (watch < 0) { watch = Tagged.Watch(Tag.ToIdentifier()); }
            IReadOnlyList<Item> items = Tagged.Of(watch);
            double time = Timing.TotalTime;
            float dt = double.IsNaN(previous) ? 0 : (float)Math.Clamp(time - previous, 0, .1);
            previous = time;
            for (int i = 0; i < items.Count; i++) {
                Item item = items[i];
                if (item.Removed || item.Prefab.Identifier != Kind.ToIdentifier()) { continue; }
                LightSource light = item.GetComponent<LightComponent>()?.Light;
                if (light == null) { continue; }
                if (!burning.TryGetValue(item, out Burn burn)) {
                    Suppress(item);
                    uint id = WeatherNoise.Hash((uint)item.ID * 2654435761u);
                    burn = new Burn { Item = item, BaseRange = light.Range, Seed = (id & 0xffff) / 65536f * 40f, Offset = (id >> 16) / 65536.0 * 20 };
                    burning[item] = burn;
                }
                bool lit = light.Enabled && !item.InWater && item.Condition > 0;
                float rate = lit ? KindleSeconds : SmotherSeconds;
                burn.Vigour += ((lit ? 1 : 0) - burn.Vigour) * (1 - MathF.Exp(-dt / rate));
                if (burn.Vigour < .004f) { burn.Vigour = 0; }
                if (!lit && burn.Vigour <= 0) { continue; }
                float diameter = item.Rect.Width / 100f;
                float flicker = FlameModel.Flicker(WeatherNoise.Hash((uint)item.ID), time + burn.Offset, FlameModel.PuffHertz(diameter));
                light.Range = burn.BaseRange * (LightFloor + (1 - LightFloor) * burn.Vigour) * (1 + Breath * flicker);
                if (light.Range < 1) { light.Range = 1; }
            }
            Forget();
        }

        private static void Suppress(Item item) {
            foreach (ItemComponent component in item.Components) {
                if (component.statusEffectLists == null) { continue; }
                foreach (List<StatusEffect> effects in component.statusEffectLists.Values) {
                    for (int i = 0; i < effects.Count; i++) {
                        effects[i].particleEmitters?.RemoveAll(e => Array.IndexOf(Replaced, e.Prefab.ParticlePrefabName) >= 0);
                    }
                }
            }
        }

        internal static void BeginFrame() { smoked = false; glowed = false; }

        internal static void DrawSmoke(SpriteBatch batch) {
            if (smoked || !Collect()) { return; }
            smoked = true;
            Camera cam = GameMain.GameScreen.Cam;
            batch.End();
            try {
                for (int i = 0; i < drawn.Count; i++) { FlameFrame f = Frame(drawn[i], cam); renderer.DrawSmoke(f, cam.Transform); }
            } finally { batch.Begin(SpriteSortMode.BackToFront, BlendState.NonPremultiplied, null, DepthStencilState.None, null, null, cam.Transform); }
        }

        internal static void DrawGlow(SpriteBatch batch) {
            if (glowed || !Collect()) { return; }
            glowed = true;
            Camera cam = GameMain.GameScreen.Cam;
            batch.End();
            try {
                for (int i = 0; i < drawn.Count; i++) { FlameFrame f = Frame(drawn[i], cam); renderer.DrawGlow(f, cam.Transform); }
            } finally { batch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.LinearWrap, DepthStencilState.None, transformMatrix: cam.Transform); }
        }

        private static bool Collect() {
            drawn.Clear();
            if (broken || burning.Count == 0 || GameMain.GameScreen?.Cam == null) { return false; }
            Rectangle view = GameMain.GameScreen.Cam.WorldView;
            foreach (Burn burn in burning.Values) {
                if (burn.Vigour <= 0 || burn.Item.Removed) { continue; }
                Vector2 p = burn.Item.DrawPosition;
                if (p.X < view.X - CullMargin || p.X > view.Right + CullMargin || p.Y > view.Y + CullMargin || p.Y < view.Y - view.Height - CullMargin) { continue; }
                drawn.Add(burn);
            }
            if (drawn.Count == 0) { return false; }
            if (renderer == null) {
                try {
                    string profile = OperatingSystem.IsWindows() ? "dx11" : "opengl";
                    renderer = new FlameRenderer(GameMain.Instance.GraphicsDevice, File.ReadAllBytes(ModAssets.Resolve("Shaders/fire_" + profile + ".mgfxo")));
                } catch (Exception e) { broken = true; ModLog.Error("Barrel fire renderer disabled: " + e.Message); return false; }
            }
            return true;
        }

        private static FlameFrame Frame(Burn burn, Camera cam) {
            Item item = burn.Item;
            float tilt = Math.Clamp(item.body?.Rotation ?? 0, -MaximumTilt, MaximumTilt);
            Vector2 mouth = item.DrawPosition + new Vector2(-MathF.Sin(tilt), MathF.Cos(tilt)) * item.Rect.Height * .5f;
            float diameter = item.Rect.Width / 100f;
            float pixelsPerMetre = MathF.Max(1, cam.WorldToScreen(new Vector2(100, 0)).X - cam.WorldToScreen(Vector2.Zero).X);
            Vec2 wind = WeatherClient.WindAt(mouth);
            float vigour = burn.Vigour;
            return new FlameFrame {
                Mouth = new Vector2(mouth.X, -mouth.Y), Radius = diameter * .5f * RimShare, Length = FlameModel.Length(diameter, FlameModel.FuelQStar),
                Clock = (float)((Timing.TotalTime + burn.Offset) % FlameModel.ClockWrap), PuffHertz = FlameModel.PuffHertz(diameter), Vigour = vigour,
                MetresPerPixel = 1 / pixelsPerMetre, Seed = burn.Seed, Wind = new Vector2(wind.X, wind.Y),
                EmberChance = EmberChance, EmberBrightness = EmberBrightness, EmberSlots = (int)(EmberSlots * vigour), SmokeOpacity = SmokeOpacity, SmokeSlots = SmokeSlots
            };
        }

        internal static void Release() {
            burning.Clear(); drawn.Clear();
            renderer?.Dispose(); renderer = null; broken = false; smoked = glowed = false;
            previous = double.NaN;
            watch = -1;
        }

        private static void Forget() {
            stale.Clear();
            foreach (KeyValuePair<Item, Burn> pair in burning) {
                if (pair.Key.Removed) { stale.Add(pair.Key); }
            }
            for (int i = 0; i < stale.Count; i++) { burning.Remove(stale[i]); }
        }
    }
}
