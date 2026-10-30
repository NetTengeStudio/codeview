using System;
using ScpItdMod.Paper;
using ScpItdMod.Ui;
using ScpItdMod.Xp;

namespace ScpItdMod.Meteo {
    public sealed class RetroField {
        private const float Pad = 5;
        private readonly int max;
        private readonly bool multiline;
        private InkEdit edit;
        private IFace layoutFace;
        private float layoutWidth;
        private FaceGlyphs glyphs;
        private string pending = "";
        private bool dragging;
        public RetroField(int max, bool multiline = false) { this.max = max; this.multiline = multiline; }
        public InkEdit Edit => edit;
        public string Text => edit?.Text ?? pending;
        public void Load(string text) { pending = text ?? ""; edit?.Load(pending); }
        public void Clear() => Load("");

        private void Ensure(IFace face, float width) {
            float target = multiline ? MathF.Max(1, width - Pad * 2) : float.MaxValue;
            if (edit != null && ReferenceEquals(layoutFace, face) && target == layoutWidth) { return; }
            if (!ReferenceEquals(layoutFace, face)) { glyphs = new FaceGlyphs(face); }
            layoutFace = face; layoutWidth = target;
            if (edit == null) { edit = new InkEdit(glyphs, target, multiline ? 64 : 1, max); edit.Load(pending); }
            else { edit.Reflow(glyphs, target); }
        }

        public bool Paint(ICanvas c, UiState ui, int id, IFace face, in Box box, bool focused, double now, string hint) {
            Box area = box.Inset(1);
            Ensure(face, area.W);
            XpPaint.Sunken(c, box, MeteoLook.Plot);
            InkLayout layout = edit.Layout;
            float lineHeight = face.LineHeight;
            float caretX = layout.X(Math.Min(edit.Caret, layout.Count));
            float scrollX = !multiline && focused ? MathF.Max(0, caretX - (area.W - Pad * 2 - 6)) : 0;
            float scrollY = multiline && focused ? MathF.Max(0, layout.Line(Math.Min(edit.Caret, layout.Count)) * lineHeight - (area.H - lineHeight - Pad * 2)) : 0;
            float top = multiline ? area.Y + Pad : MathF.Round(area.Y + (area.H - lineHeight) * .5f);
            bool clicked = false;
            if (ui.Over(id, area) && ui.In.Pressed) { ui.Active = id; dragging = true; clicked = true; Point(area, ui.In.X, ui.In.Y, false, scrollX, scrollY, lineHeight); }
            else if (dragging && ui.In.Down && ui.Active == id) { Point(area, ui.In.X, ui.In.Y, true, scrollX, scrollY, lineHeight); }
            if (!ui.In.Down) { dragging = false; }
            c.PushClip(area);
            ReadOnlySpan<char> text = edit.Span;
            if (text.IsEmpty && !focused && !string.IsNullOrEmpty(hint)) { Ui.Paint.Clipped(c, face, hint, new Box(area.X + Pad, top, area.W - Pad * 2, lineHeight), MeteoLook.Muted, Align.Left); }
            if (edit.HasSelection) {
                for (int row = layout.Line(edit.SelectionStart); row <= layout.Line(edit.SelectionEnd); row++) {
                    int end = layout.End(row);
                    bool wrapped = row + 1 < layout.Lines && end < edit.Length && edit.Span[end] != '\n';
                    int from = Math.Max(edit.SelectionStart, layout.Start(row)), to = Math.Min(edit.SelectionEnd, end + (wrapped ? 1 : 0));
                    if (to <= from) { continue; }
                    float right = wrapped && to > end ? layout.X(end) + face.Advance(edit.Span[end]) : layout.X(to);
                    c.Fill(new Box(area.X + Pad + layout.X(from) - scrollX, top + row * lineHeight - scrollY, MathF.Max(2, right - layout.X(from)), lineHeight), XpLook.Select);
                }
            }
            for (int i = 0; i < text.Length; i++) {
                if (text[i] == ' ' || text[i] == '\n' || text[i] == '\r') { continue; }
                float y = top + layout.Line(i) * lineHeight - scrollY;
                if (y + lineHeight < area.Y || y > area.Bottom) { continue; }
                bool selected = edit.HasSelection && i >= edit.SelectionStart && i < edit.SelectionEnd;
                c.Glyph(face, text[i], MathF.Round(area.X + Pad + layout.X(i) - scrollX), MathF.Round(y), selected ? XpLook.SelectText : MeteoLook.Ink);
            }
            if (focused && ((int)(now * 1.9) & 1) == 0) {
                int at = Math.Min(edit.Caret, layout.Count);
                c.Fill(new Box(MathF.Round(area.X + Pad + layout.X(at) - scrollX), top + layout.Line(at) * lineHeight - scrollY, 1, lineHeight), MeteoLook.Ink);
            }
            c.PopClip();
            return clicked;
        }
        private void Point(in Box area, float x, float y, bool extend, float scrollX, float scrollY, float lineHeight) {
            int row = multiline ? (int)MathF.Floor((y - area.Y - Pad + scrollY) / lineHeight) : 0;
            row = Math.Clamp(row, 0, edit.Layout.Lines - 1);
            float at = x - area.X - Pad + scrollX;
            edit.Point(row, at, extend);
            int end = edit.Layout.End(row);
            if (row + 1 < edit.Layout.Lines && end < edit.Length && edit.Span[end] != '\n' && at > edit.Layout.X(end) + glyphs.Advance(edit.Span[end]) * .5f) { edit.Move(InkMove.Right, extend); }
        }
    }
}
