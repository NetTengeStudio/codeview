using System;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;
using ScpItdMod.Motion;
using ScpItdMod.Weather;

namespace ScpItdMod.Thermal {
    internal static class ThermalHealthPanel {
        private static CharacterHealth owner;
        private static GUICustomComponent row;
        private static FeltHealthLayout attachment;
        private static FeltPresentation presentation;
        private static FeltRenderer renderer;
        private static string tooltip;
        private static FeltLayout layout;
        private static bool layoutError;
        internal static void Prepare(CharacterHealth health, GUIProgressBar healthBar) {
            if (!WeatherService.Active || Character.Controlled == null || !ReferenceEquals(CharacterHealth.OpenHealthWindow, health) || !ReferenceEquals(health.Character, Character.Controlled)) return;
            if (!ReferenceEquals(owner, health) || row?.RectTransform.Parent == null) {
                Detach();
                RectTransform bar = healthBar?.RectTransform.Parent;
                if (bar?.Parent?.GUIComponent is not GUILayoutGroup group || group.IsHorizontal) {
                    if (!layoutError) { layoutError = true; ModLog.Error("Thermal: cannot locate the health-window vertical layout"); }
                    return;
                }
                owner = health;
                renderer = new FeltRenderer();
                attachment = new FeltHealthLayout(health, healthBar, Draw, Interact);
                row = attachment.Row;
            }
            if (presentation?.Alive != true) { presentation?.Dispose(); presentation = new FeltPresentation(MotionClient.Runtime); }
            bool available = ThermalNetwork.TryLocal(out ThermalReading reading);
            presentation.Update(available, reading.Skin, reading.Wetness, reading.Sequence, Timing.TotalTime);
            string help = presentation.Help;
            if (help != tooltip) { row.ToolTip = help; tooltip = help; }
            ScalableFont normal = GUIStyle.SubHeadingFont.GetFontForStr(FeltPresentation.Text(presentation.State));
            ScalableFont small = GUIStyle.SmallFont.GetFontForStr(FeltRenderer.FontSample);
            if (normal == null || small == null) { row.Visible = false; return; }
            row.Visible = true;
            layout = renderer.Layout(attachment.Width, GUI.Scale, attachment.TextInset, normal, small);
            presentation.Place(layout);
            attachment.Resize(layout.Height);
        }
        internal static void Poll() {
            if (row != null && (!WeatherService.Active || !ReferenceEquals(owner, CharacterHealth.OpenHealthWindow) || !ReferenceEquals(owner?.Character, Character.Controlled))) Detach();
        }
        internal static void Changed(CharacterHealth current) { if (!ReferenceEquals(current, owner)) Detach(); }
        private static void Interact(float deltaTime, GUICustomComponent component) {
            if (presentation?.Alive == true) presentation.Hover(ReferenceEquals(GUI.MouseOn, component));
        }
        private static void Draw(SpriteBatch batch, GUICustomComponent component) {
            if (!WeatherService.Active || presentation?.Alive != true || !ReferenceEquals(owner, CharacterHealth.OpenHealthWindow) || !ReferenceEquals(owner.Character, Character.Controlled)) return;
            renderer.Draw(batch, GUI.WhiteTexture, component.Rect, layout, presentation, GUIStyle.TextColorNormal);
        }
        private static void Detach() {
            attachment?.Dispose();
            presentation?.Dispose();
            owner = null; row = null; attachment = null; presentation = null; renderer = null; tooltip = null;
        }
        internal static void Reset() { Detach(); layoutError = false; }
    }
}
