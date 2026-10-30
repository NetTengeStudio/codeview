using Barotrauma;
using HarmonyLib;
using ScpItdMod.Core;

namespace ScpItdMod.Hud {
    public static class ReportButtons {
        public static void Attach(Harmony harmony) {
            harmony.Patch(AccessTools.Method(typeof(CrewManager), nameof(CrewManager.CreateReportButtons)), prefix: ModuleHost.Bind(typeof(ReportButtons), nameof(CreatePrefix)));
            harmony.Patch(AccessTools.Method(typeof(CrewManager), nameof(CrewManager.UpdateReports)), prefix: ModuleHost.Bind(typeof(ReportButtons), nameof(UpdatePrefix)));
        }

        public static bool CreatePrefix(GUIComponent parent) {
            parent.Visible = false;
            parent.CanBeFocused = false;
            parent.RectTransform.NonScaledSize = new Microsoft.Xna.Framework.Point(0, parent.RectTransform.NonScaledSize.Y);
            return false;
        }

        public static bool UpdatePrefix(CrewManager __instance) {
            if (__instance.ReportButtonFrame != null) { __instance.ReportButtonFrame.Visible = false; }
            return false;
        }
    }
}
