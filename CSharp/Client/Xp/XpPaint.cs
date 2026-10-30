using System;
using ScpItdMod.Ui;

namespace ScpItdMod.Xp {
    public enum CaptionState : byte { Normal, Hover, Down }

    public static class XpPaint {
        private static readonly System.Collections.Generic.Dictionary<(string, int), string> numbers = new System.Collections.Generic.Dictionary<(string, int), string>();

        public static string Num(string prefix, int value) {
            if (numbers.TryGetValue((prefix, value), out string s)) { return s; }
            if (numbers.Count > 2048) { numbers.Clear(); }
            s = prefix + value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            numbers[(prefix, value)] = s;
            return s;
        }

        private static readonly byte[] corner = BuildCorner(XpLook.CornerR);

        private static byte[] BuildCorner(int r) {
            var inset = new byte[r];
            for (int row = 0; row < r; row++) {
                float dy = r - row - 0.5f;
                float dx = r - MathF.Sqrt(MathF.Max(0.0f, r * r - dy * dy));
                inset[row] = (byte)Math.Clamp((int)MathF.Round(dx), 0, r);
            }
            return inset;
        }

        public static void Text(ICanvas c, IFace face, string text, float x, float y, Rgba ink) => Paint.Text(c, face, text, MathF.Round(x), MathF.Round(y), ink);

        public static void Shadowed(ICanvas c, IFace face, string text, in Box b, Rgba ink, Rgba shadow, Align align = Align.Left) {
            Paint.Clipped(c, face, text, b.Offset(1, 1), shadow, align);
            Paint.Clipped(c, face, text, b, ink, align);
        }

        public static void Bevel(ICanvas c, in Box b, Rgba lightOuter, Rgba darkOuter, Rgba lightInner, Rgba darkInner) {
            c.Fill(new Box(b.X, b.Y, b.W, 1), lightOuter);
            c.Fill(new Box(b.X, b.Y, 1, b.H), lightOuter);
            c.Fill(new Box(b.X, b.Bottom - 1, b.W, 1), darkOuter);
            c.Fill(new Box(b.Right - 1, b.Y, 1, b.H), darkOuter);
            c.Fill(new Box(b.X + 1, b.Y + 1, b.W - 2, 1), lightInner);
            c.Fill(new Box(b.X + 1, b.Y + 1, 1, b.H - 2), lightInner);
            c.Fill(new Box(b.X + 1, b.Bottom - 2, b.W - 2, 1), darkInner);
            c.Fill(new Box(b.Right - 2, b.Y + 1, 1, b.H - 2), darkInner);
        }

        public static void Sunken(ICanvas c, in Box b, Rgba fill) {
            c.Fill(b, fill);
            Paint.Frame(c, b, 1, XpLook.InputBorder);
        }

