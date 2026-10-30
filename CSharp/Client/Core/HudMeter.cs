using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ScpItdMod.Core {
    public readonly struct HudMeter {
        public const int SegmentStride = 10;
        public const int SegmentSpan = 202;

        private readonly Rectangle iconBorder;
        private readonly Rectangle iconArea;
        private readonly Rectangle barBorder;
        private readonly Rectangle segmentArea;

        public HudMeter(Rectangle iconBorder, Rectangle iconArea, Rectangle barBorder, Rectangle segmentArea) {
            this.iconBorder = iconBorder;
            this.iconArea = iconArea;
            this.barBorder = barBorder;
            this.segmentArea = segmentArea;
        }

        public static int Segments(float fill) => (int)(SegmentSpan * Scalar.Clamp01(fill) / SegmentStride);

        public void Draw(SpriteBatch spriteBatch, HudSlot slot, Texture2D icon, Texture2D segment,
            float fill, float alpha) {
            Vector2 origin = slot.Position;
            float scale = slot.Scale;
            float thickness = HudPaint.Hairline(scale);
            Color ink = HudPaint.Alpha(HudPaint.Ink, alpha);
            Color plate = HudPaint.Alpha(HudPaint.Plate, alpha);

            Rectangle area = HudPaint.Project(iconArea, origin, scale);
            HudPaint.Fill(spriteBatch, area, plate);
            if (icon != null) { HudPaint.Blit(spriteBatch, icon, area, ink); }
            HudPaint.Outline(spriteBatch, HudPaint.Project(iconBorder, origin, scale), ink, thickness);
            HudPaint.Outline(spriteBatch, HudPaint.Project(barBorder, origin, scale), ink, thickness);

            if (segment == null) { return; }
            int count = Segments(fill);
            Rectangle cell = segmentArea;
            for (int i = 0; i < count; i++) {
                cell.X = segmentArea.X + i * SegmentStride;
                HudPaint.Blit(spriteBatch, segment, HudPaint.Project(cell, origin, scale), ink);
            }
        }
    }
}
