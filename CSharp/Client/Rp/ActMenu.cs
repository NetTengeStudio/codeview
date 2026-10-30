using System;
using System.Collections.Generic;
using ScpItdMod.Ui;

namespace ScpItdMod.Rp {
    public enum ActTab : byte { Person, Place, Self, Hands, Voice, Body }

    public enum ActKind : byte { Rp, Emote }

    public enum ActStage : byte { Browse, PickItem }

    public struct ActEntry {
        public ActKind Kind;
        public RpAct Act;
        public byte Emote;
        public string Title;
        public string Reason;
        public bool NeedsItem;
        public bool Confirm;
        public string Right;

        public bool Enabled => Reason == null;
    }

    public struct ItemChoice {
        public ushort Item;
        public string Title;
        public string Where;
        public string Reason;
    }

    public readonly struct ActIntent {
        public readonly ActKind Kind;
        public readonly RpAct Act;
        public readonly byte Emote;
        public readonly ushort Item;
        public readonly bool Valid;

        public ActIntent(ActKind kind, RpAct act, byte emote, ushort item) {
            Kind = kind;
            Act = act;
            Emote = emote;
            Item = item;
            Valid = true;
        }
    }

    public sealed class ActMenu {
        public const int Tabs = 6;
        private const int TabBase = 100;
        private const int RowBase = 200;
        private const int ItemBase = 400;

        private readonly List<ActEntry>[] entries = new List<ActEntry>[Tabs];
        public readonly List<ItemChoice> Items = new List<ItemChoice>();

        public string TargetLabel = "";
        public string TargetNote = "";
        public string ItemLabel = "";
        public ushort PresetItem;
        public ActTab Tab;
        public int Selected;
        public int Armed = -1;
        public ActStage Stage;
        public float Scroll;
        private ActEntry pending;

        public ActMenu() {
            for (int i = 0; i < Tabs; i++) { entries[i] = new List<ActEntry>(16); }
        }

        public List<ActEntry> Of(ActTab tab) => entries[(int)tab];

        public List<ActEntry> Current => entries[(int)Tab];

        public void Clear() {
            for (int i = 0; i < Tabs; i++) { entries[i].Clear(); }
            Items.Clear();
            Armed = -1;
            Stage = ActStage.Browse;
        }

        public static string TabTitle(ActTab t) => t switch {
            ActTab.Person => "Человек",
            ActTab.Place => "Укрытие",
            ActTab.Self => "Себе",
            ActTab.Hands => "Жесты",
            ActTab.Voice => "Голос",
            _ => "Тело"
        };

        public void FirstNonEmpty() {
            if (Current.Count > 0) { return; }
            for (int i = 0; i < Tabs; i++) {
                if (entries[i].Count > 0) {
                    Tab = (ActTab)i;
                    Selected = 0;
                    return;
                }
            }
        }

        public void Step(int delta) {
            if (Stage == ActStage.PickItem) {
                if (Items.Count > 0) { Selected = (Selected + delta + Items.Count) % Items.Count; }
                return;
            }
            if (Current.Count == 0) { return; }
            Selected = (Selected + delta + Current.Count) % Current.Count;
            Armed = -1;
        }

        public void TabStep(int delta) {
            if (Stage == ActStage.PickItem) { return; }
            for (int k = 1; k <= Tabs; k++) {
                int t = ((int)Tab + delta * k % Tabs + Tabs * 2) % Tabs;
                if (entries[t].Count == 0) { continue; }
                Tab = (ActTab)t;
                Selected = 0;
                Armed = -1;
                Scroll = 0.0f;
                return;
            }
        }

        public void Back() {
            if (Stage == ActStage.PickItem) {
                Stage = ActStage.Browse;
                Selected = Math.Max(0, Current.IndexOf(pending));
            }
            Armed = -1;
        }

        public ActIntent Activate(int index) {
            if (Stage == ActStage.PickItem) {
                if ((uint)index >= (uint)Items.Count) { return default; }
                ItemChoice choice = Items[index];
                if (choice.Reason != null) { return default; }
                Stage = ActStage.Browse;
                return new ActIntent(pending.Kind, pending.Act, pending.Emote, choice.Item);
            }
            List<ActEntry> list = Current;
            if ((uint)index >= (uint)list.Count) { return default; }
            ActEntry e = list[index];
            Selected = index;
            if (!e.Enabled) { return default; }
            if (e.NeedsItem && PresetItem == 0) {
                pending = e;
                Stage = ActStage.PickItem;
                Selected = 0;
                Scroll = 0.0f;
                return default;
            }
            if (e.Confirm && Armed != index) {
                Armed = index;
                return default;
            }
            Armed = -1;
            return new ActIntent(e.Kind, e.Act, e.Emote, e.NeedsItem ? PresetItem : (ushort)0);
        }

        public ActEntry PendingEntry => pending;

