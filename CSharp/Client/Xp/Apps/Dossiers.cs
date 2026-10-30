using System;
using ScpItdMod.Paper;
using ScpItdMod.Pc;
using ScpItdMod.Ui;

namespace ScpItdMod.Xp {
    public sealed class Dossiers : IXpApp, IXpSized, IXpTyping, IXpSessionLifecycle {
        private readonly XpTextBox status = new XpTextBox(false, PcLimits.StatusMax, 1);
        private readonly XpTextBox notes = new XpTextBox(true, PcLimits.NotesMax, 200);
        private readonly XpTextBox closed = new XpTextBox(true, PcLimits.NotesMax, 200);
        private XpTextBox focus;
        private int level;
        private ulong open;
        private uint shown;
        private ulong shownId;
        private uint draftRevision;
        private int sentStatus, sentNotes, sentClosed;
        private int sentLevel;
        private float listScroll;
        private uint saveToken;
        private uint newToken;
        private string notice = "";
        private bool loaded;
        private bool reopenAfterResume;
        private double lastList;

        public string Title => "Личные дела";

        public string Icon => "dossier16";

        public InkEdit Edit => focus?.ReadOnly == false ? focus.Edit : null;

        public void Size(XpWindow w) => w.Rect = new Box(0, 0, 700, 470);

        public bool Command(XpShell shell, char control) {
            if (control == '\u0013') {
                Save(shell);
                return true;
            }
            return false;
        }

        private bool Dirty => status.Dirty || notes.Dirty || closed.Dirty || shownLevel != level;

        private int shownLevel;

        public void Paint(ICanvas c, UiState ui, XpFaces f, Box client, XpShell shell, XpWindow self) {
            PcSession pc = shell.Pc;
            if (pc.Ready && (!loaded || shell.Now - lastList > 10)) {
                loaded = true;
                lastList = shell.Now;
                pc.LoadDossiers();
            }
            if (pc.Ready && reopenAfterResume) {
                reopenAfterResume = false;
                if (open != 0) { pc.OpenDossier(open); }
            }
            Results(shell, self);
            c.Fill(client, XpLook.Surface);
            var status = client.TakeBottom(XpLook.StatusH);
            var body = client.CutBottom(XpLook.StatusH).Inset(8);
            var left = body.TakeLeft(230);
            var right = body.CutLeft(240);
            List(c, ui, f, left, shell, self);
            Form(c, ui, f, right, shell, self);
            string text = notice.Length > 0 ? notice : saveToken != 0 ? "Сохранение..." : pc.Dossier != null && Dirty ? "Есть несохранённые изменения" : "";
            XpPaint.Status(c, f, status, text, XpPaint.Num("Допуск: ", pc.Clearance));
        }

        private void List(ICanvas c, UiState ui, XpFaces f, in Box area, XpShell shell, XpWindow self) {
            PcSession pc = shell.Pc;
            XpPaint.Group(c, f, area, "Картотека");
            var list = new Box(area.X + 8, area.Y + 18, area.W - 16, area.H - 56);
            XpPaint.Sunken(c, list, XpLook.White);
            Box inner = list.Inset(1);
            Box rows = XpPaint.Scroll(c, ui, 300, inner, pc.Dossiers.Count * XpLook.RowH, ref listScroll);
            c.PushClip(rows);
            for (int i = 0; i < pc.Dossiers.Count; i++) {
                PcDossierRow d = pc.Dossiers[i];
                var row = new Box(rows.X, rows.Y + i * XpLook.RowH - listScroll, rows.W, XpLook.RowH);
                if (row.Bottom < rows.Y || row.Y > rows.Bottom) { continue; }
                if (XpPaint.Row(c, ui, f, 400 + i, row, "dossier16", d.Subject, d.Status, d.Id == open, viewport: rows)) { Select(shell, self, d.Id); }
            }
            c.PopClip();
            if (pc.Dossiers.Count == 0) {
                XpPaint.Label(c, f, pc.Clearance < 1 && !pc.Gm ? "Нет доступа к картотеке." : "Дел нет.", inner.Inset(6, 2, 6, 0).TakeTop(16), XpLook.Disabled);
            }
            var create = new Box(area.X + 8, area.Bottom - 32, area.W - 16, XpLook.ButtonH);
            if (XpPaint.Button(c, ui, f, 30, create, "Завести дело...", pc.CanCreateDossier && newToken == 0)) {
                if (pc.Subjects.Count == 0) {
                    shell.Ask(self, "Личные дела", "Нет людей, на которых можно завести дело.", new[] { "OK" }, null);
                } else {
                    string[] names = new string[pc.Subjects.Count];
                    for (int i = 0; i < names.Length; i++) { names[i] = pc.Subjects[i].Name; }
                    shell.Popup(self, create.X, create.Bottom, names, null, null, i => newToken = pc.NewDossier(pc.Subjects[i].Key));
                }
            }
        }