        public static void Window(ICanvas c, XpFaces f, in Box frame, string title, string icon, bool active, bool maximized, bool closeOnly, bool resizable, bool minimizable, int hotButton, bool pressed) {
            Box b = frame.Snap();
            Ramp ramp = active ? XpLook.TitleActive : XpLook.TitleInactive;
            int th = XpLook.TitleH;
            for (int row = 0; row < th; row++) {
                float inset = maximized || row >= corner.Length ? 0 : corner[row];
                Rgba color = row == 0 ? (active ? XpLook.FrameOuterLight : XpLook.FrameInactiveOuter) : ramp.Sample(row / (float)(th - 1));
                c.Fill(new Box(b.X + inset, b.Y + row, b.W - inset * 2, 1), color);
            }
            Rgba ol = active ? XpLook.FrameOuterLight : XpLook.FrameInactiveOuter;
            Rgba od = active ? XpLook.FrameOuterDark : XpLook.FrameInactiveOuter;
            Rgba ml = active ? XpLook.FrameMidLight : XpLook.FrameInactiveMid;
            Rgba md = active ? XpLook.FrameMidDark : XpLook.FrameInactiveMid;
            Rgba il = active ? XpLook.FrameInnerLight : XpLook.FrameInactiveInner;
            Rgba id = active ? XpLook.FrameInnerDark : XpLook.FrameInactiveInner;
            float y0 = b.Y + th;
            float h = b.H - th;
            c.Fill(new Box(b.X, y0, 1, h), ol);
            c.Fill(new Box(b.X + 1, y0, 1, h - 1), ml);
            c.Fill(new Box(b.X + 2, y0, 1, h - 2), il);
            c.Fill(new Box(b.Right - 1, y0, 1, h), od);
            c.Fill(new Box(b.Right - 2, y0, 1, h - 1), md);
            c.Fill(new Box(b.Right - 3, y0, 1, h - 2), id);
            c.Fill(new Box(b.X, b.Bottom - 1, b.W, 1), od);
            c.Fill(new Box(b.X + 1, b.Bottom - 2, b.W - 2, 1), md);
            c.Fill(new Box(b.X + 2, b.Bottom - 3, b.W - 4, 1), id);
            c.Fill(new Box(b.X + 3, y0, b.W - 6, h - 3), XpLook.Surface);
            float tx = b.X + 6;
            if (!string.IsNullOrEmpty(icon)) {
                c.Image(XpLook.Image(icon), new Box(b.X + 6, b.Y + 7, 16, 16), XpLook.White);
                tx = b.X + 26;
            }
            int buttons = closeOnly ? 1 : resizable ? 3 : minimizable ? 2 : 1;
            float textRight = b.Right - XpLook.CaptionRight - buttons * (XpLook.CaptionBtn + XpLook.CaptionGap) - 4;
            var tb = new Box(tx, b.Y + 1, textRight - tx, th - 2);
            if (active) {
                Shadowed(c, f.Title, title, tb, XpLook.White, XpLook.TitleShadow);
            } else {
                Paint.Clipped(c, f.Title, title, tb, XpLook.TitleInactiveText, Align.Left);
            }
            Caption(c, XpDesk.CaptionButton(b, 0), "close", hotButton == 0, pressed && hotButton == 0, active);
            if (!closeOnly && resizable) { Caption(c, XpDesk.CaptionButton(b, 1), maximized ? "restore" : "maximize", hotButton == 1, pressed && hotButton == 1, active); }
            if (!closeOnly && minimizable) {
                int idx = resizable ? 2 : 1;
                Caption(c, XpDesk.CaptionButton(b, idx), "minimize", hotButton == idx, pressed && hotButton == idx, active);
            }
        }

        private static void Caption(ICanvas c, in Box b, string name, bool hover, bool down, bool active) {
            string key = down ? Suffix(name, "-active") : hover ? Suffix(name, "-hover") : name;
            c.Image(XpLook.Image(key), b, active ? XpLook.White : new Rgba(255, 255, 255, 200));
        }

        private static readonly System.Collections.Generic.Dictionary<(string, string), string> suffixed = new System.Collections.Generic.Dictionary<(string, string), string>();

        private static string Suffix(string name, string suffix) {
            if (!suffixed.TryGetValue((name, suffix), out string s)) {
                s = name + suffix;
                suffixed[(name, suffix)] = s;
            }
            return s;
        }

        public static bool Button(ICanvas c, UiState ui, XpFaces f, int id, in Box box, string label, bool enabled, bool isDefault = false) {
            Box b = box.Snap();
            bool over = enabled && ui.Over(id, b);
            bool clicked = enabled && ui.Click(id, b);
            bool held = over && ui.Active == id && ui.In.Down;
            ButtonFace(c, b, enabled, over, held, isDefault);
            Rgba ink = enabled ? XpLook.Text : XpLook.Disabled;
            Paint.Clipped(c, f.Body, label, b.Inset(4, held ? 1 : 0, 4, held ? -1 : 0), ink, Align.Center);
            return clicked;
        }

