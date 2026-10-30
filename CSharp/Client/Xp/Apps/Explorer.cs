using System;
using System.Collections.Generic;
using System.Globalization;
using ScpItdMod.Pc;
using ScpItdMod.Ui;

namespace ScpItdMod.Xp {
    public sealed class Explorer : IXpApp, IXpSized, IXpArg, IXpSessionLifecycle {
        public const ulong Computer = 0;
        public const ulong RecycleBin = ulong.MaxValue;
        private const float PaneW = 186;
        private static readonly Ramp Pane = new Ramp((0.0f, 0x7BA2E7), (1.0f, 0x6375D6));
        private static readonly Ramp PaneHead = new Ramp((0.0f, 0xFFFFFF), (1.0f, 0xC6D3F7));
        private static readonly string[] menu = { "Файл", "Правка", "Вид", "Справка" };
        private static readonly string[] contextFile = { "Открыть", "Печать", "", "Удалить", "Переименовать" };
        private static readonly string[] contextFolder = { "Открыть", "", "Удалить", "Переименовать" };

        private readonly List<ulong> back = new List<ulong>();
        private readonly List<ulong> forward = new List<ulong>();
        private ulong folder;
        private ulong selected;
        private float scroll;
        private uint pendingToken;
        private string pendingWhat = "";
        private double refreshed;
        private bool refreshOnResume;

        public Explorer(ulong start) => folder = start;

        public string Title => folder == Computer ? "Мой компьютер" : folder == RecycleBin ? "Корзина" : name;

        private string name = "Проводник";

        public string Icon => folder == RecycleBin ? "recycle16" : folder == Computer ? "computer16" : "folderopen16";

        public void Size(XpWindow w) => w.Rect = new Box(0, 0, 640, 440);

        public void Argue(XpShell shell, object arg) {
            if (arg is ulong id) { Go(shell, id); }
        }

        private void Go(XpShell shell, ulong id, bool history = true) {
            if (id == folder) { return; }
            if (history) {
                back.Add(folder);
                if (back.Count > 32) { back.RemoveAt(0); }
                forward.Clear();
            }
            folder = id;
            selected = 0;
            scroll = 0;
            Refresh(shell);
        }

        private void Refresh(XpShell shell) {
            if (folder == RecycleBin) {
                shell.Pc.LoadBin();
            } else if (folder != Computer) {
                shell.Pc.Folder(folder, true);
            } else {
                shell.Pc.Hello();
            }
            refreshed = shell.Now;
        }

        public void Paint(ICanvas c, UiState ui, XpFaces f, Box client, XpShell shell, XpWindow self) {
            PcSession pc = shell.Pc;
            Pending(shell, self);
            if (refreshOnResume && pc.Ready) {
                refreshOnResume = false;
                if (folder == RecycleBin) { pc.LoadBin(); }
                else if (folder != Computer) { pc.Folder(folder, true); }
            }
            c.Fill(client, XpLook.Surface);
            var bar = client.TakeTop(XpLook.MenuH);
            int picked = XpPaint.Menu(c, ui, f, 10, bar, menu, -1);
            if (picked >= 0) { MenuOpen(shell, self, f, bar, picked); }
            var tools = new Box(client.X, bar.Bottom, client.W, 38);
            Toolbar(c, ui, f, tools, shell);
            var address = new Box(client.X, tools.Bottom, client.W, 24);
            c.Fill(new Box(address.X, address.Y, address.W, 1), XpLook.Separator);
            XpPaint.Label(c, f, "Адрес", new Box(address.X + 6, address.Y + 1, 40, address.H), XpLook.Shadow);
            PcFolder pf = folder != Computer && folder != RecycleBin ? pc.Folder(folder) : null;
            if (pf != null && pc.Stale(pf) && !pf.Loading) { pc.Folder(folder, true); }
            if (pf != null && pf.Name.Length > 0) { name = pf.Name; }
            string path = folder == Computer ? "Мой компьютер" : folder == RecycleBin ? "Корзина" : pf?.Path ?? "";
            XpPaint.Field(c, f, new Box(address.X + 48, address.Y + 2, address.W - 54, 20), "      " + path, true);
            c.Image(XpLook.Image(Icon), new Box(address.X + 51, address.Y + 4, 16, 16), XpLook.White);
            var status = client.TakeBottom(XpLook.StatusH);
            var body = new Box(client.X, address.Bottom + 1, client.W, status.Y - address.Bottom - 1);
            var pane = body.TakeLeft(PaneW);
            var list = body.CutLeft(PaneW);
            Tasks(c, ui, f, pane, shell, self, pf);
            int count = Items(c, ui, f, list, shell, self, pf);
            string left = folder == RecycleBin ? XpPaint.Num("Объектов: ", count) : pf?.Error is PcError e && e != PcError.Ok ? PcWire.Explain(e) : XpPaint.Num("Объектов: ", count);
            XpPaint.Status(c, f, status, pendingWhat.Length > 0 ? pendingWhat : left, folder == Computer ? pc.Disk : pf != null ? XpPaint.Num("Допуск: ", pf.Level) : "");
            if (folder == RecycleBin && shell.Now - refreshed > 3) { Refresh(shell); }
        }

