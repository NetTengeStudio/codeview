using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using ScpItdMod.Core;
using ScpItdMod.Inv;
using ScpItdMod.People;
using ScpItdMod.Ui;

namespace ScpItdMod.Rp {
    public sealed class SearchPanel : RetroWindow {
        private const double RefreshSeconds = 0.2;
        private const int MineBase = 1000;
        private const int TheirsBase = 1000000;
        private static readonly List<InvSlotType> PocketSlots = new List<InvSlotType> { InvSlotType.Any };

        private struct Line {
            public Item Item;
            public SlotKind Kind;
            public int Depth;
            public int Count;
            public string Reason;
            public string Label;
            public string Right;
        }

        private static SearchPanel instance;
        private readonly InventoryTree inventoryTree = new InventoryTree();
        private readonly List<InventoryEntry> descendants = new List<InventoryEntry>(32);
        private readonly List<Line> mine = new List<Line>();
        private readonly List<Line> theirs = new List<Line>();
        private readonly HashSet<Item> listed = new HashSet<Item>(ReferenceEqualityComparer.Instance);
        private readonly HashSet<Item> traversed = new HashSet<Item>(ReferenceEqualityComparer.Instance);
        private readonly List<Item> roots = new List<Item>();
        private Character target;
        private string label = "";
        private string theirTitle = "";
        private string status = "";
        private Rgba statusInk = Retro.Ink;
        private float scrollMine;
        private float scrollTheirs;
        private double nextRefresh;

        public override string Title => "ОБЫСК: " + label;

        public static Character Target => instance != null && instance.IsOpen ? instance.target : null;

        public static void Open(Character target) {
            instance ??= new SearchPanel();
            instance.target = target;
            instance.status = "";
            instance.nextRefresh = 0;
            float s = GUI.Scale;
            instance.Rect = new Box(GameMain.GraphicsWidth * 0.5f - 380 * s, GameMain.GraphicsHeight * 0.18f, 760 * s, 470 * s);
            instance.Open();
        }

        public static void Shut() => instance?.Close();

        public override void Tick(float dt) {
            Character me = Character.Controlled;
            if (me == null || target == null || target.Removed || !RpWorld.Permitted(me, target) && !target.IsDead) {
                Close();
                return;
            }
            if (Timing.TotalTime < nextRefresh) { return; }
            nextRefresh = Timing.TotalTime + RefreshSeconds;
            label = Names.Sighted(target);
            theirTitle = "У НЕГО: " + StateText();
            Fill(mine, me, me, true);
            Fill(theirs, target, me, false);
        }

        private void Fill(List<Line> lines, Character owner, Character me, bool own) {
            lines.Clear(); listed.Clear(); traversed.Clear(); roots.Clear();
            CharacterInventory inv = owner.Inventory;
            if (inv == null) { return; }
            TargetState state = RpWorld.StateOf(owner);
            bool permitted = own || RpWorld.Permitted(me, owner);
            for (int i = 0; i < inv.Capacity; i++) {
                InvSlotType type = inv.SlotTypes[i];
                SlotKind kind = RpWorld.Kind(type);
                if (own && kind != SlotKind.Hand && kind != SlotKind.Pocket) { continue; }
                Item item = inv.GetItemAt(i);
                if (item != null && !listed.Add(item)) { continue; }
                if (item != null) { roots.Add(item); }
                int count = 0;
                if (item != null) {
                    foreach (Item _ in inv.GetItemsAt(i)) { count++; }
                }
                string reason = null;
                if (!own && item != null) {
                    TakeDeny d = SearchRules.Take(kind, state, permitted);
                    if (d != TakeDeny.None) { reason = SearchRules.Explain(d); }
                }
                lines.Add(Make(item, kind, 0, count, reason, own));
            }
            for (int i = 0; i < roots.Count; i++) {
                Item item = roots[i];
                inventoryTree.CollectChildren(item, descendants, visibleContainersOnly: true, sharedVisitedItems: traversed);
                string reason = null;
                if (!own) {
                    TakeDeny deny = SearchRules.Take(SlotKind.Nested, state, permitted);
                    if (deny != TakeDeny.None) { reason = SearchRules.Explain(deny); }
                }
                for (int nested = 0; nested < descendants.Count; nested++) {
                    InventoryEntry entry = descendants[nested];
                    if (listed.Add(entry.Item)) { lines.Add(Make(entry.Item, SlotKind.Nested, entry.Depth, 1, reason, own)); }
                }
            }
        }

