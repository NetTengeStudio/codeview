using System;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;
using ScpItdMod.Motion;

namespace ScpItdMod.Thermal {
    internal sealed class FeltRenderer {
        internal const string FontSample = "ТЕПЛООЩУЩЕНИЕ";
        private ScalableFont normal, small;
        private uint normalSize, smallSize;
        private float normalHeight, smallHeight, stateWidth, changeWidth, wetWidth;
        private readonly Vector2[] states = new Vector2[6];
        internal FeltLayout Layout(float width, float scale, float inset, ScalableFont primary, ScalableFont secondary) {
            if (!ReferenceEquals(normal, primary) || !ReferenceEquals(small, secondary) || normalSize != primary.Size || smallSize != secondary.Size) {
                normal = primary; small = secondary; normalSize = normal.Size; smallSize = small.Size;
                normalHeight = stateWidth = 0;
                for (int i = 0; i < states.Length; i++) {
                    Vector2 measured = normal.MeasureString(FeltPresentation.Text((FeltState)i));
                    states[i] = measured; stateWidth = MathF.Max(stateWidth, measured.X); normalHeight = MathF.Max(normalHeight, measured.Y);
                }
                Vector2 change = small.MeasureString("Нет свежих показаний"), wet = small.MeasureString("Вы промокли");
                changeWidth = change.X; wetWidth = wet.X;
                smallHeight = MathF.Max(change.Y, wet.Y);
            }
            return new FeltLayout(width, scale, inset, normalHeight, smallHeight, stateWidth, changeWidth, wetWidth);
        }
        internal static Color Tint(FeltState state) => ColorOf(FeltPresentation.Tint(state));
        private static Color ColorOf(Vec4 value) => new Color(Math.Clamp(value.X, 0, 1), Math.Clamp(value.Y, 0, 1), Math.Clamp(value.Z, 0, 1), Math.Clamp(value.W, 0, 1));
        internal void Draw(SpriteBatch batch, Texture2D pixel, Rectangle area, in FeltLayout layout, FeltPresentation view, Color text) {
            if (!view.Alive || area.Width <= 0 || area.Height < layout.Height || layout.TextScale <= 0) return;
            float s = layout.Scale;
            Color ink = ColorOf(view.InkColor);
            Color quiet = Color.Lerp(text, new Color(163, 170, 168), .28f);
            Color rail = Color.Lerp(new Color(101, 112, 109), ink, view.Focus * .45f) * (.65f + .35f * view.Availability);
            Vec2 span = view.ColumnSpan;
            float top = Math.Clamp(span.X, 2 * s, MathF.Max(2 * s, layout.GaugeBottom));
            float bottom = Math.Clamp(span.Y, top, MathF.Max(top, area.Height - 11 * s));
            top = bottom - (bottom - top) * view.Deployment;
            float x = area.X + layout.GaugeX, y0 = area.Y + top, y1 = area.Y + bottom;
            float stroke = MathF.Max(1, s);
            Line(batch, pixel, x - 2 * s, y0, x - 2 * s, y1 + s, rail, stroke);
            Line(batch, pixel, x + 2 * s, y0, x + 2 * s, y1 + s, rail, stroke);
            Line(batch, pixel, x - 2 * s, y0, x + 2 * s, y0, rail, stroke);
            for (int i = 1; i <= 5; i++) {
                float y = y1 - FeltPresentation.Position(FeltPresentation.Boundary(i)) * (y1 - y0);
                float length = (i == 5 ? 6 : 3) * s + view.Focus * 2 * s;
                Line(batch, pixel, x + 5 * s, y, MathF.Min(x + 5 * s + length, area.X + layout.TextX - 4 * s), y, rail * .8f, stroke);
            }
            float bulb = y1 + 4 * s;
            Bulb(batch, pixel, x, bulb, 4 * s, rail, stroke);
            if (view.Available) {
                float level = y1 - view.Marker * (y1 - y0), memory = y1 - view.MemoryMarker * (y1 - y0);
                Rect(batch, pixel, x - s, level, 2 * s, MathF.Max(0, y1 + 2 * s - level), ink * (.8f + .2f * view.Focus));
                Rect(batch, pixel, x - 2 * s, bulb - 2 * s, 4 * s, 4 * s, ink);
                Line(batch, pixel, x - 3 * s, level, x + 3 * s, level, ink, stroke);
                if (MathF.Abs(memory - level) > .35f * s) {
                    Line(batch, pixel, x - 5 * s, memory, x - 5 * s, level, ink * .28f, stroke);
                    Line(batch, pixel, x - 6 * s, memory, x - 4 * s, memory, ink * .45f, stroke);
                }
            } else {
                Line(batch, pixel, x - s, bulb - s, x + s, bulb + s, quiet, stroke);
                Line(batch, pixel, x - s, bulb + s, x + s, bulb - s, quiet, stroke);
            }
            Vec2 at = view.StatusPosition;
            float statusX = Math.Clamp(at.X, 0, MathF.Max(0, area.Width - states[(int)view.State].X * layout.TextScale));
            float statusY = Math.Clamp(at.Y + view.Stamp * s, 0, MathF.Max(0, area.Height - normalHeight * layout.TextScale));
            Text(batch, normal, FeltPresentation.Text(view.State), area.X + statusX, area.Y + statusY, ink, layout.TextScale);
            Vec2 detail = view.ContextPosition;
            float detailX = Math.Clamp(detail.X, 0, MathF.Max(0, area.Width - changeWidth * layout.DetailScale - 12 * s));
            float detailY = Math.Clamp(detail.Y, 0, MathF.Max(0, area.Height - smallHeight * layout.DetailScale));
            Color detailInk = quiet * view.Details;
            string change = view.ChangeText;
            if (view.Available && MathF.Abs(view.Direction) > .01f && change.Length > 0) {
                float ay = area.Y + detailY + smallHeight * layout.DetailScale * .45f;
                float ax = area.X + detailX + 3 * s, direction = -MathF.Sign(view.Direction);
                Color arrow = detailInk * MathF.Abs(view.Direction);
                Line(batch, pixel, ax, ay - 3 * s, ax, ay + 3 * s, arrow, stroke);
                Line(batch, pixel, ax, ay + direction * 3 * s, ax - 2 * s, ay, arrow, stroke);
                Line(batch, pixel, ax, ay + direction * 3 * s, ax + 2 * s, ay, arrow, stroke);
                detailX += 12 * s;
            }
            Text(batch, small, change, area.X + detailX, area.Y + detailY, detailInk, layout.DetailScale);
            string moisture = view.MoistureText;
            if (moisture.Length == 0) return;
            Vec2 humidity = view.MoisturePosition;
            float wetX = Math.Clamp(humidity.X, 0, MathF.Max(0, area.Width - wetWidth * layout.DetailScale - 12 * s));
            float wetY = Math.Clamp(humidity.Y, 0, MathF.Max(0, area.Height - smallHeight * layout.DetailScale));
            Color wetInk = quiet * (view.Damp * view.Details);
            float dx = area.X + wetX + 3 * s, dy = area.Y + wetY + smallHeight * layout.DetailScale * .45f;
            Line(batch, pixel, dx, dy - 4 * s, dx - 3 * s, dy + s, wetInk, stroke);
            Line(batch, pixel, dx, dy - 4 * s, dx + 3 * s, dy + s, wetInk, stroke);
            Line(batch, pixel, dx - 3 * s, dy + s, dx, dy + 3 * s, wetInk, stroke);
            Line(batch, pixel, dx + 3 * s, dy + s, dx, dy + 3 * s, wetInk, stroke);
            Rect(batch, pixel, dx - s, dy + s - view.Wetness * 3 * s, 2 * s, 2 * s + view.Wetness * 3 * s, wetInk * .65f);
            Text(batch, small, moisture, area.X + wetX + 12 * s, area.Y + wetY, wetInk, layout.DetailScale);
        }
        private static void Bulb(SpriteBatch batch, Texture2D pixel, float x, float y, float r, Color color, float stroke) {
            float k = r * .5f;
            Line(batch, pixel, x - k, y - r, x - r, y - k, color, stroke);
            Line(batch, pixel, x - r, y - k, x - r, y + k, color, stroke);
            Line(batch, pixel, x - r, y + k, x - k, y + r, color, stroke);
            Line(batch, pixel, x - k, y + r, x + k, y + r, color, stroke);
            Line(batch, pixel, x + k, y + r, x + r, y + k, color, stroke);
            Line(batch, pixel, x + r, y + k, x + r, y - k, color, stroke);
            Line(batch, pixel, x + r, y - k, x + k, y - r, color, stroke);
        }
        private static void Text(SpriteBatch batch, ScalableFont font, string text, float x, float y, Color color, float scale) {
            if (text.Length != 0) font.DrawString(batch, text, new Vector2(x, y), color, 0, Vector2.Zero, scale, SpriteEffects.None, .99f, Alignment.TopLeft, ForceUpperCase.No);
        }
        private static void Rect(SpriteBatch batch, Texture2D pixel, float x, float y, float width, float height, Color color) {
            if (width > 0 && height > 0) batch.Draw(pixel, new Vector2(x, y), null, color, 0, Vector2.Zero, new Vector2(width, height), SpriteEffects.None, .99f);
        }
        private static void Line(SpriteBatch batch, Texture2D pixel, float x0, float y0, float x1, float y1, Color color, float stroke) {
            if (y0 == y1) {
                Rect(batch, pixel, MathF.Min(x0, x1), x1 >= x0 ? y0 : y0 - stroke, MathF.Abs(x1 - x0), stroke, color);
                return;
            }
            if (x0 == x1) {
                Rect(batch, pixel, y1 >= y0 ? x0 - stroke : x0, MathF.Min(y0, y1), stroke, MathF.Abs(y1 - y0), color);
                return;
            }
            var delta = new Vector2(x1 - x0, y1 - y0);
            batch.Draw(pixel, new Vector2(x0, y0), null, color, MathF.Atan2(delta.Y, delta.X), Vector2.Zero, new Vector2(delta.Length(), stroke), SpriteEffects.None, .99f);
        }
    }
}
