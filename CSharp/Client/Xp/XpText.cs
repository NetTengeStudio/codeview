using System;
using ScpItdMod.Paper;
using ScpItdMod.Pc;
using ScpItdMod.Ui;

namespace ScpItdMod.Xp {
    public sealed class FaceGlyphs : IGlyphs {
        public readonly IFace Face;
        private readonly bool masked;

        public FaceGlyphs(IFace face, bool masked = false) { Face = face; this.masked = masked; }

        public float Advance(char c) => Face.Advance(masked ? '*' : c);
    }

    public sealed class XpTextBox {
        public const float Pad = 3;

        private InkEdit edit;
        private float scroll;
        private bool dragging;
        private IFace layoutFace;
        private FaceGlyphs glyphs;

        public readonly bool Multiline;
        public readonly int MaxChars;
        public readonly int MaxLines;
        public readonly bool Masked;
        public bool ReadOnly;
        public int SavedVersion;

        public XpTextBox(bool multiline, int maxChars, int maxLines, bool masked = false) {
            Multiline = multiline;
            MaxChars = maxChars;
            MaxLines = multiline ? maxLines : 1;
            Masked = masked;
        }

        public InkEdit Edit => edit;

        public string Text => edit?.Text ?? pending ?? "";

        private string pending = "";

        public bool Dirty => edit != null && edit.Version != SavedVersion;

        public void Load(string text) {
            pending = text ?? "";
            if (edit != null) {
                edit.Load(pending);
                SavedVersion = edit.Version;
            }
        }

        public void MarkSaved() {
            if (edit != null) { SavedVersion = edit.Version; }
        }

        private void Ensure(IFace face, float width) {
            float w = Multiline ? MathF.Max(20, width) : float.MaxValue;
            bool changedFace = !ReferenceEquals(layoutFace, face);
            if (edit != null && !changedFace && MathF.Abs(edit.Width - w) < 0.5f) { return; }
            if (changedFace || glyphs == null) { glyphs = new FaceGlyphs(face, Masked); layoutFace = face; }
            if (edit == null) {
                edit = new InkEdit(glyphs, w, MaxLines, MaxChars); edit.Load(pending); SavedVersion = edit.Version;
            } else { edit.Reflow(glyphs, w); }
        }

        public void Paint(ICanvas c, UiState ui, int id, IFace face, in Box box, bool focused, double now, out bool clicked) {
            XpPaint.Sunken(c, box, ReadOnly ? XpLook.Surface : XpLook.White);
            Box inner = box.Inset(1);
            float lh = face.LineHeight;
            Box area = Multiline ? inner.CutRight(XpLook.ScrollW) : inner;
            Ensure(face, area.W - Pad * 2);
            InkLayout layout = edit.Layout;
            float content = layout.Lines * lh + Pad * 2;
            if (Multiline) {
                float before = scroll;
                XpPaint.Scroll(c, ui, id + 1, inner, MathF.Max(content, inner.H + 1), ref scroll);
                if (content <= inner.H) { scroll = 0; }
                _ = before;
            }
            clicked = false;
            bool over = ui.Over(id, area);
            if (over && ui.In.Pressed) {
                ui.Active = id;
                dragging = true;
                clicked = true;
                Hit(area, ui.In.X, ui.In.Y, lh, false);
            } else if (dragging && ui.In.Down && ui.Active == id) {
                Hit(area, ui.In.X, ui.In.Y, lh, true);
            }
            if (!ui.In.Down) { dragging = false; }
            if (focused && Multiline) { Follow(area, lh); }
            c.PushClip(area);
            int first = Math.Max(0, (int)(scroll / lh) - 1);
            int last = Math.Min(layout.Lines - 1, (int)((scroll + area.H) / lh) + 1);
            ReadOnlySpan<char> text = edit.Span;
            float baseX = area.X + Pad;
            float hscroll = 0;
            if (!Multiline) {
                float caretX = layout.X(Math.Min(edit.Caret, layout.Count));
                hscroll = MathF.Max(0, caretX - (area.W - Pad * 2 - 4));
            }
            int selA = edit.SelectionStart;
            int selB = edit.SelectionEnd;
            for (int row = first; row <= last; row++) {
                int s = layout.Start(row);
                int e = Math.Min(layout.End(row), text.Length);
                float y = MathF.Round(area.Y + Pad + row * lh - scroll);
                if (edit.HasSelection && selB > s && selA <= e) {
                    int a = Math.Max(selA, s);
                    int b = Math.Min(selB, e);
                    float x0 = layout.X(a);
                    float x1 = b > a ? layout.X(b) : x0 + 4;
                    if (selB > e && row < layout.Lines - 1) { x1 += 4; }
                    c.Fill(new Box(baseX + x0 - hscroll, y, MathF.Max(2, x1 - x0), lh), focused ? XpLook.Select : XpLook.Disabled);
                }
                int len = e - s;
                if (len > 0 && text[s + len - 1] == '\n') { len--; }
                if (len > 0) {
                    ReadOnlySpan<char> line = text.Slice(s, len);
                    if (!Masked && (!edit.HasSelection || selB <= s || selA >= s + len)) {
                        Ui.Paint.Text(c, face, line, baseX - hscroll, y, ReadOnly ? XpLook.Disabled : XpLook.Text);
                    } else {
                        float pen = baseX - hscroll;
                        for (int i = 0; i < len; i++) {
                            int at = s + i;
                            bool sel = at >= selA && at < selB;
                            char ch = Masked ? '*' : text[at];
                            if (ch != ' ') { c.Glyph(face, ch, MathF.Round(pen), y, sel && focused ? XpLook.White : XpLook.Text); }
                            pen += face.Advance(ch);
                        }
                    }
                }
            }
            if (focused && !ReadOnly && ((int)(now * 1.9) & 1) == 0) {
                int caret = Math.Min(edit.Caret, layout.Count);
                int row = layout.Line(caret);
                float x = MathF.Round(baseX + layout.X(caret) - hscroll);
                float y = MathF.Round(area.Y + Pad + row * lh - scroll);
                c.Fill(new Box(x, y, 1, lh), XpLook.Black);
            }
            c.PopClip();
        }