        public static void ButtonFace(ICanvas c, in Box b, bool enabled, bool over, bool held, bool focused) {
            c.Shade(b.Inset(1), held ? XpLook.ButtonDown : XpLook.ButtonFace, true, enabled ? 1.0f : 0.6f);
            Rgba border = enabled ? XpLook.ButtonBorder : XpLook.ButtonBorderOff;
            c.Fill(new Box(b.X + 1, b.Y, b.W - 2, 1), border);
            c.Fill(new Box(b.X + 1, b.Bottom - 1, b.W - 2, 1), border);
            c.Fill(new Box(b.X, b.Y + 1, 1, b.H - 2), border);
            c.Fill(new Box(b.Right - 1, b.Y + 1, 1, b.H - 2), border);
            if (!enabled || held) { return; }
            if (over) {
                c.Fill(new Box(b.X + 1, b.Y + 1, b.W - 2, 1), XpLook.HoverA);
                c.Fill(new Box(b.X + 1, b.Y + 2, b.W - 2, 1), XpLook.HoverB);
                c.Fill(new Box(b.X + 1, b.Bottom - 2, b.W - 2, 1), XpLook.HoverD);
                c.Fill(new Box(b.X + 1, b.Y + 2, 1, b.H - 4), XpLook.HoverC);
                c.Fill(new Box(b.Right - 2, b.Y + 2, 1, b.H - 4), XpLook.HoverC);
            } else if (focused) {
                c.Fill(new Box(b.X + 1, b.Y + 1, b.W - 2, 1), XpLook.FocusA);
                c.Fill(new Box(b.X + 1, b.Y + 2, b.W - 2, 1), XpLook.FocusC);
                c.Fill(new Box(b.X + 1, b.Bottom - 2, b.W - 2, 1), XpLook.FocusD);
                c.Fill(new Box(b.X + 1, b.Y + 2, 1, b.H - 4), XpLook.FocusB);
                c.Fill(new Box(b.Right - 2, b.Y + 2, 1, b.H - 4), XpLook.FocusB);
            }
        }

        public static void Field(ICanvas c, XpFaces f, in Box b, string text, bool enabled) {
            Sunken(c, b, enabled ? XpLook.White : XpLook.Surface);
            Paint.Clipped(c, f.Body, text, b.Inset(3, 0, 3, 0), enabled ? XpLook.Text : XpLook.Disabled, Align.Left);
        }

        public static void Label(ICanvas c, XpFaces f, string text, in Box b, Rgba? ink = null, Align align = Align.Left) =>
            Paint.Clipped(c, f.Body, text, b, ink ?? XpLook.Text, align);

        public static bool Row(ICanvas c, UiState ui, XpFaces f, int id, in Box b, string icon, string text, string right, bool selected, bool enabled = true, Box? viewport = null) {
            Box hit = viewport.HasValue ? Box.Intersect(b, viewport.Value) : b;
            bool clicked = ui.Click(id, hit);
            if (selected) { c.Fill(b, XpLook.Select); }
            Rgba ink = selected ? XpLook.SelectText : enabled ? XpLook.Text : XpLook.Disabled;
            float x = b.X + 2;
            if (!string.IsNullOrEmpty(icon)) {
                c.Image(XpLook.Image(icon), new Box(x, b.Y + MathF.Floor((b.H - 16) * 0.5f), 16, 16), XpLook.White);
                x += 20;
            }
            float rw = 0;
            if (!string.IsNullOrEmpty(right)) {
                rw = MathF.Min(b.W * 0.45f, Paint.Measure(f.Body, right) + 8);
                Paint.Clipped(c, f.Body, right, new Box(b.Right - rw, b.Y, rw - 4, b.H), ink, Align.Right);
            }
            Paint.Clipped(c, f.Body, text, new Box(x, b.Y, b.Right - x - rw - 2, b.H), ink, Align.Left);
            return clicked && enabled;
        }

