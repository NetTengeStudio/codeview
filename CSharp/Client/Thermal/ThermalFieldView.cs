using System;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;
using ScpItdMod.Motion;
using ScpItdMod.Weather;

namespace ScpItdMod.Thermal {
    internal static class ThermalFieldView {
        private static GUICustomComponent layer;
        private static MotionScope scope;
        private static MotionHandle<float> opacity;
        private static byte mode;
        private static readonly string[] labels = { "-40", "-20", "0", "+20", "+40 °C" };
        internal static void Command(string[] args) {
            if (args.Length > 2) throw new ArgumentException("cold map [air|surface|off]");
            string option = args.Length == 1 ? mode == 0 ? "air" : "off" : args[1];
            byte next = option switch { "air" or "on" => 1, "surface" => 2, "off" => 0, _ => throw new ArgumentException("cold map [air|surface|off]") };
            if (next != 0 && !WeatherService.Active) { ModLog.Message("Тепловая карта недоступна: симуляция этой карты не активна."); return; }
            mode = next; ThermalNetwork.RequestMap(mode != 0);
            if (mode == 0) { scope?.Dispose(); scope = null; return; }
            if (scope?.Alive != true) { scope?.Dispose(); scope = MotionClient.Runtime.Scope(); opacity = scope.Scalar(); }
            opacity.To(.62f, MotionPresets.Fade);
            ModLog.Message(mode == 1 ? "Тепловая карта: воздух и вода. Стрелки — перенос тепла, квадрат — источник. /cold map surface — поверхности; /cold map off — закрыть." : "Тепловая карта: поверхности. /cold map air — воздух; /cold map off — закрыть.");
        }
        internal static void Prepare() {
            if (mode == 0 || !WeatherService.Active || GUI.DisableHUD || Character.Controlled == null) return;
            if (scope?.Alive != true) { scope?.Dispose(); scope = MotionClient.Runtime.Scope(); opacity = scope.Scalar(); opacity.To(.62f, MotionPresets.Fade); }
            layer ??= new GUICustomComponent(new RectTransform(Vector2.One, GUI.Canvas), Draw) { CanBeFocused = false };
            layer.AddToGUIUpdateList(order: -100);
        }
        private static void Draw(SpriteBatch batch, GUICustomComponent component) {
            Camera camera = GameMain.GameScreen?.Cam;
            if (mode == 0 || !WeatherService.Active || camera == null || scope?.Alive != true) return;
            Vector2 origin = camera.WorldToScreen(Vector2.Zero);
            Vector2 axisX = camera.WorldToScreen(new Vector2(WeatherService.UnitsPerMetre, 0)) - origin;
            Vector2 axisY = camera.WorldToScreen(new Vector2(0, WeatherService.UnitsPerMetre)) - origin;
            int tiles = ThermalFieldRenderer.Draw(batch, GUI.WhiteTexture, ThermalNetwork.Map, Timing.TotalTime, origin, axisX, axisY,
                new Rectangle(0, 0, GameMain.GraphicsWidth, GameMain.GraphicsHeight), opacity.Value, mode == 2);
            string title = tiles == 0 ? "ТЕПЛОВАЯ КАРТА: НЕТ СВЕЖИХ ДАННЫХ" : mode == 1 ? "ТЕМПЕРАТУРА ВОЗДУХА И ВОДЫ" : "ТЕМПЕРАТУРА ПОВЕРХНОСТЕЙ";
            ScalableFont font = GUIStyle.SmallFont.GetFontForStr(title);
            if (font == null) return;
            Vector2 size = font.MeasureString(title), at = new Vector2(MathF.Max(12, GameMain.GraphicsWidth - size.X - 16), 18);
            font.DrawString(batch, title, at + Vector2.One, Color.Black, ForceUpperCase.No);
            font.DrawString(batch, title, at, new Color(220, 220, 205), ForceUpperCase.No);
            if (tiles == 0) return;
            float width = MathF.Max(200, size.X);
            for (int i = 0; i < 80; i++) batch.Draw(GUI.WhiteTexture, new Rectangle((int)(at.X + i * width / 80), (int)(at.Y + size.Y + 4), (int)MathF.Ceiling(width / 80), 4), ThermalFieldRenderer.Temperature(-40 + i));
            for (int i = 0; i < labels.Length; i++) {
                float textWidth = font.MeasureString(labels[i]).X;
                float x = at.X + Math.Clamp(width * i / 4 - textWidth * .5f, 0, width - textWidth);
                font.DrawString(batch, labels[i], new Vector2(x, at.Y + size.Y + 10), new Color(220, 220, 205), ForceUpperCase.No);
            }
        }
        internal static void Reset() {
            mode = 0; scope?.Dispose(); scope = null;
            if (layer != null) layer.RectTransform.Parent = null;
            layer = null;
        }
    }
}