        private void Hit(in Box area, float x, float y, float lh, bool extend) {
            InkLayout layout = edit.Layout;
            int row = Math.Clamp((int)((y - area.Y - Pad + scroll) / lh), 0, Math.Max(0, layout.Lines - 1));
            float horizontal = Multiline ? 0 : MathF.Max(0, layout.X(Math.Min(edit.Caret, layout.Count)) - (area.W - Pad * 2 - 4));
            edit.Point(row, x - area.X - Pad + horizontal, extend);
        }

        private void Follow(in Box area, float lh) {
            int row = edit.Layout.Line(Math.Min(edit.Caret, edit.Layout.Count));
            float top = row * lh;
            float bottom = top + lh + Pad * 2;
            if (top < scroll) { scroll = top; }
            if (bottom > scroll + area.H) { scroll = bottom - area.H; }
        }

        private int lineVersion = -1;
        private int lineCaret = -1;
        private string lineText = "Стр 1, стлб 1";

        public string Line() {
            if (edit == null) { return "Стр 1, стлб 1"; }
            if (edit.Version == lineVersion && edit.Caret == lineCaret) { return lineText; }
            lineVersion = edit.Version;
            lineCaret = edit.Caret;
            int caret = Math.Min(edit.Caret, edit.Layout.Count);
            int row = 1;
            int col = 1;
            ReadOnlySpan<char> t = edit.Span;
            for (int i = 0; i < caret && i < t.Length; i++) {
                if (t[i] == '\n') {
                    row++;
                    col = 1;
                } else {
                    col++;
                }
            }
            lineText = $"Стр {row}, стлб {col}";
            return lineText;
        }
    }

    public sealed class XpPrompt : IXpApp, IXpTyping {
        private readonly string label;
        private readonly Action<string> done;
        private readonly XpTextBox box;
        private bool answered;

        public XpPrompt(string title, string label, string initial, int maxChars, Action<string> done) {
            Title = title;
            this.label = label;
            this.done = done;
            box = new XpTextBox(false, maxChars, 1);
            box.Load(initial ?? "");
        }

        public string Title { get; }

        public string Icon => null;

        public InkEdit Edit => box.Edit;

        public bool Command(XpShell shell, char control) {
            if (control == '\r' || control == '\n') {
                Finish(shell, shell.FocusedWindow, true);
                return true;
            }
            return false;
        }

        public void Paint(ICanvas c, UiState ui, XpFaces f, Box client, XpShell shell, XpWindow self) {
            c.Fill(client, XpLook.Surface);
            XpPaint.Label(c, f, label, new Box(client.X + 12, client.Y + 12, client.W - 24, 14));
            box.Paint(c, ui, 10, f.Body, new Box(client.X + 12, client.Y + 32, client.W - 24, XpLook.FieldH), true, shell.Now, out _);
            var ok = new Box(client.Right - XpLook.ButtonW * 2 - 18, client.Bottom - XpLook.ButtonH - 10, XpLook.ButtonW, XpLook.ButtonH);
            var cancel = new Box(client.Right - XpLook.ButtonW - 12, ok.Y, XpLook.ButtonW, XpLook.ButtonH);
            if (XpPaint.Button(c, ui, f, 20, ok, "OK", true, true)) { Finish(shell, self, true); }
            if (XpPaint.Button(c, ui, f, 21, cancel, "Отмена", true)) { Finish(shell, self, false); }
        }

