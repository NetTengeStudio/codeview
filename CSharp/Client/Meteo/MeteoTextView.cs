using System;
using ScpItdMod.Ui;
using Draw = ScpItdMod.Ui.Paint;

namespace ScpItdMod.Meteo {
    public sealed partial class MeteoScreen {
        private sealed class TextBlock {
            private string text = "";
            private IFace face;
            private float width;
            private bool dirty = true;
            private int[] starts = new int[16], lengths = new int[16];
            public int Count { get; private set; }
            public float Height => Count * 15;
            public void Set(string value) { if (text == value) { return; } text = value ?? ""; dirty = true; }
            public void Layout(IFace target, float available) {
                if (!dirty && ReferenceEquals(face, target) && width == available) { return; }
                dirty = false; face = target; width = available; Count = 0;
                int offset = 0;
                while (offset < text.Length) {
                    ReadOnlySpan<char> remaining = text.AsSpan(offset);
                    int newline = remaining.IndexOf('\n');
                    ReadOnlySpan<char> paragraph = newline >= 0 ? remaining.Slice(0, newline) : remaining;
                    int fit = Draw.Fit(face, paragraph, MathF.Max(1, width));
                    int consume = fit;
                    if (fit < paragraph.Length) {
                        int space = paragraph.Slice(0, fit).LastIndexOf(' ');
                        if (space > 0) { fit = space; consume = space + 1; }
                    } else { consume = paragraph.Length + (newline >= 0 ? 1 : 0); }
                    if (consume == 0) { int next = 0; Draw.Next(remaining, ref next); fit = consume = next + 1; }
                    if (Count == starts.Length) { Array.Resize(ref starts, starts.Length * 2); Array.Resize(ref lengths, lengths.Length * 2); }
                    starts[Count] = offset; lengths[Count++] = fit; offset += consume;
                }
            }
            public void Paint(ICanvas c, IFace font, in Box box, Rgba ink, float scroll = 0) {
                Layout(font, box.W);
                c.PushClip(box);
                for (int line = 0; line < Count; line++) {
                    float y = box.Y + line * 15 - scroll;
                    if (y + 15 < box.Y || y >= box.Bottom) { continue; }
                    Draw.Text(c, font, text.AsSpan(starts[line], lengths[line]), new Box(box.X, y, box.W, 15), ink, Align.Left);
                }
                c.PopClip();
            }
        }
    }
}