        private void Toolbar(ICanvas c, UiState ui, XpFaces f, in Box tools, XpShell shell) {
            c.Fill(tools, XpLook.Surface);
            c.Fill(new Box(tools.X, tools.Y, tools.W, 1), XpLook.White);
            float x = tools.X + 4;
            if (Tool(c, ui, f, 30, ref x, tools, "◄ Назад", back.Count > 0)) {
                forward.Add(folder);
                ulong to = back[back.Count - 1];
                back.RemoveAt(back.Count - 1);
                Go(shell, to, false);
            }
            if (Tool(c, ui, f, 31, ref x, tools, "Вперёд ►", forward.Count > 0)) {
                back.Add(folder);
                ulong to = forward[forward.Count - 1];
                forward.RemoveAt(forward.Count - 1);
                Go(shell, to, false);
            }
            PcFolder pf = folder != Computer && folder != RecycleBin ? shell.Pc.Folder(folder) : null;
            if (Tool(c, ui, f, 32, ref x, tools, "▲ Вверх", folder != Computer)) { Go(shell, pf != null && pf.Parent != 0 ? pf.Parent : Computer); }
            c.Fill(new Box(x + 2, tools.Y + 6, 1, tools.H - 12), XpLook.Separator);
            x += 8;
            if (Tool(c, ui, f, 33, ref x, tools, "Обновить", true)) { Refresh(shell); }
        }

        private static bool Tool(ICanvas c, UiState ui, XpFaces f, int id, ref float x, in Box tools, string label, bool enabled) {
            float w = Ui.Paint.Measure(f.Body, label) + 16;
            var b = new Box(x, tools.Y + 4, w, tools.H - 8);
            x += w + 2;
            bool over = enabled && ui.Over(id, b);
            bool clicked = enabled && ui.Click(id, b);
            if (over) {
                bool held = ui.In.Down && ui.Active == id;
                c.Fill(b, held ? Rgba.Hex(0xE3E3DB) : XpLook.White);
                Ui.Paint.Frame(c, b, 1, Rgba.Hex(0xCECEC3));
            }
            Ui.Paint.Text(c, f.Body, label, b, enabled ? XpLook.Text : XpLook.Disabled, Align.Center);
            return clicked;
        }

        private void Tasks(ICanvas c, UiState ui, XpFaces f, in Box pane, XpShell shell, XpWindow self, PcFolder pf) {
            c.Shade(pane, Pane, true, 1.0f);
            var group = new Box(pane.X + 12, pane.Y + 12, pane.W - 24, 23);
            c.Shade(group, PaneHead, false, 1.0f);
            Ui.Paint.Text(c, f.Bold, folder == RecycleBin ? "Задачи корзины" : "Файлы и папки", group.Inset(8, 0, 4, 0), XpLook.GroupHeader, Align.Left);
            var links = new Box(group.X, group.Bottom, group.W, 98);
            c.Fill(links, Rgba.Hex(0xD6DFF7));
            Ui.Paint.Frame(c, links, 1, XpLook.White);
            float y = links.Y + 8;
            PcEntry sel = default;
            bool has = Selected(shell, pf, out sel);
            if (folder == RecycleBin) {
                if (Link(c, ui, f, 60, links.X + 10, ref y, "Восстановить объект", has)) { Begin(shell, shell.Pc.Restore(sel.Id), "Восстановление..."); }
                return;
            }
            bool writable = pf != null && pf.Writable;
            if (Link(c, ui, f, 61, links.X + 10, ref y, "Создать документ", writable)) {
                shell.Dialog(self, new XpPrompt("Создать документ", "Имя нового документа:", "Новый документ.txt", PcLimits.NameMax,
                    text => Begin(shell, shell.Pc.Create(folder, NodeKind.Text, text), "Создание документа...")), 330, 130);
            }
            if (Link(c, ui, f, 62, links.X + 10, ref y, "Создать папку", writable)) {
                shell.Dialog(self, new XpPrompt("Создать папку", "Имя новой папки:", "Новая папка", PcLimits.NameMax,
                    text => Begin(shell, shell.Pc.Create(folder, NodeKind.Folder, text), "Создание папки...")), 330, 130);
            }
            if (Link(c, ui, f, 63, links.X + 10, ref y, "Переименовать", has)) { Rename(shell, self, sel); }
            if (Link(c, ui, f, 64, links.X + 10, ref y, "Удалить", has)) { Delete(shell, self, sel); }
            if (Link(c, ui, f, 65, links.X + 10, ref y, "Напечатать файл", has && sel.Kind == NodeKind.Text)) { shell.Dialog(self, new XpPrintDialog(sel.Id, sel.Rev, sel.Name), 360, 240); }
        }

