using System;
using ScpItdMod.Paper;
using ScpItdMod.Pc;
using ScpItdMod.Ui;

namespace ScpItdMod.Xp {
    public sealed class Notepad : IXpApp, IXpTyping, IXpSized, IXpSessionLifecycle {
        private static readonly string[] menu = { "Файл", "Правка", "Справка" };
        private static readonly string[] fileItems = { "Сохранить", "Печать...", "", "Выход" };
        private static readonly string[] fileKeys = { "Ctrl+S", "Ctrl+P", null, null };
        private static readonly string[] editItems = { "Отменить", "Вернуть", "", "Вырезать", "Копировать", "Вставить", "Удалить", "", "Выделить всё" };
        private static readonly string[] editKeys = { "Ctrl+Z", "Ctrl+Y", null, "Ctrl+X", "Ctrl+C", "Ctrl+V", "Del", null, "Ctrl+A" };
        private static readonly string[] helpItems = { "О программе" };

        private readonly ulong fileId;
        private readonly ushort computer;
        private readonly XpTextBox box = new XpTextBox(true, PcLimits.BodyMax, 4000);
        private PcFile file;
        private int loadedRevision = -1;
        private uint saveToken;
        private uint baseRev;
        private bool closeAfterSave;
        private bool printAfterSave;
        private int openMenu = -1;
        private string notice = "";

        public Notepad(ushort computer, ulong fileId) { this.computer = computer; this.fileId = fileId; }

        private string titleFor;
        private string title = "Загрузка - Блокнот";

        public string Title {
            get {
                string n = file == null || file.Name.Length == 0 ? "Загрузка" : file.Name;
                if (!ReferenceEquals(n, titleFor)) {
                    titleFor = n;
                    title = n + " - Блокнот";
                }
                return title;
            }
        }

        public string Icon => "text16";

        public InkEdit Edit => box.ReadOnly ? null : box.Edit;

        public ulong FileId => fileId;

        public void Size(XpWindow w) => w.Rect = new Box(0, 0, 560, 420);

        public void Paint(ICanvas c, UiState ui, XpFaces f, Box client, XpShell shell, XpWindow self) {
            PcSession pc = shell.Pc;
            file ??= pc.Retain(fileId);
            if (pc.Ready && !file.Loading && file.Error is PcError.NoPower or PcError.NoServer or PcError.Far or PcError.Timeout) { pc.File(fileId, true); }
            if (file.Revision != loadedRevision && !file.Loading) {
                loadedRevision = file.Revision;
                if (file.Error == PcError.Ok && !box.Dirty) {
                    box.Load(file.Text);
                    baseRev = file.Rev;
                }
                box.ReadOnly = file.ReadOnly || file.Error != PcError.Ok;
            }
            Results(shell, self);
            c.Fill(client, XpLook.Surface);
            var bar = client.TakeTop(XpLook.MenuH);
            int picked = XpPaint.Menu(c, ui, f, 10, bar, menu, openMenu);
            if (picked >= 0) { OpenMenu(shell, self, f, bar, picked); }
            var status = client.TakeBottom(XpLook.StatusH);
            var text = new Box(client.X, bar.Bottom, client.W, client.H - bar.H - status.H);
            if (file.Loading && file.Revision == 0) {
                XpPaint.Sunken(c, text, XpLook.White);
            } else if (file.Error != PcError.Ok) {
                XpPaint.Sunken(c, text, XpLook.White);
                box.Paint(c, ui, 100, f.Body, text, ReferenceEquals(shell.FocusedWindow, self), shell.Now, out _);
                notice = PcWire.Explain(file.Error);
            } else {
                box.Paint(c, ui, 100, f.Body, text, ReferenceEquals(shell.FocusedWindow, self), shell.Now, out _);
            }
            string right = box.ReadOnly ? "Только чтение" : box.Line();
            XpPaint.Status(c, f, status, notice.Length > 0 ? notice : saveToken != 0 ? "Сохранение..." : box.Dirty ? "Изменён" : "", right);
        }

        private void OpenMenu(XpShell shell, XpWindow self, XpFaces f, Box bar, int which) {
            openMenu = which;
            float x = XpPaint.MenuX(f, bar, menu, which);
            switch (which) {
                case 0:
                    shell.Popup(self, x, bar.Bottom, fileItems, new[] { !box.ReadOnly, true, true, true }, fileKeys, i => {
                        openMenu = -1;
                        if (i == 0) { Save(shell); }
                        if (i == 1) { Print(shell, self); }
                        if (i == 3) { shell.RequestClose(self); }
                    });
                    break;
                case 1: {
                    InkEdit e = box.Edit;
                    bool sel = e != null && e.HasSelection;
                    bool rw = !box.ReadOnly;
                    shell.Popup(self, x, bar.Bottom, editItems, new[] { rw, rw, true, rw && sel, sel, rw && shell.PasteText != null, rw && sel, true, true }, editKeys, i => {
                        openMenu = -1;
                        EditCommand(shell, i);
                    });
                    break;
                }
                case 2:
                    shell.Popup(self, x, bar.Bottom, helpItems, null, null, _ => {
                        openMenu = -1;
                        shell.Ask(self, "О программе «Блокнот»", "Блокнот, версия 5.1\nФайлы хранятся на диске этой рабочей станции.\nНаибольший размер документа: 4096 знаков.", new[] { "OK" }, null);
                    });
                    break;
            }
        }

