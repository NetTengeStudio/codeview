using System;
using ScpItdMod.Pc;
using ScpItdMod.Ui;

namespace ScpItdMod.Xp {
    public sealed class Printers : IXpApp, IXpSized, IXpSessionLifecycle {
        private double lastLoad = double.NegativeInfinity;
        private uint selected;
        private uint cancelToken;
        private string notice = "";
        private float printerScroll, jobScroll;

        public string Title => "Принтеры и факсы";

        public string Icon => "printer16";

        public void Size(XpWindow w) { w.Rect = new Box(0, 0, 600, 400); w.MinW = 420; w.MinH = 340; }

        public static string JobText(in PcJob j) => j.State switch {
            JobState.Queued => "В очереди",
            JobState.Printing => $"Печать: стр. {Math.Min(j.Printed + 1, j.Pages)} из {j.Pages}",
            JobState.Done => "Напечатано",
            JobState.Cancelled => "Отменено",
            _ => "Ошибка: " + PcWire.Explain(j.Error)
        };

        public void Paint(ICanvas c, UiState ui, XpFaces f, Box client, XpShell shell, XpWindow self) {
            PcSession pc = shell.Pc;
            if (shell.Now - lastLoad > 1.0) {
                lastLoad = shell.Now;
                pc.LoadJobs();
            }
            if (cancelToken != 0 && pc.Take(cancelToken, out PcResult r)) {
                cancelToken = 0;
                notice = r.Error == PcError.Ok ? "Задание отменено." : PcWire.Explain(r.Error);
            }
            c.Fill(client, XpLook.Surface);
            var status = client.TakeBottom(XpLook.StatusH);
            var body = client.CutBottom(XpLook.StatusH).Inset(8);
            var top = body.TakeTop(96);
            XpPaint.Group(c, f, top, "Подключённые принтеры");
            var printerArea = top.Inset(8, 18, 8, 6);
            int columns = Math.Max(1, (int)((printerArea.W - XpLook.ScrollW) / 110));
            Box printerRows = XpPaint.Scroll(c, ui, 300, printerArea, (pc.Printers.Count + columns - 1) / columns * 64, ref printerScroll);
            c.PushClip(printerRows);
            for (int i = 0; i < pc.Printers.Count; i++) {
                PcPrinter p = pc.Printers[i];
                var cell = new Box(printerRows.X + i % columns * 110, printerRows.Y + i / columns * 64 - printerScroll, 104, 58);
                if (cell.Bottom <= printerRows.Y || cell.Y >= printerRows.Bottom) { continue; }
                c.Image(XpLook.Image("printer32"), new Box(cell.X + 36, cell.Y, 32, 32), p.State == PrinterState.Ready || p.State == PrinterState.Busy ? XpLook.White : new Rgba(255, 255, 255, 110));
                Ui.Paint.Clipped(c, f.Body, p.Name, new Box(cell.X, cell.Y + 33, cell.W, 12), XpLook.Text, Align.Center);
                Ui.Paint.Clipped(c, f.Body, XpPrintDialog.State(p.State), new Box(cell.X, cell.Y + 45, cell.W, 12), p.State == PrinterState.Ready ? XpLook.Shadow : Rgba.Hex(0xB02020), Align.Center);
            }
            c.PopClip();
            if (pc.Printers.Count == 0) { XpPaint.Label(c, f, "К этой станции не подключено ни одного принтера.", new Box(top.X + 12, top.Y + 30, top.W - 24, 14), XpLook.Shadow); }
            var list = new Box(body.X, top.Bottom + 6, body.W, body.H - top.H - 42);
            XpPaint.Sunken(c, list, XpLook.White);
            var head = new Box(list.X + 1, list.Y + 1, list.W - 2, 18);
            c.Shade(head, XpLook.TabFace, true, 1.0f);
            float c1 = head.X + 6, c2 = head.X + head.W * 0.40f, c3 = head.X + head.W * 0.72f;
            Ui.Paint.Text(c, f.Body, "Документ", new Box(c1, head.Y, 200, head.H), XpLook.Text, Align.Left);
            Ui.Paint.Text(c, f.Body, "Состояние", new Box(c2, head.Y, 200, head.H), XpLook.Text, Align.Left);
            Ui.Paint.Text(c, f.Body, "Ход печати", new Box(c3, head.Y, 200, head.H), XpLook.Text, Align.Left);
            Box jobRows = XpPaint.Scroll(c, ui, 500, new Box(list.X + 1, head.Bottom, list.W - 2, list.Bottom - head.Bottom - 1), pc.Jobs.Count * (XpLook.RowH + 2), ref jobScroll);
            c.PushClip(jobRows);
            for (int i = 0; i < pc.Jobs.Count; i++) {
                PcJob j = pc.Jobs[i];
                var row = new Box(jobRows.X, jobRows.Y + i * (XpLook.RowH + 2) - jobScroll, jobRows.W, XpLook.RowH + 2);
                if (row.Bottom <= jobRows.Y || row.Y >= jobRows.Bottom) { continue; }
                bool sel = j.Id == selected;
                if (sel) { c.Fill(row, XpLook.Select); }
                Rgba ink = sel ? XpLook.White : XpLook.Text;
                c.Image(XpLook.Image("text16"), new Box(row.X + 4, row.Y + 1, 16, 16), XpLook.White);
                Ui.Paint.Clipped(c, f.Body, j.Title, new Box(c1 + 20, row.Y, c2 - c1 - 26, row.H), ink, Align.Left);
                Ui.Paint.Clipped(c, f.Body, JobText(j), new Box(c2, row.Y, c3 - c2 - 6, row.H), ink, Align.Left);
                XpPaint.Progress(c, new Box(c3, row.Y + 3, MathF.Min(140, row.Right - c3 - 6), row.H - 6), j.Progress);
                if (ui.Click(800 + i, Box.Intersect(row, jobRows))) { selected = j.Id; }
            }
            c.PopClip();
            bool canCancel = false;
            for (int i = 0; i < pc.Jobs.Count; i++) {
                if (pc.Jobs[i].Id == selected && (pc.Jobs[i].Mine || pc.Gm) && (pc.Jobs[i].State == JobState.Queued || pc.Jobs[i].State == JobState.Printing)) { canCancel = true; }
            }
            var cancel = new Box(body.Right - 120, body.Bottom - XpLook.ButtonH, 120, XpLook.ButtonH);
            if (XpPaint.Button(c, ui, f, 50, cancel, "Отменить печать", canCancel && cancelToken == 0)) { cancelToken = pc.Cancel(selected); }
            XpPaint.Status(c, f, status, notice.Length > 0 ? notice : $"Заданий в очереди: {Active(pc)}", "");
        }

        private static int Active(PcSession pc) {
            int n = 0;
            for (int i = 0; i < pc.Jobs.Count; i++) {
                if (pc.Jobs[i].State == JobState.Queued || pc.Jobs[i].State == JobState.Printing) { n++; }
            }
            return n;
        }

        public bool WantsClose(XpShell shell, XpWindow self) => true;

        public void OnSessionSuspending(XpShell shell) {
            cancelToken = 0;
            lastLoad = double.NegativeInfinity;
        }

        public void OnSessionResumed(XpShell shell) => lastLoad = double.NegativeInfinity;

        public void Closed(XpShell shell) { }
    }
}