        private static bool Link(ICanvas c, UiState ui, XpFaces f, int id, float x, ref float y, string text, bool enabled) {
            var b = new Box(x, y, 150, 15);
            y += 17;
            bool over = enabled && ui.Over(id, b);
            Rgba ink = !enabled ? Rgba.Hex(0x8A9AC6) : over ? Rgba.Hex(0x428EFF) : XpLook.GroupHeader;
            Ui.Paint.Text(c, f.Body, text, b, ink, Align.Left);
            if (over) { c.Fill(new Box(b.X, b.Y + 13, Ui.Paint.Measure(f.Body, text), 1), ink); }
            return enabled && ui.Click(id, b);
        }

        private bool Selected(XpShell shell, PcFolder pf, out PcEntry e) {
            e = default;
            if (selected == 0) { return false; }
            List<PcEntry> source = folder == RecycleBin ? shell.Pc.Bin : pf?.Entries;
            if (source == null) { return false; }
            for (int i = 0; i < source.Count; i++) {
                if (source[i].Id == selected) {
                    e = source[i];
                    return true;
                }
            }
            return false;
        }

        private int Items(ICanvas c, UiState ui, XpFaces f, in Box area, XpShell shell, XpWindow self, PcFolder pf) {
            XpPaint.Sunken(c, area, XpLook.White);
            var head = new Box(area.X + 1, area.Y + 1, area.W - 2, 18);
            c.Shade(head, XpLook.TabFace, true, 1.0f);
            c.Fill(new Box(head.X, head.Bottom - 1, head.W, 1), Rgba.Hex(0xD5D2C2));
            float colName = head.X + 6;
            float colSize = head.X + head.W * 0.44f;
            float colType = head.X + head.W * 0.56f;
            float colDate = head.X + head.W * 0.84f;
            Ui.Paint.Text(c, f.Body, "Имя", new Box(colName, head.Y, 200, head.H), XpLook.Text, Align.Left);
            Ui.Paint.Text(c, f.Body, "Размер", new Box(colSize, head.Y, 80, head.H), XpLook.Text, Align.Left);
            Ui.Paint.Text(c, f.Body, "Тип", new Box(colType, head.Y, 100, head.H), XpLook.Text, Align.Left);
            Ui.Paint.Text(c, f.Body, "Изменён", new Box(colDate, head.Y, 100, head.H), XpLook.Text, Align.Left);
            var rowsArea = new Box(area.X + 1, head.Bottom, area.W - 2, area.Bottom - head.Bottom - 1);
            if (folder == Computer) { return Roots(c, ui, f, rowsArea, shell); }
            List<PcEntry> source = folder == RecycleBin ? shell.Pc.Bin : pf?.Entries;
            if (source == null) { return 0; }
            if (pf != null && pf.Loading && pf.Entries.Count == 0) {
                Ui.Paint.Text(c, f.Body, "Загрузка...", rowsArea.Inset(8, 4, 8, 0).TakeTop(16), XpLook.Shadow, Align.Left);
                return 0;
            }
            Box rows = XpPaint.Scroll(c, ui, 400, rowsArea, source.Count * XpLook.RowH, ref scroll);
            c.PushClip(rows);
            int first = Math.Max(0, (int)(scroll / XpLook.RowH));
            int last = Math.Min(source.Count - 1, (int)((scroll + rows.H) / XpLook.RowH));
            for (int i = first; i <= last; i++) {
                PcEntry e = source[i];
                var row = new Box(rows.X, rows.Y + i * XpLook.RowH - scroll, rows.W, XpLook.RowH);
                bool sel = e.Id == selected;
                string icon = e.Kind == NodeKind.Folder ? "folder16" : "text16";
                if (sel) { c.Fill(new Box(row.X + 22, row.Y, colSize - row.X - 26, row.H), XpLook.Select); }
                c.Image(XpLook.Image(icon), new Box(row.X + 4, row.Y, 16, 16), XpLook.White);
                Rgba ink = sel ? XpLook.White : XpLook.Text;
                Ui.Paint.Clipped(c, f.Body, e.Name, new Box(row.X + 24, row.Y, colSize - row.X - 30, row.H), ink, Align.Left);
                if (e.Kind == NodeKind.Text) { Ui.Paint.Text(c, f.Body, Size(e.Size), new Box(colSize, row.Y, colType - colSize - 8, row.H), XpLook.Text, Align.Left); }
                Ui.Paint.Clipped(c, f.Body, e.Kind == NodeKind.Folder ? "Папка с файлами" : "Текстовый документ", new Box(colType, row.Y, colDate - colType - 6, row.H), XpLook.Text, Align.Left);
                Ui.Paint.Text(c, f.Body, Stamp(e.Updated), new Box(colDate, row.Y, 100, row.H), XpLook.Text, Align.Left);
                if (ui.Over(500 + i, row)) {
                    if (ui.In.Pressed || ui.In.AltPressed) { selected = e.Id; }
                    if (ui.In.AltPressed && folder != RecycleBin) { Context(shell, self, e); }
                }
                if (ui.Click(500 + i, row) && shell.Clicked(500 + i, out bool twice) && twice && folder != RecycleBin) { Open(shell, e); }
            }
            c.PopClip();
            return source.Count;
        }

