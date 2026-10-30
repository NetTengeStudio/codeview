using HarmonyLib;

namespace ScpItdMod.Core {
    public sealed class VanillaAudioModule : IModule {
        public void Attach(Harmony harmony) {
            VanillaAudioDevices.Attach(harmony);
        }

        public void RoundStart() {}

        public void Release() {
            VanillaAudioDevices.Release();
        }
    }
}
