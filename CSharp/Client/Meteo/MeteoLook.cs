using System;
using ScpItdMod.Ui;
using ScpItdMod.Xp;

namespace ScpItdMod.Meteo {
    public static class MeteoLook {
        public const int W = XpLook.ScreenW, H = XpLook.ScreenH;
        public const float RowH = 19, Pad = 10;

        public static readonly Rgba Page = Rgba.Hex(0xBFC4BD);
        public static readonly Rgba Surface = Rgba.Hex(0xD2D5CE);
        public static readonly Rgba Plot = Rgba.Hex(0xE3E5DD);
        public static readonly Rgba Ink = Rgba.Hex(0x161B21);
        public static readonly Rgba Muted = Rgba.Hex(0x4C5355);
        public static readonly Rgba Rule = Rgba.Hex(0xA7AEA5);
        public static readonly Rgba Grid = Rgba.Hex(0xC3C9BF);
        public static readonly Rgba Axis = Rgba.Hex(0x7D857C);
        public static readonly Rgba Zebra = Rgba.Hex(0xDADDD5);
        public static readonly Rgba Accent = Rgba.Hex(0x213E68);
        public static readonly Rgba Chrome = Rgba.Hex(0x35424A);
        public static readonly Rgba Highlight = Rgba.Hex(0xF1F3EC);
        public static readonly Rgba ControlHot = Rgba.Hex(0xECEFE8);
        public static readonly Rgba Warn = Rgba.Hex(0xA85A00);
        public static readonly Rgba Alarm = Rgba.Hex(0xA01914);
        public static readonly Rgba Good = Rgba.Hex(0x1E6B2E);
        public static readonly Rgba WarnFill = Rgba.Hex(0xFFF0B8);
        public static readonly Rgba AlarmFill = Rgba.Hex(0xF8CFC8);
        public static readonly Rgba InfoFill = Rgba.Hex(0xFFFFE1);
        public static readonly Rgba Temperature = Rgba.Hex(0x172F91);
        public static readonly Rgba Pressure = Rgba.Hex(0x172F91);
        public static readonly Rgba Wind = Rgba.Hex(0x172F91);
        public static readonly Rgba Visibility = Rgba.Hex(0x172F91);

        public static Rgba Tone(MeteoAlert a) => a switch { MeteoAlert.Storm => Alarm, MeteoAlert.Warning => Warn, MeteoAlert.Watch => Accent, _ => Ink };

        public static Rgba Fill(MeteoAlert a) => a switch { MeteoAlert.Storm => AlarmFill, MeteoAlert.Warning => WarnFill, _ => InfoFill };
    }

    public static class Scope {
        public static void Line(ICanvas c, float x0, float y0, float x1, float y1, Rgba color, float t = 1) {
            float dx = x1 - x0, dy = y1 - y0;
            if (MathF.Abs(dx) >= MathF.Abs(dy)) {
                if (x1 < x0) { (x0, x1) = (x1, x0); (y0, y1) = (y1, y0); dx = -dx; dy = -dy; }
                int ax = (int)MathF.Floor(x0), bx = (int)MathF.Floor(x1);
                for (int x = ax; x <= bx; x++) {
                    float ya = y0 + dy * (dx == 0 ? 0 : (x - x0) / dx), yb = y0 + dy * (dx == 0 ? 0 : (x + 1 - x0) / dx);
                    if (x == ax) { ya = y0; }
                    if (x == bx) { yb = y1; }
                    float top = MathF.Min(ya, yb), bottom = MathF.Max(ya, yb);
                    c.Fill(new Box(x, MathF.Floor(top), 1, MathF.Max(t, MathF.Floor(bottom) - MathF.Floor(top) + 1)), color);
                }
            } else {
                if (y1 < y0) { (x0, x1) = (x1, x0); (y0, y1) = (y1, y0); dx = -dx; dy = -dy; }
                int ay = (int)MathF.Floor(y0), by = (int)MathF.Floor(y1);
                for (int y = ay; y <= by; y++) {
                    float xa = x0 + dx * (y - y0) / dy, xb = x0 + dx * (y + 1 - y0) / dy;
                    if (y == ay) { xa = x0; }
                    if (y == by) { xb = x1; }
                    float left = MathF.Min(xa, xb), right = MathF.Max(xa, xb);
                    c.Fill(new Box(MathF.Floor(left), y, MathF.Max(t, MathF.Floor(right) - MathF.Floor(left) + 1), 1), color);
                }
            }
        }