        private int Roots(ICanvas c, UiState ui, XpFaces f, in Box rows, XpShell shell) {
            List<PcRoot> roots = shell.Pc.Roots;
            for (int i = 0; i < roots.Count; i++) {
                var row = new Box(rows.X, rows.Y + i * XpLook.RowH, rows.W, XpLook.RowH);
                bool sel = roots[i].Id == selected;
                if (sel) { c.Fill(new Box(row.X + 22, row.Y, row.W * 0.46f, row.H), XpLook.Select); }
                c.Image(XpLook.Image("folder16"), new Box(row.X + 4, row.Y, 16, 16), XpLook.White);
                Ui.Paint.Text(c, f.Body, roots[i].Name, new Box(row.X + 24, row.Y, row.W * 0.46f, row.H), sel ? XpLook.White : XpLook.Text, Align.Left);
                Ui.Paint.Text(c, f.Body, "Локальный диск (C:)", new Box(row.X + row.W * 0.62f, row.Y, 200, row.H), XpLook.Text, Align.Left);
                if (ui.Over(500 + i, row) && ui.In.Pressed) { selected = roots[i].Id; }
                if (ui.Click(500 + i, row) && shell.Clicked(500 + i, out bool twice) && twice) { Go(shell, roots[i].Id); }
            }
            return roots.Count;
        }

        private void Context(XpShell shell, XpWindow self, PcEntry e) {
            if (e.Kind == NodeKind.Folder) {
                shell.Popup(self, shell.In.X, shell.In.Y, contextFolder, null, null, i => {
                    if (i == 0) { Open(shell, e); }
                    if (i == 2) { Delete(shell, self, e); }
                    if (i == 3) { Rename(shell, self, e); }
                });
            } else {
                shell.Popup(self, shell.In.X, shell.In.Y, contextFile, null, null, i => {
                    if (i == 0) { Open(shell, e); }
                    if (i == 1) { shell.Dialog(self, new XpPrintDialog(e.Id, e.Rev, e.Name), 360, 240); }
                    if (i == 3) { Delete(shell, self, e); }
                    if (i == 4) { Rename(shell, self, e); }
                });
            }
        }

