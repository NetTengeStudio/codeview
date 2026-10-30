using System;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;

namespace ScpItdMod.Inv {
    // What one ring slot shows this frame. InvUi fills it from the inventory; the renderer reads nothing else.
    public struct RingContent {
        public Sprite Sprite;
        public Color Tint;
        // Container fill and item condition as 0..1, negative when the bar is not shown.
        public float Fill, Condition;
        public int Stack;
        // Pop-in of the icon, 0..1.
        public float IconPop;
        public float IconAlpha;
        // The slot holds an item that is not drawn here (dragged away from this slot).
        public bool Hidden;
        public bool Held;
        public bool DropTarget;
    }

    // The caption in the hub: name, hint and an optional extra line, with their own alpha.
    public struct RingCaption {
        public string Name, Hint, Extra;
        public float Focus, Swap, Alpha;
    }

    // Draws the ring in fixed passes: backing wedges, light and drop targets, hub, contents, caption.
    // No pass reads game state; every value arrives in the arguments, so the order is the whole logic.
    public static class RingRenderer {
        private const float WedgeShade = 0.6f, LightWedge = 0.18f, LightHeld = 0.12f, DropShade = 0.3f;
        private const float IconFit = 0.74f, IconMaxScale = 2.5f, IconAlphaMin = 0.02f;

        public static void Draw(SpriteBatch sb, in InvRing ring, ReadOnlySpan<RingWedge> wedges, ReadOnlySpan<RingContent> content,
            int picked, float fade, float spread, in RingCaption caption) {
            int count = Math.Min(wedges.Length, content.Length);
            for (int k = 0; k < count; k++) {
                RingWedge w = wedges[k];
                if (!w.Visible) { continue; }
                // A wedge's alpha is its own bloom: the shared reveal would hide the staggered retract.
                float alpha = w.Grown;
                RingPaint.Wedge(sb, ring, k, Color.Black * (WedgeShade * alpha), w.Halo);
                float light = LightWedge * w.Hover + (content[k].Held ? LightHeld : 0f);
                if (light > 0.001f) { RingPaint.Wedge(sb, ring, k, Color.White * (light * alpha), w.Halo); }
                if (k == picked && content[k].DropTarget) { RingPaint.Wedge(sb, ring, k, GUIStyle.Green * (DropShade * alpha), w.Halo); }
            }
            RingPaint.Hub(sb, ring, spread, fade);
            for (int k = 0; k < count; k++) {
                RingWedge w = wedges[k];
                if (!w.Visible || content[k].Sprite == null || content[k].Hidden) { continue; }
                DrawContent(sb, ring, w, content[k], w.Grown * Math.Clamp(content[k].IconAlpha, 0f, 1f));
            }
            DrawCaption(sb, new Vector2(ring.X, ring.Y), ring.HubRadius * spread, fade, caption);
        }

        private static void DrawContent(SpriteBatch sb, in InvRing ring, in RingWedge w, in RingContent c, float alpha) {
            if (alpha < IconAlphaMin || w.Side < 8) { return; }
            Sprite sprite = c.Sprite;
            if (sprite.size.X <= 0f || sprite.size.Y <= 0f) { return; }
            float fit = w.Side * IconFit;
            float scale = MathF.Min(MathF.Min(fit / sprite.size.X, fit / sprite.size.Y), IconMaxScale * w.Side / ring.Cell) * c.IconPop;
            var centre = new Vector2(w.X + w.Side * 0.5f, w.Y + w.Side * 0.5f);
            sprite.Draw(sb, centre + Vector2.One * 2f, Color.Black * (0.5f * alpha), 0f, scale);
            sprite.Draw(sb, centre, c.Tint * alpha, 0f, scale);

            int bar = Math.Max(2, (int)(w.Side * 0.03f));
            int barWidth = (int)(w.Side * 0.6f);
            int barX = w.X + w.Side / 2 - barWidth / 2;
            int barY = w.Y + w.Side - bar;
            if (c.Fill >= 0f) {
                DrawBar(sb, new Rectangle(barX, barY, barWidth, bar), c.Fill, alpha);
                barY -= bar * 2;
            }
            if (c.Condition >= 0f) { DrawBar(sb, new Rectangle(barX, barY, barWidth, bar), c.Condition, alpha); }

            if (c.Stack > 1) {
                string text = "x" + c.Stack.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Vector2 size = GUIStyle.SmallFont.MeasureString(text);
                Vector2 at = new Vector2(w.X + w.Side, w.Y + w.Side) - size - new Vector2(2f, bar * 2f);
                GUI.DrawString(sb, Snap(at + Vector2.One), text, Color.Black * alpha, font: GUIStyle.SmallFont);
                GUI.DrawString(sb, Snap(at), text, Color.White * alpha, font: GUIStyle.SmallFont);
            }
        }