        public static Box Scroll(ICanvas c, UiState ui, int id, in Box area, float content, ref float scroll) {
            float view = area.H;
            float max = MathF.Max(0, content - view);
            if (area.Contains(ui.In.X, ui.In.Y) && ui.In.Wheel != 0) { scroll -= ui.In.Wheel * XpLook.RowH * 3; }
            scroll = Math.Clamp(scroll, 0, max);
            if (content <= view) { return area; }
            Box rows = area.CutRight(XpLook.ScrollW);
            var track = area.TakeRight(XpLook.ScrollW);
            var up = track.TakeTop(XpLook.ScrollW);
            var down = track.TakeBottom(XpLook.ScrollW);
            var lane = new Box(track.X, up.Bottom, track.W, track.H - XpLook.ScrollW * 2);
            c.Shade(lane, XpLook.ScrollTrack, false, 1.0f);
            if (ui.Click(id + 1, up)) { scroll = MathF.Max(0, scroll - XpLook.RowH); }
            if (ui.Click(id + 2, down)) { scroll = MathF.Min(max, scroll + XpLook.RowH); }
            c.Image(XpLook.Image("scroll-arrow-up"), up, XpLook.White);
            c.Image(XpLook.Image("scroll-arrow-down"), down, XpLook.White);
            float thumbH = MathF.Max(17, lane.H * view / content);
            float y = lane.Y + (max <= 0 ? 0 : scroll / max * (lane.H - thumbH));
            var thumb = new Box(lane.X, MathF.Round(y), lane.W, MathF.Round(thumbH));
            if (ui.Over(id + 3, lane) && ui.In.Down && (ui.Active == id + 3 || ui.In.Pressed)) {
                ui.Active = id + 3;
                float k = (ui.In.Y - lane.Y - thumbH * 0.5f) / MathF.Max(1, lane.H - thumbH);
                scroll = Math.Clamp(k * max, 0, max);
            }
            c.Fill(thumb.Inset(1), XpLook.ScrollThumb);
            Paint.Frame(c, thumb, 1, XpLook.White);
            c.Fill(new Box(thumb.Right - 4, thumb.Y + 1, 3, thumb.H - 2), XpLook.ScrollThumbEdge);
            c.Image(XpLook.Image("scroll-thumb"), new Box(MathF.Round(thumb.CenterX - 4), MathF.Round(thumb.CenterY - 4), 7, 8), XpLook.White);
            return rows;
        }

        public static int Menu(ICanvas c, UiState ui, XpFaces f, int id, in Box bar, string[] items, int open) {
            c.Fill(bar, XpLook.Surface);
            float x = bar.X + 1;
            int clicked = -1;
            for (int i = 0; i < items.Length; i++) {
                float w = Paint.Measure(f.Body, items[i]) + 14;
                var b = new Box(x, bar.Y, w, bar.H);
                bool over = ui.Over(id + i, b);
                if (open == i || over && open >= 0) {
                    c.Fill(b, XpLook.Select);
                    Paint.Text(c, f.Body, items[i], b, XpLook.White, Align.Center);
                } else {
                    if (over) { Paint.Frame(c, b, 1, XpLook.Shadow); }
                    Paint.Text(c, f.Body, items[i], b, XpLook.Text, Align.Center);
                }
                if (ui.Click(id + i, b)) { clicked = i; }
                x += w;
            }
            return clicked;
        }

        public static float MenuX(XpFaces f, in Box bar, string[] items, int index) {
            float x = bar.X + 1;
            for (int i = 0; i < index; i++) { x += Paint.Measure(f.Body, items[i]) + 14; }
            return x;
        }

        public static int Popup(ICanvas c, UiState ui, XpFaces f, int id, float x, float y, string[] items, bool[] enabled, string[] keys) {
            float w = 0;
            for (int i = 0; i < items.Length; i++) {
                float k = keys != null && i < keys.Length && keys[i] != null ? Paint.Measure(f.Body, keys[i]) + 24 : 0;
                w = MathF.Max(w, Paint.Measure(f.Body, items[i]) + 48 + k);
            }
            w = MathF.Max(w, 140);
            float h = 4;
            for (int i = 0; i < items.Length; i++) { h += items[i].Length == 0 ? 9 : 22; }
            var box = new Box(MathF.Round(x), MathF.Round(y), MathF.Round(w), h);
            c.Fill(box.Offset(2, 2), new Rgba(0, 0, 0, 70));
            c.Fill(box, XpLook.White);
            Paint.Frame(c, box, 1, XpLook.Shadow);
            ui.Block(box);
            int clicked = -1;
            float yy = box.Y + 2;
            for (int i = 0; i < items.Length; i++) {
                if (items[i].Length == 0) {
                    c.Fill(new Box(box.X + 2, yy + 4, box.W - 4, 1), XpLook.Separator);
                    yy += 9;
                    continue;
                }
                var row = new Box(box.X + 2, yy, box.W - 4, 22);
                bool on = enabled == null || i >= enabled.Length || enabled[i];
                bool over = on && row.Contains(ui.In.X, ui.In.Y);
                if (over) { c.Fill(row, XpLook.Select); }
                Rgba ink = !on ? XpLook.Disabled : over ? XpLook.White : XpLook.Text;
                Paint.Text(c, f.Body, items[i], new Box(row.X + 24, row.Y, row.W - 30, row.H), ink, Align.Left);
                if (keys != null && i < keys.Length && keys[i] != null) { Paint.Text(c, f.Body, keys[i], new Box(row.X, row.Y, row.W - 10, row.H), ink, Align.Right); }
                if (on && over && ui.In.Released) { clicked = i; }
                yy += 22;
            }
            return clicked;
        }

