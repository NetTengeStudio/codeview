using HarmonyLib;
using Barotrauma;
using Barotrauma.Lights;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;

namespace ScpItdMod.Fire {
    public sealed class BarrelFireModule : IModule {
        public void Attach(Harmony harmony) {
            harmony.Patch(
                AccessTools.Method(typeof(GameScreen), nameof(GameScreen.Update), new[] { typeof(double) }),
                postfix: ModuleHost.Bind(typeof(BarrelFireModule), nameof(UpdatePostfix)));
            harmony.Patch(AccessTools.Method(typeof(GameScreen), nameof(GameScreen.DrawMap)), prefix: ModuleHost.Bind(typeof(BarrelFireModule), nameof(FramePrefix)));
            harmony.Patch(AccessTools.Method(typeof(Submarine), nameof(Submarine.DrawFront)), postfix: ModuleHost.Bind(typeof(BarrelFireModule), nameof(SmokePostfix)));
            harmony.Patch(AccessTools.Method(typeof(LightManager), nameof(LightManager.DebugDrawVertices)), prefix: ModuleHost.Bind(typeof(BarrelFireModule), nameof(GlowPrefix)));
        }

        public void RoundStart() => BarrelFire.Release();

        public void Release() => BarrelFire.Release();

        public static void UpdatePostfix(double __0) => BarrelFire.Step();

        // The smoke is drawn in the scene's own pass, where the light map tints it; the flame and embers after the light map, so they stay bright.
        public static void FramePrefix() => BarrelFire.BeginFrame();

        public static void SmokePostfix(SpriteBatch spriteBatch) => BarrelFire.DrawSmoke(spriteBatch);

        public static void GlowPrefix(SpriteBatch spriteBatch) => BarrelFire.DrawGlow(spriteBatch);
    }
}