        private void EditCommand(XpShell shell, int i) {
            InkEdit e = box.Edit;
            if (e == null) { return; }
            double now = shell.Now;
            switch (i) {
                case 0: if (!box.ReadOnly) { e.Undo(); } break;
                case 1: if (!box.ReadOnly) { e.Redo(); } break;
                case 3:
                    if (!box.ReadOnly && e.HasSelection) {
                        shell.CopyText?.Invoke(e.Selected());
                        e.Back(false, now);
                    }
                    break;
                case 4: if (e.HasSelection) { shell.CopyText?.Invoke(e.Selected()); } break;
                case 5: {
                    string t = shell.PasteText?.Invoke();
                    if (!box.ReadOnly && !string.IsNullOrEmpty(t)) { e.Type(t, now); }
                    break;
                }
                case 6: if (!box.ReadOnly) { e.Delete(false, now); } break;
                case 8: e.SelectAll(); break;
            }
        }

        public bool Command(XpShell shell, char control) {
            switch (control) {
                case '\u0013':
                    Save(shell);
                    return true;
                case '\u0010':
                    Print(shell, shell.FocusedWindow);
                    return true;
            }
            return false;
        }

        private void Save(XpShell shell) {
            if (box.ReadOnly || saveToken != 0 || file == null || file.Error != PcError.Ok) { return; }
            notice = "";
            saveToken = shell.Pc.Save(fileId, baseRev, box.Text);
            sentVersion = box.Edit?.Version ?? 0;
        }

        private int sentVersion;

        private void Print(XpShell shell, XpWindow self) {
            if (file == null || file.Error != PcError.Ok) { return; }
            if (box.Dirty) {
                shell.Ask(self, "Блокнот", "Перед печатью документ нужно сохранить.\nСохранить сейчас?", new[] { "Да", "Отмена" }, i => {
                    if (i != 0) { return; }
                    printAfterSave = true;
                    Save(shell);
                }, null);
                return;
            }
            shell.Dialog(self, new XpPrintDialog(fileId, baseRev, file.Name), 360, 240);
        }

        private void Results(XpShell shell, XpWindow self) {
            if (saveToken == 0 || !shell.Pc.Take(saveToken, out PcResult r)) { return; }
            saveToken = 0;
            if (r.Error == PcError.Ok) {
                baseRev = r.Rev;
                loadedRevision = -1;
                if (box.Edit != null && box.Edit.Version == sentVersion) { box.MarkSaved(); }
                notice = "";
                if (closeAfterSave) {
                    closeAfterSave = false;
                    if (!box.Dirty) { shell.Close(self); }
                    return;
                }
                if (printAfterSave) {
                    printAfterSave = false;
                    shell.Dialog(self, new XpPrintDialog(fileId, baseRev, file.Name), 360, 240);
                }
                return;
            }
            closeAfterSave = false;
            printAfterSave = false;
            if (r.Error == PcError.Conflict && file.Conflict) {
                shell.Ask(self, "Конфликт версий", $"Файл «{file.Name}» был изменён другим пользователем,\nпока вы его редактировали.\n\nЗаменить — записать вашу версию поверх.\nОткрыть — загрузить их версию (ваши изменения будут потеряны).",
                    new[] { "Заменить", "Открыть", "Отмена" }, i => {
                        if (i == 0) {
                            file.Rev = file.TheirRev;
                            baseRev = file.TheirRev;
                            file.Conflict = false;
                            Save(shell);
                        } else if (i == 1) {
                            file.Rev = file.TheirRev;
                            baseRev = file.TheirRev;
                            file.Text = file.TheirText ?? "";
                            file.Conflict = false;
                            box.Load(file.Text);
                        }
                    }, null);
                return;
            }
            notice = PcWire.Explain(r.Error);
            shell.Ask(self, "Блокнот", "Не удалось сохранить файл.\n" + PcWire.Explain(r.Error), new[] { "OK" }, null);
        }

        public bool WantsClose(XpShell shell, XpWindow self) {
            if (!box.Dirty) { return true; }
            if (box.ReadOnly) {
                shell.Ask(self, "Блокнот", "Изменения не сохранены. Сейчас запись недоступна.\nЗакрыть документ без сохранения?", new[] { "Закрыть без сохранения", "Отмена" }, i => {
                    if (i == 0) { shell.Close(self); }
                }, null);
                return false;
            }
            shell.Ask(self, "Блокнот", $"Текст в файле «{file?.Name}» был изменён.\n\nСохранить изменения?", new[] { "Да", "Нет", "Отмена" }, i => {
                if (i == 0) {
                    closeAfterSave = true;
                    Save(shell);
                } else if (i == 1) {
                    shell.Close(self);
                }
            }, null);
            return false;
        }

        public void OnSessionSuspending(XpShell shell) {
            file = null;
            loadedRevision = -1;
            saveToken = 0;
            closeAfterSave = false;
            printAfterSave = false;
        }

        public void OnSessionResumed(XpShell shell) {
            file = null;
            loadedRevision = -1;
        }

        public void Closed(XpShell shell) {
            if (file != null && shell.Pc?.Computer == computer) { shell.Pc.Forget(fileId); }
        }
    }
}
