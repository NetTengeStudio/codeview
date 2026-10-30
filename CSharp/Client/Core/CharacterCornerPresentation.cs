using System;
using Barotrauma;
using HarmonyLib;
namespace ScpItdMod.Core {
    // Removes the vanilla bottom-right character corner (portrait, job icon and
    // the health bar) because SCPITD's own HUD owns that edge. The health bar is
    // a GUI component that vanilla re-shows whenever the selected character
    // changes, and a character that existed before this plugin loaded never ran
    // the constructor hook at all. Both paths are closed here: the visibility
    // setter is forced to false, and the draw pass re-asserts the hidden state
    // for the controlled character so a stale instance cannot survive.
    public static class CharacterCornerPresentation {
        private static int drawing;

        public static void Attach(Harmony harmony) {
            Patch(harmony, "character HUD draw", () => harmony.Patch(
                AccessTools.Method(typeof(CharacterHUD), nameof(CharacterHUD.Draw)),
                prefix: ModuleHost.Bind(typeof(CharacterCornerPresentation), nameof(Enter)),
                finalizer: ModuleHost.Bind(typeof(CharacterCornerPresentation), nameof(Leave))));
            Patch(harmony, "character portrait input", () => harmony.Patch(
                AccessTools.Method(typeof(CharacterHUD), nameof(CharacterHUD.MouseOnCharacterPortrait)),
                prefix: ModuleHost.Bind(typeof(CharacterCornerPresentation), nameof(NoPortraitInput))));
            foreach (string method in new[] { "DrawBackground", "DrawForeground", "DrawIcon", "DrawJobIcon" }) {
                Patch(harmony, "character portrait " + method, () => harmony.Patch(
                    AccessTools.Method(typeof(CharacterInfo), method),
                    prefix: ModuleHost.Bind(typeof(CharacterCornerPresentation), nameof(Portrait))));
            }
            Patch(harmony, "character health bar constructor", () => harmony.Patch(
                AccessTools.Constructor(typeof(CharacterHealth), new[] { typeof(Character) }),
                postfix: ModuleHost.Bind(typeof(CharacterCornerPresentation), nameof(HealthCreated))));
            // Single choke point for every later visibility change. The setter is
            // used by the character selection code in vanilla; forcing the value
            // here keeps the bar hidden without polling it every frame.
            Patch(harmony, "character health bar visibility", () => harmony.Patch(
                AccessTools.Method(typeof(CharacterHealth), nameof(CharacterHealth.SetHealthBarVisibility)),
                prefix: ModuleHost.Bind(typeof(CharacterCornerPresentation), nameof(HideHealthBar))));
        }

        private static void Patch(Harmony harmony, string feature, Action apply) {
            try { apply(); }
            catch (Exception error) { ModuleHost.ReportFailure(feature, error); }
        }

        public static void Enter() => drawing++;
        public static void Leave() => drawing = Math.Max(0, drawing - 1);
        public static bool Portrait() => drawing == 0;
        public static bool NoPortraitInput(ref bool __result) { __result = false; return false; }
        public static void HealthCreated(CharacterHealth __instance) => __instance.SetHealthBarVisibility(false);
        public static void HideHealthBar(ref bool value) => value = false;

        public static void Enforce(Character character) {
            CharacterHealth health = character?.CharacterHealth;
            if (health == null) { return; }
            health.SetHealthBarVisibility(false);
        }

        public static void Release() {
            drawing = 0;
            for (int i = 0; i < Character.CharacterList.Count; i++) {
                Character c = Character.CharacterList[i];
                c.CharacterHealth?.SetHealthBarVisibility(c.SelectedCharacter == null);
            }
        }
    }
}