        public ActIntent Draw(ICanvas c, UiState ui, RetroFaces f, in Box frame, float alpha, string keyName) {
            ActIntent result = default;
            Box body = Retro.Window(c, f, frame, Stage == ActStage.PickItem ? "ВЫБОР ПРЕДМЕТА" : "ДЕЙСТВИЯ", alpha);
            float row = Retro.Px(c, Retro.RowPx);
            float pad = Retro.Px(c, Retro.PadPx);

            var head = body.TakeTop(row);
            Paint.Text(c, f.Small, "ЦЕЛЬ", head, Retro.Muted.Fade(alpha), Align.Left);
            float tagW = Retro.Px(c, 64.0f);
            Paint.Clipped(c, f.Body, TargetLabel.Length == 0 ? "—" : TargetLabel, head.CutLeft(tagW), Retro.Bright.Fade(alpha), Align.Left);
            if (TargetNote.Length > 0) { Paint.Text(c, f.Small, TargetNote, head, Retro.Muted.Fade(alpha), Align.Right); }
            body = body.CutTop(row);
            if (ItemLabel.Length > 0 || Stage == ActStage.PickItem) {
                var line = body.TakeTop(row);
                Paint.Text(c, f.Small, "ПРЕДМЕТ", line, Retro.Muted.Fade(alpha), Align.Left);
                string what = Stage == ActStage.PickItem ? pending.Title + ": выберите свой предмет" : ItemLabel;
                Paint.Clipped(c, f.Body, what, line.CutLeft(tagW), Retro.Warn.Fade(alpha), Align.Left);
                body = body.CutTop(row);
            }
            body = body.CutTop(Retro.Px(c, 4.0f));

            var footer = body.TakeBottom(row * 2.0f);
            body = body.CutBottom(row * 2.0f + Retro.Px(c, 4.0f));

            if (Stage == ActStage.Browse) {
                int shown = 0;
                for (int i = 0; i < Tabs; i++) {
                    if (entries[i].Count > 0) { shown++; }
                }
                var tabs = body.TakeTop(Retro.Px(c, Retro.ButtonPx));
                float tw = shown == 0 ? tabs.W : MathF.Floor(tabs.W / shown);
                int k = 0;
                for (int i = 0; i < Tabs; i++) {
                    if (entries[i].Count == 0) { continue; }
                    var tb = new Box(tabs.X + k * tw, tabs.Y, k == shown - 1 ? tabs.Right - (tabs.X + k * tw) : tw, tabs.H);
                    if (Retro.Tab(c, ui, f, TabBase + i, tb, TabTitle((ActTab)i), (int)Tab == i, alpha)) {
                        Tab = (ActTab)i;
                        Selected = 0;
                        Armed = -1;
                        Scroll = 0.0f;
                    }
                    k++;
                }
                body = body.CutTop(tabs.H + Retro.Px(c, 2.0f));
                List<ActEntry> list = Current;
                Box rows = Retro.List(c, ui, 50, body, list.Count, row, ref Scroll, out int first, out int last);
                c.PushClip(rows);
                for (int i = first; i <= last; i++) {
                    ActEntry e = list[i];
                    Box rb = Retro.RowBox(rows, i, row, Scroll);
                    string right = Armed == i ? "ещё раз — подтвердить" : e.Right;
                    if (Retro.Row(c, ui, f, RowBase + i, rb, e.Title, right, i == Selected, e.Enabled, alpha, Armed == i ? Retro.Warn : (Rgba?)null)) {
                        result = Activate(i);
                    }
                    if (ui.Over(RowBase + i, rb)) { Selected = i; }
                }
                c.PopClip();
                string reason = (uint)Selected < (uint)list.Count ? list[Selected].Reason : null;
                if (reason != null) {
                    Paint.Clipped(c, f.Small, reason, footer.TakeTop(row), Retro.Bad.Fade(alpha), Align.Left);
                } else if (Armed >= 0) {
                    Paint.Clipped(c, f.Small, "Подтвердите: Enter или повторный щелчок", footer.TakeTop(row), Retro.Warn.Fade(alpha), Align.Left);
                }
            } else {
                Box rows = Retro.List(c, ui, 60, body, Items.Count, row, ref Scroll, out int first, out int last);
                c.PushClip(rows);
                for (int i = first; i <= last; i++) {
                    ItemChoice it = Items[i];
                    Box rb = Retro.RowBox(rows, i, row, Scroll);
                    if (Retro.Row(c, ui, f, ItemBase + i, rb, it.Title, it.Where, i == Selected, it.Reason == null, alpha)) { result = Activate(i); }
                    if (ui.Over(ItemBase + i, rb)) { Selected = i; }
                }
                c.PopClip();
                if (Items.Count == 0) { Paint.Text(c, f.Body, "Подходящих предметов нет", rows.TakeTop(row), Retro.Muted.Fade(alpha), Align.Left); }
                string reason = (uint)Selected < (uint)Items.Count ? Items[Selected].Reason : null;
                if (reason != null) { Paint.Clipped(c, f.Small, reason, footer.TakeTop(row), Retro.Bad.Fade(alpha), Align.Left); }
            }
            var hints = footer.TakeBottom(row);
            float x = hints.X;
            x += Retro.Hint(c, f, x, hints.Y, "Enter", "выполнить", alpha);
            x += Retro.Hint(c, f, x, hints.Y, Stage == ActStage.PickItem ? "Backspace" : "←→", Stage == ActStage.PickItem ? "назад" : "вкладки", alpha);
            Retro.Hint(c, f, x, hints.Y, keyName, "закрыть", alpha);
            return result;
        }
    }
}
