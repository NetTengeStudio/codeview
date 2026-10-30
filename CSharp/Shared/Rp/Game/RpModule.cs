using HarmonyLib;
using ScpItdMod.Core;

namespace ScpItdMod.Rp {
    public sealed class RpModule : IModule, IRoundEnd {
        public void Attach(Harmony harmony) {
            RpAccess.Attach(harmony);
#if SERVER
            RpHost.Attach(harmony);
#else
            RpClient.Attach(harmony);
            ActPanel.Attach(harmony);
            Targeting.Attach(harmony);
#endif
        }

        public void RoundStart() {
#if SERVER
            RpHost.RoundStart();
#else
            RpClient.Reset();
            ActPanel.Reset();
            Targeting.Cancel();
#endif
        }

        public void Release() {
#if SERVER
            RpHost.Release();
#else
            RpClient.Reset();
            ActPanel.Release();
            Targeting.Cancel();
#endif
        }
        public void RoundEnd() => RoundStart();
    }
}
