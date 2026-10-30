using HarmonyLib;
using ScpItdMod.Core;

namespace ScpItdMod.Pc {
    public sealed class PcModule : IModule, IRoundEnd {
        public void Attach(Harmony harmony) {
#if SERVER
            PcHost.Attach(harmony);
#else
            PcScreen.Attach(harmony);
#endif
        }

        public void RoundStart() {
#if SERVER
            PcHost.RoundStart();
#else
            PcScreen.Reset();
#endif
        }

        public void Release() {
#if SERVER
            PcHost.Release();
#else
            PcScreen.Release();
#endif
        }

        public void RoundEnd() => RoundStart();
    }
}
