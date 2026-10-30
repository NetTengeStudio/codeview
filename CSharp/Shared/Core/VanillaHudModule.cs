using HarmonyLib;
#if CLIENT
using ScpItdMod.Hand;
#endif

namespace ScpItdMod.Core {
    public sealed class VanillaHudModule : IModule {
        public void Attach(Harmony harmony) {
            VanillaHotbarSlots.Attach(harmony);
#if CLIENT
            HudFrame.Attach(harmony);
            CharacterCornerPresentation.Attach(harmony);
            VanillaSlotSprite.Attach(harmony);
            HandHud.Register();
            VanillaAfflictionHud.Attach(harmony);
            VanillaLobbyScreen.Attach(harmony);
            VanillaRespawnSettings.Attach();
            VanillaChatPalette.Attach();
#endif
        }

        public void RoundStart() {}

        public void Release() {
#if CLIENT
            VanillaChatPalette.Release();
            VanillaLobbyScreen.Release();
            VanillaAfflictionHud.Release();
            CharacterCornerPresentation.Release();
            VanillaSlotSprite.Release();
            HandHud.Reset();
            HudFrame.Clear();
#endif
        }
    }
}
