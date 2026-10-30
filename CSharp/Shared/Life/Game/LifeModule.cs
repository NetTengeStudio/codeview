using HarmonyLib;
using ScpItdMod.Core;

namespace ScpItdMod.Life {
    public sealed class LifeModule : IModule {
        public const string Channel = "scpitd.life";
        public const byte Proto = 1;
        public const byte Die = 1;

        public void Attach(Harmony harmony) {
#if SERVER
            LifeHost.Attach(harmony);
#else
            LifeUi.Attach(harmony);
#endif
        }

        public void RoundStart() {
#if SERVER
            LifeHost.RoundStart();
#endif
        }

        public void Release() {
#if SERVER
            LifeHost.Release();
#endif
#if CLIENT
            LifeUi.Release();
#endif
        }
    }
}