        private void Finish(XpShell shell, XpWindow self, bool accept) {
            if (answered) { return; }
            answered = true;
            string text = box.Text.Trim();
            shell.Close(self);
            if (accept) { done?.Invoke(text); }
        }

        public bool WantsClose(XpShell shell, XpWindow self) {
            Finish(shell, self, false);
            return false;
        }

        public void Closed(XpShell shell) { }
    }

    public sealed class XpPrintDialog : IXpApp {
        private readonly ulong file;
        private readonly uint rev;
        private readonly string name;
        private int selected;
        private uint token;
        private string status = "";
        private float printerScroll;

        public XpPrintDialog(ulong file, uint rev, string name) {
            this.file = file;
            this.rev = rev;
            this.name = name;
        }

        public string Title => "Печать";

        public string Icon => null;

        public void Paint(ICanvas c, UiState ui, XpFaces f, Box client, XpShell shell, XpWindow self) {
            c.Fill(client, XpLook.Surface);
            PcSession pc = shell.Pc;
            var group = new Box(client.X + 8, client.Y + 8, client.W - 16, 130);
            XpPaint.Group(c, f, group, "Выберите принтер");
            var list = new Box(group.X + 8, group.Y + 18, group.W - 16, 84);
            XpPaint.Sunken(c, list, XpLook.White);
            Box rows = XpPaint.Scroll(c, ui, 2000, list.Inset(1), pc.Printers.Count * XpLook.RowH, ref printerScroll);
            c.PushClip(rows);
            for (int i = 0; i < pc.Printers.Count; i++) {
                PcPrinter p = pc.Printers[i];
                var row = new Box(rows.X, rows.Y + i * XpLook.RowH - printerScroll, rows.W, XpLook.RowH);
                if (row.Bottom <= rows.Y || row.Y >= rows.Bottom) { continue; }
                if (XpPaint.Row(c, ui, f, 100 + i, row, "printer16", p.Name, State(p.State), i == selected, viewport: rows)) { selected = i; }
            }
            c.PopClip();
            if (pc.Printers.Count == 0) { XpPaint.Label(c, f, "Нет подключённых принтеров.", list.Inset(6, 2, 6, 0), XpLook.Disabled); }
            XpPaint.Label(c, f, $"Документ: {name}, версия {rev}", new Box(group.X + 8, list.Bottom + 4, group.W - 16, 14));
            XpPaint.Label(c, f, status, new Box(client.X + 10, group.Bottom + 6, client.W - 20, 14), XpLook.GroupHeader);
            if (token != 0 && pc.Take(token, out PcResult r)) {
                if (r.Error == PcError.Ok) {
                    token = 0;
                    shell.Close(self);
                    pc.LoadJobs();
                    return;
                }
                status = PcWire.Explain(r.Error);
                token = 0;
            }
            bool can = token == 0 && selected < pc.Printers.Count && pc.Printers[selected].State != PrinterState.Off && pc.Printers[selected].State != PrinterState.Gone;
            var print = new Box(client.Right - XpLook.ButtonW * 2 - 18, client.Bottom - XpLook.ButtonH - 10, XpLook.ButtonW, XpLook.ButtonH);
            var cancel = new Box(client.Right - XpLook.ButtonW - 12, print.Y, XpLook.ButtonW, XpLook.ButtonH);
            if (XpPaint.Button(c, ui, f, 20, print, "Печать", can, true)) {
                token = pc.Print(file, rev, pc.Printers[selected].Id);
                status = "Отправка задания...";
            }
            if (XpPaint.Button(c, ui, f, 21, cancel, "Отмена", true)) { shell.Close(self); }
        }

        public static string State(PrinterState s) => s switch {
            PrinterState.Ready => "Готов",
            PrinterState.Off => "Отключён",
            PrinterState.NoPaper => "Нет бумаги",
            PrinterState.Busy => "Печать",
            _ => "Недоступен"
        };

        public bool WantsClose(XpShell shell, XpWindow self) => true;

        public void Closed(XpShell shell) { }
    }
}