        private static void DrawBar(SpriteBatch sb, Rectangle area, float state, float alpha) {
            state = Math.Clamp(state, 0f, 1f);
            GUI.DrawRectangle(sb, area, Color.Black * (0.6f * alpha), true);
            Color color = ToolBox.GradientLerp(state, GUIStyle.ColorInventoryEmpty, GUIStyle.ColorInventoryHalf, GUIStyle.ColorInventoryFull);
            GUI.DrawRectangle(sb, new Rectangle(area.X, area.Y, (int)(area.Width * state), area.Height), color * (0.9f * alpha), true);
        }

        private static void DrawCaption(SpriteBatch sb, Vector2 centre, float hub, float alpha, in RingCaption caption) {
            if (string.IsNullOrEmpty(caption.Name)) { return; }
            float focus = Math.Clamp(caption.Focus, 0f, 1f), swap = Math.Clamp(caption.Swap, 0f, 1f);
            float a = alpha * caption.Alpha * swap;
            Color ink = Color.White * ((0.55f + 0.45f * focus) * a);
            Vector2 nameSize = GUIStyle.SubHeadingFont.MeasureString(caption.Name);
            Vector2 at = centre - new Vector2(nameSize.X * 0.5f, nameSize.Y * (0.5f + 0.35f * focus) + (1f - swap) * 6f);
            GUI.DrawString(sb, Snap(at) + Vector2.One * 2f, caption.Name, Color.Black * (0.8f * a), font: GUIStyle.SubHeadingFont);
            GUI.DrawString(sb, Snap(at), caption.Name, ink, font: GUIStyle.SubHeadingFont);
            if (focus < 0.02f || caption.Hint.Length == 0) { return; }
            float hintAlpha = a * focus;
            Vector2 hintSize = GUIStyle.SmallFont.MeasureString(caption.Hint);
            Vector2 hintAt = centre + new Vector2(-hintSize.X * 0.5f, nameSize.Y * 0.25f);
            GUI.DrawString(sb, Snap(hintAt) + Vector2.One, caption.Hint, Color.Black * (0.8f * hintAlpha), font: GUIStyle.SmallFont);
            GUI.DrawString(sb, Snap(hintAt), caption.Hint, Color.White * (0.75f * hintAlpha), font: GUIStyle.SmallFont);
            if (caption.Extra.Length == 0) { return; }
            Vector2 extraSize = GUIStyle.SmallFont.MeasureString(caption.Extra);
            Vector2 extraAt = centre + new Vector2(-extraSize.X * 0.5f, nameSize.Y * 0.25f + hintSize.Y);
            GUI.DrawString(sb, Snap(extraAt) + Vector2.One, caption.Extra, Color.Black * (0.8f * hintAlpha), font: GUIStyle.SmallFont);
            GUI.DrawString(sb, Snap(extraAt), caption.Extra, Color.White * (0.75f * hintAlpha), font: GUIStyle.SmallFont);
        }

        private static Vector2 Snap(Vector2 at) => new Vector2(MathF.Round(at.X), MathF.Round(at.Y));
    }
}
