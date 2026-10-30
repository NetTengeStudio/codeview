using ScpItdMod.Core;
using HarmonyLib;
using Barotrauma;

namespace ScpItdMod.Footsteps {
    public sealed class FootstepsModule : IModule {
        public void Attach(Harmony harmony) {
            harmony.Patch(
                AccessTools.Method(typeof(Ragdoll), nameof(Ragdoll.PlayImpactSound)),
                prefix: ModuleHost.Bind(typeof(FootstepsModule), nameof(PlayImpactSoundPrefix)));
        }

        public void RoundStart() {
            FootstepGear.Reset();
            FootstepAudio.Warm();
        }

        public void Release() {
            FootstepGear.Reset();
            FootstepAudio.Release();
        }

        public static bool PlayImpactSoundPrefix(Limb limb) => !Footfall.Intercept(limb);
    }
}
