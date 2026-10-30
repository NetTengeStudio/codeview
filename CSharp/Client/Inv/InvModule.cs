using HarmonyLib;
using ScpItdMod.Core;

namespace ScpItdMod.Inv {
    public sealed class InvModule : IModule {
        public void Attach(Harmony harmony) {
            InvUi.Attach(harmony);
        }

        public void RoundStart() {
            InvUi.Reset();
        }

        public void Release() {
            InvUi.Release();
        }
    }
}
