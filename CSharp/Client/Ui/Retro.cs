using System;

namespace ScpItdMod.Ui {
    public struct UiInput {
        public float X;
        public float Y;
        public bool Down;
        public bool Pressed;
        public bool Released;
        public bool AltPressed;
        public float Wheel;
        public bool Up;
        public bool DownKey;
        public bool Enter;
        public bool Back;
        public double Now;

        public static UiInput None => new UiInput { X = -1e6f, Y = -1e6f };
    }

    public sealed class UiState {
        internal static Action<UiState, int, Box, float> HitObserver;
        public UiInput In;
        public int Hot;
        public int Active;
        public int Focus;
        private int hotNext;

        public void Begin(in UiInput input) {
            In = input;
            Hot = hotNext;
            hotNext = 0;
            if (!In.Down && !In.Released) { Active = 0; }
        }

        public void ResetInput() {
            In = UiInput.None;
            Hot = Active = 0;
            hotNext = 0;
        }

        public bool Over(int id, in Box box) {
            HitObserver?.Invoke(this, id, box, 0);
            if (!box.Contains(In.X, In.Y)) { return false; }
            hotNext = id;
            return Hot == id || Hot == 0;
        }

        public bool Click(int id, in Box box) {
            bool over = Over(id, box);
            if (over && In.Pressed) { Active = id; }
            bool clicked = over && In.Released && Active == id;
            if (In.Released && Active == id) { Active = 0; }
            return clicked;
        }

        public bool Click(int id, in VisualPose pose) {
            HitObserver?.Invoke(this, id, pose.Bounds, pose.Angle);
            bool over = pose.Contains(In.X, In.Y);
            if (over) { hotNext = id; over = Hot == id || Hot == 0; }
            if (over && In.Pressed) { Active = id; }
            bool clicked = over && In.Released && Active == id;
            if (In.Released && Active == id) { Active = 0; }
            return clicked;
        }

        public void Block(in Box box) {
            if (box.Contains(In.X, In.Y)) { hotNext = -1; }
        }

        public bool Blocked => Hot == -1;
    }

    public enum Tone : byte { Normal, Good, Bad, Warn, Dim }

    public sealed class RetroFaces {
        public IFace Body;
        public IFace Small;
        public IFace Head;
    }

    public static class Retro {
        public static readonly Rgba Plate = new Rgba(0, 0, 0, 236);
        public static readonly Rgba PlateSolid = new Rgba(0, 0, 0, 255);
        public static readonly Rgba Edge = new Rgba(105, 105, 105);
        public static readonly Rgba EdgeLit = new Rgba(160, 160, 160);
        public static readonly Rgba Hover = new Rgba(30, 30, 30);
        public static readonly Rgba Pressed = new Rgba(48, 48, 48);
        public static readonly Rgba Rule = new Rgba(60, 60, 60);
        public static readonly Rgba Ink = new Rgba(200, 200, 200);
        public static readonly Rgba Bright = new Rgba(255, 255, 255);
        public static readonly Rgba Muted = new Rgba(125, 125, 125);
        public static readonly Rgba Good = new Rgba(120, 210, 120);
        public static readonly Rgba Bad = new Rgba(243, 109, 109);
        public static readonly Rgba Warn = new Rgba(232, 176, 72);
        public static readonly Rgba Select = new Rgba(232, 176, 72, 255);

        public const float EdgePx = 3.0f;
        public const float GapPx = 1.0f;
        public const float PadPx = 6.0f;
        public const float RowPx = 22.0f;
        public const float TitlePx = 22.0f;
        public const float ButtonPx = 24.0f;

        public static float Px(ICanvas c, float px) => MathF.Max(1.0f, MathF.Round(px * c.Scale));

        public static Rgba Of(Tone tone) => tone switch {
            Tone.Good => Good,
            Tone.Bad => Bad,
            Tone.Warn => Warn,
            Tone.Dim => Muted,
            _ => Ink
        };

        public static void Panel(ICanvas c, in Box b, float alpha) {
            c.Fill(b, Plate.Fade(alpha));
            Paint.Frame(c, b, Px(c, EdgePx), Edge.Fade(alpha));
        }

