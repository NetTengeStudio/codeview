using HarmonyLib;
using ScpItdMod.Core;

namespace ScpItdMod.Med {
    public sealed class MedModule : IModule {
        public void Attach(Harmony harmony) {
            MedWorld.Load();
#if SERVER
            MedHost.Attach(harmony);
#else
            MedUi.Attach(harmony);
#endif
        }

        public void RoundStart() {
#if CLIENT
            MedUi.Reset();
#endif
        }

        public void Release() {
#if CLIENT
            MedUi.Reset();
#endif
        }
    }
}
