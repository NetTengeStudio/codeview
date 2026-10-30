using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace ScpItdMod.Core {
    public static class VanillaAfflictionHud {
        private const int StackRow = 2;

        private static readonly Vector2 GroupSize = new Vector2(257.0f, 50.0f);

        private static HudSlot slot;

        public static void Attach(Harmony harmony) {
            slot = HudDock.Register(HudCorner.BottomRight, GroupSize, StackRow);
            harmony.Patch(
                AccessTools.Method(typeof(HUDLayoutSettings), nameof(HUDLayoutSettings.CreateAreas)),
                postfix: ModuleHost.Bind(typeof(VanillaAfflictionHud), nameof(CreateAreasPostfix)));
            harmony.Patch(
                AccessTools.Method(typeof(CharacterHealth), nameof(CharacterHealth.UpdateStatusHUD)),
                postfix: ModuleHost.Bind(typeof(VanillaAfflictionHud), nameof(StatusHudPostfix)));
        }

        public static void Release() {
            slot = null;
            for (int i = 0; i < Character.CharacterList.Count; i++) {
                CharacterHealth health = Character.CharacterList[i].CharacterHealth;
                if (health == null) { continue; }
                Settle(health.afflictionIconContainer);
                Settle(health.hiddenAfflictionIconContainer);
            }
            HUDLayoutSettings.CreateAreas();
        }

        private static void Settle(GUIComponent component) {
            if (component == null) { return; }
            component.RectTransform.ScreenSpaceOffset = Point.Zero;
        }

        public static void CreateAreasPostfix() {
            if (slot == null) { return; }
            HudDock.Layout();
            HUDLayoutSettings.HealthBarAfflictionArea = new Rectangle(
                Scalar.Round(slot.Target.X), Scalar.Round(slot.Target.Y),
                Scalar.Round(slot.Size.X), Scalar.Round(slot.Size.Y));

            Rectangle objectives = HUDLayoutSettings.TutorialObjectiveListArea;
            HUDLayoutSettings.TutorialObjectiveListArea = new Rectangle(
                objectives.X, objectives.Y, objectives.Width,
                (HUDLayoutSettings.HealthBarAfflictionArea.Top - HUDLayoutSettings.Padding) - objectives.Y);
        }

        public static void StatusHudPostfix(CharacterHealth __instance) {
            if (slot == null || Character.Controlled == null) { return; }
            if (!ReferenceEquals(__instance, Character.Controlled.CharacterHealth)) { return; }

            GUIComponent icons = __instance.afflictionIconContainer;
            if (icons == null) { return; }

            HudDock.Layout();
            RectTransform placement = icons.RectTransform;
            Point anchor = placement.Rect.Location - placement.ScreenSpaceOffset;
            var wanted = new Point(
                Scalar.Round(slot.Position.X + slot.Size.X) - anchor.X - placement.Rect.Width,
                Scalar.Round(slot.Position.Y) - anchor.Y);
            Point shift = wanted - placement.ScreenSpaceOffset;
            placement.ScreenSpaceOffset = wanted;

            GUIComponent stowed = __instance.hiddenAfflictionIconContainer;
            if (stowed == null) { return; }
            stowed.RectTransform.ScreenSpaceOffset += shift;
        }
    }
}
