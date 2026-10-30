using HarmonyLib;
using ScpItdMod.Core;

namespace ScpItdMod.Rec {
    public sealed class RecModule : IModule, IRoundEnd {
        public void Attach(Harmony harmony) {
#if SERVER
            CamHost.Attach(harmony);
#else
            CamClient.Attach();
            ScreenCameras.Attach(harmony);
            Memory.Attach(harmony);
            Visions.Attach(harmony);
#endif
        }

        public void RoundStart() {
#if SERVER
            CamHost.RoundStart();
#else
            CamClient.Reset();
            ScreenCameras.Clear();
            Memory.Reset();
            Visions.Reset();
            Ghosts.Release();
#endif
        }

        public void RoundEnd() => RoundStart();

        public void Release() {
#if SERVER
            CamHost.Release();
#else
            CamClient.Reset();
            ScreenCameras.Clear();
            Memory.Detach();
            Memory.Reset();
            Visions.Reset();
            Ghosts.Release();
#endif
        }
    }
}
