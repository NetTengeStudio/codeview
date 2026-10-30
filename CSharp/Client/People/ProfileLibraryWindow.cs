using System;
using System.IO;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ScpItdMod.Core;

namespace ScpItdMod.People {
    public sealed class ProfileLibraryWindow : IDisposable {
        private static ProfileLibraryWindow current;
        private readonly ProfileLibrary library;
        private readonly Action<ProfileData> import;
        private GUIMessageBox window;
        private GUITextBox name, look, notes;
        private GUITextBlock status, counter;
        private GUIFrame appearanceHost;
        private CharacterInfo info;
        private CharacterInfo.AppearanceCustomizationMenu appearance;
        private uint selected;
        private bool disposed;
        public static bool IsOpen => current?.window?.Closed == false;
        public static void Open(Action<ProfileData> import = null) {
            if (IsOpen) { return; }
            current?.Dispose(); current = null;
            try { current = new ProfileLibraryWindow(import); }
            catch (Exception error) { ModLog.Error("Библиотека персонажей: " + error); new GUIMessageBox("Персонажи", error.Message); }
        }
        public static void Close() { current?.Dispose(); current = null; }
        private ProfileLibraryWindow(Action<ProfileData> import) {
            this.import = import;
            library = new ProfileLibrary(ProfileLibrary.DefaultPath);
            try {
                window = new GUIMessageBox("", "", Array.Empty<LocalizedString>(), relativeSize: new Vector2(0.88f, 0.9f));
                window.Content.Visible = false;
                var body = new GUILayoutGroup(new RectTransform(new Vector2(0.94f, 0.92f), window.InnerFrame.RectTransform, Anchor.Center)) { Stretch = true, RelativeSpacing = 0.015f };
                var top = new GUILayoutGroup(new RectTransform(new Vector2(1, 0.075f), body.RectTransform), isHorizontal: true) { Stretch = true, RelativeSpacing = 0.01f };
                Button(top, "<", 0.08f, () => Step(-1));
                counter = new GUITextBlock(new RectTransform(new Vector2(0.48f, 1), top.RectTransform), "Персонажи", textAlignment: Alignment.Center, font: GUIStyle.SubHeadingFont);
                Button(top, ">", 0.08f, () => Step(1));
                Button(top, "Новый", 0.18f, () => Navigate(0));
                Button(top, "Закрыть", 0.18f, () => Confirm(Dispose));
                var columns = new GUILayoutGroup(new RectTransform(new Vector2(1, 0.72f), body.RectTransform), isHorizontal: true) { Stretch = true, RelativeSpacing = 0.025f };
                appearanceHost = new GUIFrame(new RectTransform(new Vector2(0.46f, 1), columns.RectTransform), style: null);
                var scroll = new GUIListBox(new RectTransform(new Vector2(0.515f, 1), columns.RectTransform));
                var fields = new GUILayoutGroup(new RectTransform(Vector2.One, scroll.Content.RectTransform) { MinSize = new Point(0, GUI.IntScale(520)) }) { Stretch = true, RelativeSpacing = 0.01f };
                Label(fields, "Имя персонажа"); name = Field(fields, 0.075f, ProfileRules.NameMax, false);
                Label(fields, "Описание — видно другим игрокам"); look = Field(fields, 0.3f, ProfileRules.LookMax * 2, true);
                Label(fields, "Личные заметки — видите вы и ГМ"); notes = Field(fields, 0.3f, ProfileRules.NotesMax * 2, true);
                status = new GUITextBlock(new RectTransform(new Vector2(1, 0.065f), body.RectTransform), "", font: GUIStyle.SmallFont, wrap: true) { TextColor = GUIStyle.TextColorNormal };
                var buttons = new GUILayoutGroup(new RectTransform(new Vector2(1, 0.065f), body.RectTransform), isHorizontal: true) { Stretch = true, RelativeSpacing = 0.02f };
                Button(buttons, "Сохранить", 0.31f, Save);
                Button(buttons, "Удалить", 0.31f, Delete);
                if (import != null) { Button(buttons, "Перенести на сервер", 0.34f, Transfer); }
                else { Button(buttons, "Отменить правки", 0.34f, () => Show(selected)); }
                new GUICustomComponent(new RectTransform(Vector2.Zero, body.RectTransform), onUpdate: (_, _) => {
                    if (window.Closed) { Dispose(); }
                    else if (GUI.KeyboardDispatcher.Subscriber == null && PlayerInput.KeyHit(Keys.Escape)) { Confirm(Dispose); }
                }) { IgnoreLayoutGroups = true, CanBeFocused = false };
                Show(library.Book?.Active ?? 0);
            } catch { Dispose(); throw; }
        }
        private static void Label(GUILayoutGroup parent, string text) => new GUITextBlock(new RectTransform(new Vector2(1, 0.075f), parent.RectTransform), text, font: GUIStyle.SmallFont, wrap: true) { TextColor = GUIStyle.TextColorNormal };
        private static GUITextBox Field(GUILayoutGroup parent, float height, int max, bool wrap) {
            if (wrap) { return ProfileTextArea.Create(new RectTransform(new Vector2(1, height), parent.RectTransform), max); }
            var box = new GUITextBox(new RectTransform(new Vector2(1, height), parent.RectTransform), "", font: GUIStyle.SmallFont,
                textAlignment: wrap ? Alignment.TopLeft : Alignment.Left, wrap: wrap, createPenIcon: false) { MaxTextLength = max, OverflowClip = true };
            box.RectTransform.MaxSize = new Point(int.MaxValue, int.MaxValue);
            box.RectTransform.MinSize = new Point(0, GUI.IntScale(wrap ? 120 : 32));
            foreach (GUIComponent child in box.Children) { child.RectTransform.MaxSize = new Point(int.MaxValue, int.MaxValue); }
            return box;
        }
        private static void Button(GUILayoutGroup parent, string text, float width, Action action) {
            new GUIButton(new RectTransform(new Vector2(width, 1), parent.RectTransform), text) { OnClicked = (_, _) => { action(); return true; } };
        }
        private void Show(uint id) {
            selected = id; Profile p = library.Book?.Find(id);
            name.Text = p?.Data.Name ?? ""; look.Text = p?.Data.Look ?? ""; notes.Text = p?.Data.Notes ?? "";
            appearance?.Dispose(); info?.Remove();
            info = new CharacterInfo(CharacterPrefab.HumanSpeciesName, p?.Name ?? "Персонаж", null);
            if (p?.Data.Looks.Set == true) { LooksCodec.Apply(info, p.Data.Looks); }
            appearance = new CharacterInfo.AppearanceCustomizationMenu(info, appearanceHost);
            counter.Text = id == 0 ? "Новый персонаж" : p.Name;
            status.Text = $"Локальная библиотека: {library.Book?.Count ?? 0}/{ProfileRules.PerAccount}. Перенос на сервер создаёт отдельный серверный профиль.";
        }
        private ProfileData Data() => new ProfileData { Name = name.Text, Look = look.Text, Notes = notes.Text, Looks = LooksCodec.Capture(info) };
        private bool Dirty() {
            if (info == null) { return false; }
            ProfileData data = ProfileRules.Normalized(Data()); Profile p = library.Book?.Find(selected);
            return p == null ? data.Name.Length != 0 || data.Look.Length != 0 || data.Notes.Length != 0 : !data.Same(p.Data);
        }
        private void Confirm(Action action) {
            if (!Dirty()) { action(); return; }
            var confirm = new GUIMessageBox("Несохранённые изменения", "Сохранить изменения персонажа?", new LocalizedString[] { "Сохранить", "Не сохранять", "Отмена" });
            confirm.Buttons[0].OnClicked = (_, _) => { if (TrySave()) { confirm.Close(); action(); } return true; };
            confirm.Buttons[1].OnClicked = (_, _) => { confirm.Close(); action(); return true; };
            confirm.Buttons[2].OnClicked = confirm.Close;
        }
        private void Navigate(uint id) => Confirm(() => { Show(id); Choose(id); });
        private void Choose(uint id) {
            if (id == 0 || library.Book?.Active == id) { return; }
            try {
                PeopleError error = library.Activate(id);
                status.Text = error == PeopleError.Ok ? "Этот персонаж выбран для игры: он появится в главном меню и будет предложен на сервере." : "Не удалось выбрать персонажа: " + error;
            } catch (Exception error) { status.Text = "Ошибка записи: " + error.Message; }
        }
        private void Step(int dir) {
            ProfileBook book = library.Book; if (book == null || book.Count == 0) { return; }
            int index = 0; for (int i = 0; i < book.Count; i++) { if (book.At(i).Id == selected) { index = i; break; } }
            uint id = book.At((index + dir + book.Count) % book.Count).Id; Navigate(id);
        }
        private void Save() => TrySave();
        private bool TrySave() {
            try {
                PeopleError error = library.Save(selected, Data(), out uint id, out ProfileField field);
                if (error != PeopleError.Ok) { status.Text = $"Не удалось сохранить: {field} — {error}. Имя: 2–32 буквы; описание: до 400 знаков / 8 строк; заметки: до 600 знаков / 12 строк."; return false; }
                Show(id); status.Text = "Персонаж сохранён на этом компьютере."; return true;
            } catch (Exception error) { status.Text = "Ошибка записи: " + error.Message; return false; }
        }
        private void Delete() {
            if (selected == 0) { return; }
            var confirm = new GUIMessageBox("Удалить персонажа?", name.Text, new LocalizedString[] { "Удалить", "Отмена" });
            confirm.Buttons[0].OnClicked = (_, _) => {
                try { PeopleError error = library.Delete(selected); if (error == PeopleError.Ok) { Show(library.Book?.Active ?? 0); } else { status.Text = "Удаление: " + error; } }
                catch (Exception error) { status.Text = "Ошибка удаления: " + error.Message; }
                confirm.Close(); return true;
            };
            confirm.Buttons[1].OnClicked = confirm.Close;
        }
        private void Transfer() {
            if (!TrySave()) { return; }
            ProfileData data = library.Book.Find(selected).Data.Clone(); import(data); Dispose();
        }
        public void Dispose() {
            if (disposed) { return; } disposed = true;
            appearance?.Dispose(); appearance = null; info?.Remove(); info = null;
            window?.Close();
            if (ReferenceEquals(current, this)) { current = null; }
        }
    }
}
