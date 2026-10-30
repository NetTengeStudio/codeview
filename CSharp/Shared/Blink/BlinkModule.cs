using Barotrauma;
using ScpItdMod.Core;
using HarmonyLib;
#if CLIENT
using Microsoft.Xna.Framework.Graphics;
#else
using Barotrauma.Networking;
#endif

namespace ScpItdMod.Blink {
    public sealed class BlinkModule : IModule {
        private const string HudOverrideName = "alwaysblinkenable";
        private const string HudOverrideHelp =
            "alwaysblinkenable [true/false]: runs the blink mechanic even when nothing is watching you.";

        public void Attach(Harmony harmony) {
            bool kernel = CharacterControlKernel.Available;
            if (!kernel) {
                harmony.Patch(
                    AccessTools.Method(typeof(Character), nameof(Character.Control)),
                    postfix: ModuleHost.Bind(typeof(BlinkModule), nameof(ControlPostfix)));
            }
            ModCommands.Register(HudOverrideName, HudOverrideHelp, HudOverrideExecute);
#if CLIENT
            harmony.Patch(
                AccessTools.Method(typeof(Character), nameof(Character.DrawHUD)),
                prefix: ModuleHost.Bind(typeof(BlinkModule), nameof(DrawHudPrefix)));
            BlinkClient.Hook();
            BlinkHud.Register();
#else
            BlinkServer.Hook();
#endif
            if (kernel) { CharacterControlKernel.EnableBlink(); }
        }

        public void RoundStart() {
#if CLIENT
            BlinkClient.Reset();
            BlinkHud.Reset();
#else
            BlinkServer.Reset();
#endif
        }

        public void Release() {
            CharacterControlKernel.DisableBlink();
            BlinkGate.Forced = false;
#if CLIENT
            BlinkClient.Reset();
            BlinkHud.Reset();
#else
            BlinkServer.Reset();
#endif
        }

        public static void ControlPostfix(Character __instance, float deltaTime) {
            if (deltaTime <= 0.0f || !BlinkSim.Eligible(__instance)) { return; }
#if CLIENT
            if (!ReferenceEquals(__instance, Character.Controlled)) { return; }
            ControlPostfixKernel(__instance, deltaTime);
#else
            BlinkServer.Advance(__instance, deltaTime);
#endif
        }

#if CLIENT
        internal static void ControlPostfixKernel(Character character, float deltaTime) =>
            BlinkClient.Advance(character, deltaTime);
#else
        internal static void ControlPostfixKernel(Character character, float deltaTime, int slot, uint generation,
            NetworkConnection connection) =>
            BlinkServer.Advance(character, slot, generation, connection, deltaTime);
#endif

        public static void HudOverrideExecute(string[] args) {
            BlinkGate.Forced = args.Length > 0 && bool.TryParse(args[0], out bool wanted)
                ? wanted
                : !BlinkGate.Forced;
            ModLog.Message($"{HudOverrideName} {BlinkGate.Forced}");
        }

#if CLIENT
        public static void DrawHudPrefix(Character __instance, SpriteBatch spriteBatch) {
            if (!ReferenceEquals(__instance, Character.Controlled)) { return; }
            BlinkOverlay.Draw(spriteBatch);
        }
#endif
    }
}
