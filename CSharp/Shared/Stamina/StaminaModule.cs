using Barotrauma;
using ScpItdMod.Core;
using HarmonyLib;
#if SERVER
using Barotrauma.Networking;
#endif

namespace ScpItdMod.Stamina {
    public sealed class StaminaModule : IModule {
        public void Attach(Harmony harmony) {
            bool kernel = CharacterControlKernel.Available;
            if (!kernel) {
                harmony.Patch(
                    AccessTools.Method(typeof(Character), nameof(Character.Control)),
                    prefix: ModuleHost.Bind(typeof(StaminaModule), nameof(ControlPrefix)),
                    postfix: ModuleHost.Bind(typeof(StaminaModule), nameof(ControlPostfix)));
            }
#if CLIENT
            harmony.Patch(
                AccessTools.PropertyGetter(typeof(Camera), nameof(Camera.DefaultZoom)),
                postfix: ModuleHost.Bind(typeof(StaminaModule), nameof(DefaultZoomPostfix)));
            StaminaClient.Hook();
            StaminaHud.Register();
#endif
            if (kernel) { CharacterControlKernel.EnableStamina(); }
        }

        public void RoundStart() {
#if CLIENT
            StaminaClient.Reset();
            StaminaApi.Reset();
            StaminaAudio.Reset();
            StaminaAudio.Warm();
            StaminaHud.Reset();
#else
            StaminaServer.Reset();
#endif
        }

        public void Release() {
            CharacterControlKernel.DisableStamina();
#if CLIENT
            StaminaClient.Reset();
            StaminaApi.Reset();
            StaminaAudio.Release();
            StaminaHud.Reset();
#else
            StaminaServer.Reset();
#endif
        }

        public static void ControlPrefix(Character __instance) {
            if (!StaminaSim.Eligible(__instance)) { return; }
#if CLIENT
            if (!ReferenceEquals(__instance, Character.Controlled)) { return; }
            ControlPrefixKernel(__instance);
#else
            if (!StaminaServer.TryGet(__instance, out StaminaState state)) { return; }
            Apply(__instance, state);
#endif
        }

        public static void ControlPostfix(Character __instance, float deltaTime) {
            if (deltaTime <= 0.0f || !StaminaSim.Eligible(__instance)) { return; }
#if CLIENT
            if (!ReferenceEquals(__instance, Character.Controlled)) { return; }
            ControlPostfixKernel(__instance, deltaTime);
#else
            StaminaActivity activity = StaminaSim.Classify(__instance);
            bool exerting = StaminaSim.Exerting(__instance, activity);
            StaminaServer.Advance(__instance, activity, exerting, deltaTime);
#endif
        }

#if CLIENT
        internal static void ControlPrefixKernel(Character character) => Apply(character, StaminaClient.Predicted);

        internal static void ControlPostfixKernel(Character character, float deltaTime) {
            StaminaActivity activity = StaminaSim.Classify(character);
            bool exerting = StaminaSim.Exerting(character, activity);
            StaminaClient.Advance(character, activity, exerting, deltaTime);
            StaminaApi.Observe();
            StaminaAudio.Advance();
        }
#else
        internal static void ControlPrefixKernel(Character character, int slot, uint generation) {
            StaminaServer.Ensure(slot, generation);
            if (StaminaServer.TryGet(slot, generation, out StaminaState state)) { Apply(character, state); }
        }

        internal static void ControlPostfixKernel(Character character, float deltaTime, int slot, uint generation,
            NetworkConnection connection) {
            StaminaActivity activity = StaminaSim.Classify(character);
            bool exerting = StaminaSim.Exerting(character, activity);
            StaminaServer.Advance(character, slot, generation, connection, activity, exerting, deltaTime);
        }
#endif

        private static void Apply(Character character, StaminaState state) {
            if (state.Locked) { character.DisableRunning = true; }
            float speed = StaminaSim.Speed(state);
            if (speed < 1.0f) { character.StackSpeedMultiplier(speed); }
        }

#if CLIENT
        public static void DefaultZoomPostfix(ref float __result) {
            if (GameMain.GameSession == null || Screen.Selected != GameMain.GameScreen ||
                Character.Controlled == null) {
                return;
            }
            __result *= 1.0f + StaminaConfig.ZoomBoost * StaminaClient.Exhaustion;
        }
#endif
    }
}