        private void MenuOpen(XpShell shell, XpWindow self, XpFaces f, Box bar, int which) {
            float x = XpPaint.MenuX(f, bar, menu, which);
            PcFolder pf = folder != Computer && folder != RecycleBin ? shell.Pc.Folder(folder) : null;
            bool has = Selected(shell, pf, out PcEntry sel);
            switch (which) {
                case 0:
                    shell.Popup(self, x, bar.Bottom, new[] { "Открыть", "Печать...", "", "Закрыть" }, new[] { has, has && sel.Kind == NodeKind.Text, true, true }, null, i => {
                        if (i == 0) { Open(shell, sel); }
                        if (i == 1) { shell.Dialog(self, new XpPrintDialog(sel.Id, sel.Rev, sel.Name), 360, 240); }
                        if (i == 3) { shell.RequestClose(self); }
                    });
                    break;
                case 1:
                    shell.Popup(self, x, bar.Bottom, new[] { "Переименовать", "Удалить" }, new[] { has && folder != RecycleBin, has && folder != RecycleBin }, new[] { "F2", "Del" }, i => {
                        if (i == 0) { Rename(shell, self, sel); }
                        if (i == 1) { Delete(shell, self, sel); }
                    });
                    break;
                case 2:
                    shell.Popup(self, x, bar.Bottom, new[] { "Обновить" }, null, new[] { "F5" }, _ => Refresh(shell));
                    break;
                case 3:
                    shell.Popup(self, x, bar.Bottom, new[] { "О программе" }, null, null, _ =>
                        shell.Ask(self, "Проводник", "Проводник рабочей станции.\nДиск: " + shell.Pc.Disk, new[] { "OK" }, null));
                    break;
            }
        }

        private void Open(XpShell shell, PcEntry e) {
            if (e.Id == 0) { return; }
            if (e.Kind == NodeKind.Folder) {
                Go(shell, e.Id);
                return;
            }
            IReadOnlyList<XpWindow> stack = shell.Desk.Stack;
            for (int i = 0; i < stack.Count; i++) {
                if (stack[i].App is Notepad n && n.FileId == e.Id) {
                    shell.Desk.ActivateFromTask(stack[i]);
                    shell.Desk.Focus(stack[i]);
                    return;
                }
            }
            shell.Launch("notepad", e.Id);
        }

        private void Rename(XpShell shell, XpWindow self, PcEntry e) =>
            shell.Dialog(self, new XpPrompt("Переименование", "Новое имя:", e.Name, PcLimits.NameMax, text => Begin(shell, shell.Pc.Rename(e.Id, text), "Переименование...")), 330, 130);

        private void Delete(XpShell shell, XpWindow self, PcEntry e) =>
            shell.Ask(self, e.Kind == NodeKind.Folder ? "Подтверждение удаления папки" : "Подтверждение удаления файла",
                $"Вы действительно хотите переместить «{e.Name}» в корзину?", new[] { "Да", "Нет" }, i => {
                    if (i == 0) { Begin(shell, shell.Pc.Delete(e.Id), "Удаление..."); }
                }, "recycle32");

        private void Begin(XpShell shell, uint token, string what) {
            pendingToken = token;
            pendingWhat = what;
        }

        private void Pending(XpShell shell, XpWindow self) {
            if (pendingToken == 0 || !shell.Pc.Take(pendingToken, out PcResult r)) { return; }
            pendingToken = 0;
            pendingWhat = "";
            if (r.Error != PcError.Ok) {
                shell.Ask(self, "Ошибка", PcWire.Explain(r.Error), new[] { "OK" }, null);
            } else if (r.Op == PcOp.Create) {
                selected = r.Id;
            }
            Refresh(shell);
        }

        public static string Size(int chars) => XpPaint.Num("", Math.Max(1, (chars + 1023) / 1024)) is var n ? Kb(n) : "";

        private static readonly System.Collections.Generic.Dictionary<string, string> kb = new System.Collections.Generic.Dictionary<string, string>();

        private static string Kb(string n) {
            if (!kb.TryGetValue(n, out string s)) {
                s = n + " КБ";
                kb[n] = s;
            }
            return s;
        }

        private static readonly string[] stamps = new string[24 * 60];

        public static string Stamp(double seconds) {
            if (seconds <= 0) { return ""; }
            int minutes = (int)(seconds / 60.0) % stamps.Length;
            return stamps[minutes] ??= $"{minutes / 60:00}:{minutes % 60:00}";
        }

        public bool WantsClose(XpShell shell, XpWindow self) => true;

        public void OnSessionSuspending(XpShell shell) {
            pendingToken = 0;
            pendingWhat = "";
            refreshOnResume = true;
        }

        public void OnSessionResumed(XpShell shell) => refreshOnResume = true;

        public void Closed(XpShell shell) { }
    }
}