        private static Line Make(Item item, SlotKind kind, int depth, int count, string reason, bool own) {
            string slot = depth > 0 ? new string(' ', Math.Min(depth, 16) * 2) + "└ " : SearchRules.Title(kind) + ": ";
            string name = item == null ? "—" : item.Name + (count > 1 ? " ×" + count : "");
            return new Line {
                Item = item, Kind = kind, Depth = depth, Count = count, Reason = reason, Label = slot + name,
                Right = item == null ? "" : reason != null ? "закрыто" : own ? "положить" : "взять"
            };
        }

        public override void Body(ICanvas c, UiState ui, RetroFaces f, Box body) {
            float row = Retro.Px(c, Retro.RowPx);
            float pad = Retro.Px(c, Retro.PadPx);
            var foot = body.TakeBottom(row);
            body = body.CutBottom(row + pad);
            float half = MathF.Floor((body.W - pad) * 0.5f);
            Box left = body.TakeLeft(half);
            Box right = body.TakeRight(half);
            Column(c, ui, f, left, "У ВАС", mine, true, ref scrollMine, MineBase);
            Column(c, ui, f, right, theirTitle, theirs, false, ref scrollTheirs, TheirsBase);
            Paint.Clipped(c, f.Small, status.Length > 0 ? status : "Щелчок справа — забрать себе, слева — положить в карман человеку.", foot, status.Length > 0 ? statusInk : Retro.Muted, Align.Left);
        }

        private string StateText() => RpWorld.StateOf(target) switch {
            TargetState.Cooperating => "сдался",
            TargetState.Bound => "связан",
            TargetState.Unconscious => "без сознания",
            TargetState.Dead => "тело",
            _ => "сопротивляется"
        };

        private void Column(ICanvas c, UiState ui, RetroFaces f, Box box, string title, List<Line> lines, bool own, ref float scroll, int idBase) {
            float row = Retro.Px(c, Retro.RowPx);
            c.Fill(box, Retro.PlateSolid);
            Paint.Frame(c, box, Retro.Px(c, 1.0f), Retro.Rule);
            Paint.Clipped(c, f.Head, title, box.TakeTop(row).Inset(Retro.Px(c, 4.0f), 0, Retro.Px(c, 4.0f), 0), Retro.Bright, Align.Left);
            Box rows = Retro.List(c, ui, own ? 10 : 20, box.CutTop(row), lines.Count, row, ref scroll, out int first, out int last);
            c.PushClip(rows);
            for (int i = first; i <= last; i++) {
                Line l = lines[i];
                Box rb = Retro.RowBox(rows, i, row, scroll);
                bool enabled = l.Item != null && l.Reason == null;
                if (Retro.Row(c, ui, f, idBase + i, rb, l.Label, l.Right, false, enabled, 1.0f)) {
                    if (own) { Put(l.Item); } else { Take(l.Item); }
                }
                if (l.Reason != null && ui.Over(idBase + i, rb)) {
                    status = l.Reason;
                    statusInk = Retro.Warn;
                }
            }
            c.PopClip();
        }

        private void Take(Item item) {
            Character me = Character.Controlled;
            if (me?.Inventory == null || item == null) { return; }
            if (!me.Inventory.CanBePut(item)) {
                Say("Вам некуда это положить", Retro.Bad);
                return;
            }
            if (me.Inventory.TryPutItem(item, me, CharacterInventory.AnySlot)) {
                Say("Вы забрали: " + item.Name, Retro.Good);
                nextRefresh = 0;
            } else {
                Say("Не получилось забрать", Retro.Bad);
            }
        }

        private void Put(Item item) {
            Character me = Character.Controlled;
            if (me == null || target?.Inventory == null || item == null) { return; }
            TakeDeny d = SearchRules.Put(SlotKind.Pocket, RpWorld.StateOf(target), RpWorld.Permitted(me, target));
            if (d != TakeDeny.None && !target.IsDead) {
                Say(SearchRules.Explain(d), Retro.Bad);
                return;
            }
            if (!target.Inventory.CanBePut(item)) {
                Say("У человека нет свободного кармана", Retro.Bad);
                return;
            }
            if (target.Inventory.TryPutItem(item, me, PocketSlots)) {
                Say("Вы положили: " + item.Name, Retro.Good);
                nextRefresh = 0;
            } else {
                Say("Не получилось положить", Retro.Bad);
            }
        }

        private void Say(string text, Rgba ink) {
            status = text;
            statusInk = ink;
        }
    }
}