        private void Select(XpShell shell, XpWindow self, ulong id) {
            if (id == open) {
                if (shell.Pc.DossierError != PcError.Ok) { shell.Pc.OpenDossier(id); }
                return;
            }
            if (Dirty) {
                shell.Ask(self, "Личные дела", "Изменения в открытом деле не сохранены.\nПерейти без сохранения?", new[] { "Да", "Нет" }, i => {
                    if (i != 0) { return; }
                    Discard();
                    open = id;
                    shell.Pc.OpenDossier(id);
                });
                return;
            }
            open = id;
            shell.Pc.OpenDossier(id);
        }

        private void Discard() {
            status.Load(status.Text);
            notes.Load(notes.Text);
            closed.Load(closed.Text);
            shown = 0;
        }

        private void Form(ICanvas c, UiState ui, XpFaces f, in Box area, XpShell shell, XpWindow self) {
            PcSession pc = shell.Pc;
            PcDossierView v = pc.Dossier;
            if (v == null || v.Id != open) {
                XpPaint.Group(c, f, area, "Дело");
                XpPaint.Label(c, f, open == 0 ? "Выберите дело в картотеке слева." : pc.DossierError != PcError.Ok ? PcWire.Explain(pc.DossierError) : "Загрузка...", new Box(area.X + 12, area.Y + 24, area.W - 24, 16), XpLook.Shadow);
                return;
            }
            if (shownId != v.Id || shown != v.Rev) {
                bool keepDraft = shownId == v.Id && Dirty;
                shownId = v.Id;
                shown = v.Rev;
                if (keepDraft) {
                    if (draftRevision != v.Rev) { notice = "Дело изменилось, пока станция была отключена. Сохранение проверит конфликт."; }
                } else {
                    status.Load(v.Status);
                    notes.Load(v.Notes);
                    closed.Load(v.Closed ?? "");
                    level = shownLevel = v.Level;
                    draftRevision = v.Rev;
                }
                status.ReadOnly = notes.ReadOnly = !v.CanWrite;
                closed.ReadOnly = !v.CanWrite || !v.CanClosed;
            }
            XpPaint.Group(c, f, area, XpPaint.Num("Дело № ", (int)Math.Min(v.Id, int.MaxValue)));
            float x = area.X + 12;
            float y = area.Y + 22;
            float w = area.W - 24;
            XpPaint.Label(c, f, "Лицо:", new Box(x, y, 80, XpLook.FieldH));
            Ui.Paint.Clipped(c, f.Bold, v.Subject, new Box(x + 84, y, w - 84, XpLook.FieldH), XpLook.Text, Align.Left);
            y += XpLook.FieldH + 6;
            XpPaint.Label(c, f, "Статус:", new Box(x, y, 80, XpLook.FieldH));
            status.Paint(c, ui, 500, f.Body, new Box(x + 84, y, w - 84, XpLook.FieldH), ReferenceEquals(focus, status) && Focused(shell, self), shell.Now, out bool cs);
            if (cs) { focus = status; }
            y += XpLook.FieldH + 6;
            XpPaint.Label(c, f, "Допуск:", new Box(x, y, 80, XpLook.FieldH));
            for (int i = 0; i <= PcLimits.Levels; i++) {
                var b = new Box(x + 84 + i * 28, y, 26, XpLook.FieldH);
                bool on = i == level;
                XpPaint.ButtonFace(c, b, v.CanWrite, v.CanWrite && ui.Over(520 + i, b), on, false);
                Ui.Paint.Text(c, on ? f.Bold : f.Body, i.ToString(), b, v.CanWrite ? XpLook.Text : XpLook.Disabled, Align.Center);
                if (v.CanWrite && ui.Click(520 + i, b)) { level = i; }
            }
            y += XpLook.FieldH + 8;
            float rest = area.Bottom - y - 44;
            float notesH = v.CanClosed ? rest * 0.55f : rest;
            XpPaint.Label(c, f, "Сведения:", new Box(x, y, 120, 14));
            notes.Paint(c, ui, 600, f.Body, new Box(x, y + 16, w, notesH - 16), ReferenceEquals(focus, notes) && Focused(shell, self), shell.Now, out bool cn);
            if (cn) { focus = notes; }
            y += notesH + 4;
            if (v.CanClosed) {
                XpPaint.Label(c, f, "Закрытый раздел (допуск 3):", new Box(x, y, w, 14), Rgba.Hex(0x9A1B1B));
                closed.Paint(c, ui, 700, f.Body, new Box(x, y + 16, w, rest - notesH - 20), ReferenceEquals(focus, closed) && Focused(shell, self), shell.Now, out bool cc);
                if (cc) { focus = closed; }
            }
            var save = new Box(area.Right - XpLook.ButtonW - 12, area.Bottom - XpLook.ButtonH - 10, XpLook.ButtonW, XpLook.ButtonH);
            if (XpPaint.Button(c, ui, f, 40, save, "Сохранить", v.CanWrite && Dirty && saveToken == 0, true)) { Save(shell); }
        }