        public static Box Window(ICanvas c, RetroFaces f, in Box b, string title, float alpha) {
            Panel(c, b, alpha);
            float e = Px(c, EdgePx);
            float th = Px(c, TitlePx);
            var strip = new Box(b.X + e, b.Y + e, b.W - e * 2.0f, th);
            c.Fill(strip, Edge.Fade(alpha));
            Paint.Clipped(c, f.Head, title, strip.Inset(Px(c, PadPx), 0, Px(c, PadPx), 0), PlateSolid.Fade(alpha), Align.Left);
            return new Box(b.X + e, b.Y + e + th, b.W - e * 2.0f, b.H - e * 2.0f - th).Inset(Px(c, PadPx));
        }

        public static bool Button(ICanvas c, UiState ui, RetroFaces f, int id, in Box b, string label, bool enabled, float alpha, bool lit = false) {
            bool over = enabled && ui.Over(id, b);
            bool clicked = enabled && ui.Click(id, b);
            bool held = over && ui.Active == id && ui.In.Down;
            float e = Px(c, EdgePx);
            Rgba edge = lit ? Select : over ? EdgeLit : Edge;
            c.Fill(b, (held ? Pressed : over || lit ? Hover : PlateSolid).Fade(alpha));
            Paint.Frame(c, b, e, (enabled ? edge : Rule).Fade(alpha));
            Rgba ink = !enabled ? Muted : lit ? Select : over ? Bright : Ink;
            Paint.Clipped(c, f.Body, label, b.Inset(e + Px(c, 4.0f), held ? 1.0f : 0.0f, e + Px(c, 4.0f), 0.0f), ink.Fade(alpha), Align.Center);
            return clicked;
        }

        public static bool Row(ICanvas c, UiState ui, RetroFaces f, int id, in Box b, string label, string right, bool selected, bool enabled, float alpha, Rgba? ink = null) {
            bool over = ui.Over(id, b);
            bool clicked = ui.Click(id, b);
            if (selected) {
                c.Fill(b, Hover.Fade(alpha));
                c.Fill(new Box(b.X, b.Y, Px(c, 3.0f), b.H), Select.Fade(alpha));
            } else if (over) {
                c.Fill(b, Hover.Fade(alpha));
            }
            Rgba text = ink ?? (!enabled ? Muted : selected ? Bright : over ? Bright : Ink);
            float pad = Px(c, PadPx);
            float rw = string.IsNullOrEmpty(right) ? 0.0f : Paint.Measure(f.Small, right) + pad;
            Paint.Clipped(c, f.Body, label, new Box(b.X + pad, b.Y, b.W - pad * 2.0f - rw, b.H), text.Fade(alpha), Align.Left);
            if (rw > 0.0f) { Paint.Text(c, f.Small, right, new Box(b.Right - rw, b.Y, rw - pad, b.H), Muted.Fade(alpha), Align.Right); }
            return clicked && enabled;
        }

        public static bool Tab(ICanvas c, UiState ui, RetroFaces f, int id, in Box b, string label, bool selected, float alpha) {
            bool over = ui.Over(id, b);
            bool clicked = ui.Click(id, b);
            float e = Px(c, EdgePx);
            c.Fill(b, (selected ? PlateSolid : over ? Hover : new Rgba(12, 12, 12)).Fade(alpha));
            c.Fill(new Box(b.X, b.Y, b.W, e), (selected ? Select : Edge).Fade(alpha));
            c.Fill(new Box(b.X, b.Y, e, b.H), Edge.Fade(alpha));
            c.Fill(new Box(b.Right - e, b.Y, e, b.H), Edge.Fade(alpha));
            if (!selected) { c.Fill(new Box(b.X, b.Bottom - e, b.W, e), Edge.Fade(alpha)); }
            Paint.Clipped(c, f.Body, label, b.Inset(e + Px(c, 4.0f), e, e + Px(c, 4.0f), 0), (selected ? Bright : over ? Bright : Ink).Fade(alpha), Align.Center);
            return clicked;
        }