        public static void Ring(ICanvas c, float cx, float cy, float r, Rgba color, float t = 1) {
            int n = (int)MathF.Ceiling(r);
            for (int dy = -n; dy <= n; dy++) {
                float y = dy + .5f;
                float outer = MathF.Sqrt(MathF.Max(0, r * r - y * y)), inner = r - t > MathF.Abs(y) ? MathF.Sqrt((r - t) * (r - t) - y * y) : 0;
                if (outer <= 0) { continue; }
                if (inner <= 0) { c.Fill(new Box(MathF.Round(cx - outer), MathF.Floor(cy + dy), MathF.Round(outer * 2), 1), color); continue; }
                c.Fill(new Box(MathF.Round(cx - outer), MathF.Floor(cy + dy), MathF.Max(1, outer - inner), 1), color);
                c.Fill(new Box(MathF.Round(cx + inner), MathF.Floor(cy + dy), MathF.Max(1, outer - inner), 1), color);
            }
        }

        public static void Dots(ICanvas c, float x0, float y, float x1, Rgba color, float on = 2, float off = 4) {
            for (float x = x0; x < x1; x += on + off) { c.Fill(new Box(MathF.Floor(x), MathF.Floor(y), MathF.Min(on, x1 - x), 1), color); }
        }

        public static void DotsVertical(ICanvas c, float x, float y0, float y1, Rgba color, float on = 2, float off = 4) {
            for (float y = y0; y < y1; y += on + off) { c.Fill(new Box(MathF.Floor(x), MathF.Floor(y), 1, MathF.Min(on, y1 - y)), color); }
        }

        public static void Hatch(ICanvas c, in Box box, Rgba color, float pitch = 5) {
            c.PushClip(box);
            for (float x = box.X - box.H; x < box.Right; x += pitch) { Line(c, x, box.Bottom, x + box.H, box.Y, color); }
            c.PopClip();
        }

        public static Box Section(ICanvas c, XpFaces f, in Box box, string title) {
            float w = Paint.Measure(f.Bold, title);
            Paint.Text(c, f.Bold, title, new Box(box.X, box.Y, w, 17), MeteoLook.Accent, Align.Left);
            c.Fill(new Box(box.X + w + 6, box.Y + 8, MathF.Max(0, box.W - w - 6), 1), MeteoLook.Rule);
            return new Box(box.X, box.Y + 21, box.W, box.H - 21);
        }

        public static void Sheet(ICanvas c, in Box box) {
            c.Fill(box, MeteoLook.Plot);
            Paint.Frame(c, box, 1, XpLook.InputBorder);
        }

        public static void Cross(ICanvas c, float x, float y, float r, Rgba color) {
            c.Fill(new Box(MathF.Round(x - r), MathF.Round(y), r * 2 + 1, 1), color);
            c.Fill(new Box(MathF.Round(x), MathF.Round(y - r), 1, r * 2 + 1), color);
        }

        public static void Arrow(ICanvas c, float x0, float y0, float x1, float y1, Rgba color, float head = 9) {
            Line(c, x0, y0, x1, y1, color, 2);
            float a = MathF.Atan2(y1 - y0, x1 - x0);
            for (int s = -1; s <= 1; s += 2) {
                float b = a + MathF.PI - s * .42f;
                Line(c, x1, y1, x1 + MathF.Cos(b) * head, y1 + MathF.Sin(b) * head, color, 2);
            }
        }
    }
}
