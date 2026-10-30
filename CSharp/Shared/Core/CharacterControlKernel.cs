using System;
using System.Reflection;
using Barotrauma;
using HarmonyLib;
using MonoMod.RuntimeDetour;
using ScpItdMod.Blink;
using ScpItdMod.Stamina;
#if SERVER
using Barotrauma.Networking;
#endif

namespace ScpItdMod.Core {
    public sealed class CharacterControlModule : IModule {
        public void Attach(Harmony harmony) => CharacterControlKernel.Attach();

        public void RoundStart() {}

        public void Release() => CharacterControlKernel.Release();
    }

    public static class CharacterControlKernel {
        private const uint StaminaBit = 1u << 0;
        private const uint BlinkBit = 1u << 1;

        private delegate void ControlOrig(Character self, float deltaTime, Camera camera);
        private delegate void ControlDetour(ControlOrig orig, Character self, float deltaTime, Camera camera);

        private static Hook controlHook;
        private static uint features;

        public static bool Available => controlHook != null;

        public static void Attach() {
            if (controlHook != null) { return; }
            MethodInfo source = typeof(Character).GetMethod(nameof(Character.Control),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new[] { typeof(float), typeof(Camera) }, null);
            if (source == null) { throw new MissingMethodException(typeof(Character).FullName, nameof(Character.Control)); }
            controlHook = new Hook(source, new ControlDetour(Control));
        }

        public static void Release() {
            features = 0u;
            Hook hook = controlHook;
            controlHook = null;
            hook?.Dispose();
        }

        public static void EnableStamina() => features |= StaminaBit;

        public static void DisableStamina() => features &= ~StaminaBit;

        public static void EnableBlink() => features |= BlinkBit;

        public static void DisableBlink() => features &= ~BlinkBit;

        private static void Control(ControlOrig orig, Character character, float deltaTime, Camera camera) {
            uint active = features;
            if (active == 0u || character == null) {
                orig(character, deltaTime, camera);
                return;
            }
#if CLIENT
            if (!ReferenceEquals(character, Character.Controlled)) {
                orig(character, deltaTime, camera);
                return;
            }
            bool stamina = (active & StaminaBit) != 0u && StaminaSim.Eligible(character);
            bool blink = (active & BlinkBit) != 0u && BlinkSim.Eligible(character);
            if (stamina) { StaminaModule.ControlPrefixKernel(character); }
            orig(character, deltaTime, camera);
            if (deltaTime <= 0.0f) { return; }
            if (stamina) { StaminaModule.ControlPostfixKernel(character, deltaTime); }
            if (blink) { BlinkModule.ControlPostfixKernel(character, deltaTime); }
#else
            bool stamina = (active & StaminaBit) != 0u && StaminaSim.Eligible(character);
            bool blink = (active & BlinkBit) != 0u && BlinkSim.Eligible(character);
            int slot = -1;
            uint generation = 0u;
            NetworkConnection owner = null;
            if (stamina || blink) {
                slot = CharacterRuntime.Slots.SlotFor(character, out _);
                if (slot >= 0) {
                    generation = CharacterRuntime.Slots.Generation(slot);
                    owner = OwnerConnection.Of(character, slot, generation);
                }
            }
            if (stamina && slot >= 0) { StaminaModule.ControlPrefixKernel(character, slot, generation); }
            orig(character, deltaTime, camera);
            if (deltaTime <= 0.0f || slot < 0) { return; }
            if (stamina) { StaminaModule.ControlPostfixKernel(character, deltaTime, slot, generation, owner); }
            if (blink) { BlinkModule.ControlPostfixKernel(character, deltaTime, slot, generation, owner); }
#endif
        }
    }
}