        private static bool Focused(XpShell shell, XpWindow self) => ReferenceEquals(shell.FocusedWindow, self);

        private void Save(XpShell shell) {
            PcDossierView v = shell.Pc.Dossier;
            if (v == null || !v.CanWrite || saveToken != 0) { return; }
            notice = "";
            sentStatus = status.Edit?.Version ?? 0; sentNotes = notes.Edit?.Version ?? 0; sentClosed = closed.Edit?.Version ?? 0; sentLevel = level;
            saveToken = shell.Pc.SaveDossier(v, draftRevision, status.Text, level, notes.Text, v.CanClosed ? closed.Text : null);
        }

        private void Results(XpShell shell, XpWindow self) {
            PcSession pc = shell.Pc;
            if (newToken != 0 && pc.Take(newToken, out PcResult made)) {
                newToken = 0;
                if (made.Error == PcError.Ok || made.Error == PcError.Exists) {
                    pc.LoadDossiers();
                    open = made.Id;
                    shown = 0;
                    pc.OpenDossier(made.Id);
                    if (made.Error == PcError.Exists) { notice = "На этого человека дело уже заведено — открыто существующее."; }
                } else {
                    shell.Ask(self, "Личные дела", PcWire.Explain(made.Error), new[] { "OK" }, null);
                }
            }
            if (saveToken != 0 && pc.Take(saveToken, out PcResult saved)) {
                saveToken = 0;
                if (saved.Error == PcError.Ok) {
                    draftRevision = pc.Dossier?.Rev ?? draftRevision;
                    status.SavedVersion = sentStatus; notes.SavedVersion = sentNotes; closed.SavedVersion = sentClosed; shownLevel = sentLevel;
                    notice = Dirty ? "Сохранена отправленная версия. Есть новые изменения." : "Дело сохранено.";
                    pc.LoadDossiers();
                } else if (saved.Error == PcError.Conflict) {
                    notice = "Дело изменилось. Ваш черновик сохранён в редакторе.";
                    shell.Ask(self, "Конфликт", "Дело было изменено другим сотрудником.\nЗаменить — сохранить ваш черновик поверх актуальной версии.\nОткрыть — удалить черновик и загрузить актуальную версию.", new[] { "Заменить", "Открыть", "Отмена" }, i => {
                        PcDossierView current = pc.Dossier;
                        if (current == null || current.Id != open) { return; }
                        if (i == 0) { draftRevision = current.Rev; Save(shell); }
                        else if (i == 1) {
                            status.Load(current.Status); notes.Load(current.Notes); closed.Load(current.Closed ?? "");
                            level = shownLevel = current.Level; draftRevision = current.Rev;
                        }
                    }, null);
                } else {
                    notice = PcWire.Explain(saved.Error);
                }
            }
        }

        public bool WantsClose(XpShell shell, XpWindow self) {
            if (!Dirty) { return true; }
            shell.Ask(self, "Личные дела", "Изменения в деле не сохранены.\nЗакрыть без сохранения?", new[] { "Да", "Нет" }, i => {
                if (i == 0) { shell.Close(self); }
            });
            return false;
        }

        public void OnSessionSuspending(XpShell shell) {
            saveToken = newToken = 0;
            loaded = false;
            lastList = double.NegativeInfinity;
            shown = 0;
        }

        public void OnSessionResumed(XpShell shell) {
            loaded = false;
            lastList = double.NegativeInfinity;
            reopenAfterResume = open != 0;
        }

        public void Closed(XpShell shell) { }
    }
}