        public static Box List(ICanvas c, UiState ui, int id, in Box b, int count, float rowH, ref float scroll, out int first, out int last) {
            float content = count * rowH;
            float bar = Px(c, 8.0f);
            bool overflow = content > b.H;
            Box rows = overflow ? b.CutRight(bar + Px(c, 2.0f)) : b;
            float max = MathF.Max(0.0f, content - b.H);
            if (ui.Over(id, b) && ui.In.Wheel != 0.0f) { scroll -= ui.In.Wheel * 0.25f; }
            if (overflow) {
                var track = b.TakeRight(bar);
                c.Fill(track, Hover);
                float thumbH = MathF.Max(Px(c, 16.0f), track.H * b.H / content);
                var thumbArea = new Box(track.X, track.Y, track.W, track.H);
                if (ui.Over(id + 1, thumbArea) && ui.In.Down) {
                    float k = (ui.In.Y - track.Y - thumbH * 0.5f) / MathF.Max(1.0f, track.H - thumbH);
                    scroll = k * max;
                }
                scroll = scroll < 0.0f ? 0.0f : scroll > max ? max : scroll;
                float y = track.Y + (max <= 0.0f ? 0.0f : scroll / max * (track.H - thumbH));
                c.Fill(new Box(track.X, y, track.W, thumbH), Edge);
            } else {
                scroll = 0.0f;
            }
            first = Math.Max(0, (int)(scroll / rowH));
            last = Math.Min(count - 1, (int)((scroll + b.H) / rowH));
            return rows;
        }

        public static Box RowBox(in Box rows, int index, float rowH, float scroll) =>
            new Box(rows.X, rows.Y + index * rowH - scroll, rows.W, rowH);

        public static float Paragraph(ICanvas c, IFace face, string text, in Box b, Rgba ink, float scroll = 0.0f) {
            if (string.IsNullOrEmpty(text)) { return 0.0f; }
            float y = b.Y - scroll;
            float lh = face.LineHeight;
            int start = 0;
            ReadOnlySpan<char> s = text;
            while (start <= s.Length) {
                int nl = s.Slice(start).IndexOf('\n');
                ReadOnlySpan<char> line = nl < 0 ? s.Slice(start) : s.Slice(start, nl);
                while (true) {
                    int fit = line.Length == 0 ? 0 : Math.Max(1, Paint.Fit(face, line, b.W));
                    if (fit < line.Length) {
                        int space = line.Slice(0, fit).LastIndexOf(' ');
                        if (space > 0) { fit = space + 1; }
                    }
                    if (y + lh > b.Y && y < b.Bottom) { Paint.Text(c, face, line.Slice(0, fit), b.X, y, ink); }
                    y += lh;
                    if (fit >= line.Length) { break; }
                    line = line.Slice(fit);
                }
                if (nl < 0) { break; }
                start += nl + 1;
            }
            return y + scroll - b.Y;
        }

        public static void Divider(ICanvas c, in Box b, float alpha) => c.Fill(new Box(b.X, b.Y, b.W, Px(c, 1.0f)), Rule.Fade(alpha));

        public static void Meter(ICanvas c, in Box b, float value, Rgba color, float alpha) {
            float e = Px(c, 1.0f);
            c.Fill(b, PlateSolid.Fade(alpha));
            Paint.Frame(c, b, e, Edge.Fade(alpha));
            var inner = b.Inset(e * 2.0f);
            float v = value < 0.0f ? 0.0f : value > 1.0f ? 1.0f : value;
            c.Fill(new Box(inner.X, inner.Y, MathF.Round(inner.W * v), inner.H), color.Fade(alpha));
        }

        public static void Key(ICanvas c, RetroFaces f, float x, float y, string key, float alpha, out float width) {
            float pad = Px(c, 4.0f);
            float w = Paint.Measure(f.Small, key) + pad * 2.0f;
            float h = f.Small.LineHeight + Px(c, 2.0f);
            var b = new Box(x, y, w, h);
            c.Fill(b, Hover.Fade(alpha));
            Paint.Frame(c, b, Px(c, 1.0f), Edge.Fade(alpha));
            Paint.Text(c, f.Small, key, b, Bright.Fade(alpha), Align.Center);
            width = w;
        }

        public static float HintWidth(ICanvas c, RetroFaces f, string key, string text) =>
            Paint.Measure(f.Small, key) + Px(c, 4.0f) * 3.0f + Paint.Measure(f.Small, text) + Px(c, 10.0f);

        public static float Hint(ICanvas c, RetroFaces f, float x, float y, string key, string text, float alpha) {
            Key(c, f, x, y, key, alpha, out float kw);
            float gap = Px(c, 4.0f);
            float h = f.Small.LineHeight + Px(c, 2.0f);
            Paint.Text(c, f.Small, text, new Box(x + kw + gap, y, 4096, h), Ink.Fade(alpha), Align.Left);
            return kw + gap + Paint.Measure(f.Small, text) + Px(c, 10.0f);
        }
    }
}