        public static Box PopupBox(XpFaces f, float x, float y, string[] items, string[] keys) {
            float w = 140;
            float h = 4;
            for (int i = 0; i < items.Length; i++) {
                float k = keys != null && i < keys.Length && keys[i] != null ? Paint.Measure(f.Body, keys[i]) + 24 : 0;
                w = MathF.Max(w, Paint.Measure(f.Body, items[i]) + 48 + k);
                h += items[i].Length == 0 ? 9 : 22;
            }
            return new Box(MathF.Round(x), MathF.Round(y), MathF.Round(w), h);
        }

        public static int Tabs(ICanvas c, UiState ui, XpFaces f, int id, in Box strip, string[] labels, int selected) {
            float x = strip.X + 2;
            int result = selected;
            c.Fill(new Box(strip.X, strip.Bottom - 1, strip.W, 1), XpLook.TabBorder);
            for (int i = 0; i < labels.Length; i++) {
                float w = Paint.Measure(f.Body, labels[i]) + 16;
                bool sel = i == selected;
                var b = new Box(x, strip.Y + (sel ? 0 : 2), w, strip.H - (sel ? 0 : 2));
                bool over = ui.Over(id + i, b);
                c.Shade(b, XpLook.TabFace, true, 1.0f);
                Paint.Frame(c, new Box(b.X, b.Y, b.W, b.H + (sel ? 1 : 0)), 1, XpLook.TabBorder);
                if (sel) { c.Fill(new Box(b.X + 1, b.Bottom - 1, b.W - 2, 2), XpLook.TabPage); }
                if (sel || over) {
                    c.Fill(new Box(b.X, b.Y, b.W, 1), XpLook.TabHot);
                    c.Fill(new Box(b.X + 1, b.Y + 1, b.W - 2, 2), XpLook.TabHotLight);
                }
                Paint.Text(c, f.Body, labels[i], b, XpLook.Text, Align.Center);
                if (ui.Click(id + i, b)) { result = i; }
                x += w - 1;
            }
            return result;
        }

        public static void Status(ICanvas c, XpFaces f, in Box b, string left, string right) {
            c.Fill(b, XpLook.Surface);
            c.Fill(new Box(b.X, b.Y, b.W, 1), XpLook.Separator);
            Paint.Clipped(c, f.Body, left, b.Inset(4, 1, 4, 0), XpLook.Text, Align.Left);
            if (!string.IsNullOrEmpty(right)) {
                float w = Paint.Measure(f.Body, right) + 12;
                c.Fill(new Box(b.Right - w - 2, b.Y + 3, 1, b.H - 6), XpLook.Separator);
                Paint.Clipped(c, f.Body, right, new Box(b.Right - w, b.Y + 1, w - 4, b.H - 1), XpLook.Text, Align.Left);
            }
        }

        public static void Progress(ICanvas c, in Box b, float value) {
            Sunken(c, b, XpLook.White);
            float v = Math.Clamp(value, 0, 1);
            Box inner = b.Inset(2);
            int blocks = (int)(inner.W * v / 10);
            for (int i = 0; i < blocks; i++) {
                var blk = new Box(inner.X + i * 10, inner.Y, 8, inner.H);
                c.Fill(blk, XpLook.Progress);
                c.Fill(new Box(blk.X, blk.Y + inner.H * 0.3f, blk.W, inner.H * 0.25f), XpLook.ProgressLight);
            }
        }

        public static void Group(ICanvas c, XpFaces f, in Box b, string title) {
            Paint.Frame(c, new Box(b.X, b.Y + 6, b.W, b.H - 6), 1, Rgba.Hex(0xD0D0BF));
            float w = Paint.Measure(f.Body, title) + 6;
            c.Fill(new Box(b.X + 6, b.Y, w, 13), XpLook.Surface);
            Paint.Text(c, f.Body, title, new Box(b.X + 9, b.Y, w, 13), XpLook.GroupHeader, Align.Left);
        }
    }
}
