using HarmonyLib;
using ScpItdMod.Core;

namespace ScpItdMod.Rp {
    public sealed class EmoteModule : IModule, IRoundEnd {
        public void Attach(Harmony harmony) {
            EmoteTrace.Register();
#if SERVER
            RpHost.LoadCatalog();
#else
            EmoteView.Attach(harmony);
#endif
        }
        public void RoundStart() {
#if CLIENT
            EmoteView.Reset();
#endif
        }
        public void RoundEnd() => RoundStart();
        public void Release() => RoundStart();
    }
}
