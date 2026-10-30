using HarmonyLib;
using ScpItdMod.Core;

namespace ScpItdMod.Hud {
    public sealed class HudModule : IModule {
        public void Attach(Harmony harmony) {
            TypeSettings.Attach();
            ChatType.Attach(harmony);
            Speech.Attach(harmony);
            ReportButtons.Attach(harmony);
        }

        public void RoundStart() {
            ChatType.Release();
            Speech.Release();
            TypeSound.Release();
            TypeSound.Warm();
        }

        public void Release() {
            ChatType.Release();
            Speech.Release();
            TypeSound.Release();
            TypeGlyphRenderer.Release();
        }
    }
}
