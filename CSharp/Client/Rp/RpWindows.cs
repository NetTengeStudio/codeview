using System;
using System.Text;
using Barotrauma;
using Barotrauma.Items.Components;
using ScpItdMod.Core;
using ScpItdMod.Hud;
using ScpItdMod.People;
using ScpItdMod.Ui;

namespace ScpItdMod.Rp {
    public sealed class ExamineWindow : RetroWindow {
        private static ExamineWindow instance;
        private Character who;
        private string label = "";
        private float scroll;
        private readonly StringBuilder text = new StringBuilder();
        private string shown = "";
        private double nextRefresh;

        public override string Title => "ОСМОТР: " + label;

        public static void Show(Character c) {
            if (c == null) { return; }
            instance ??= new ExamineWindow();
            instance.who = c;
            instance.scroll = 0.0f;
            instance.nextRefresh = 0;
            float s = GUI.Scale;
            instance.Rect = new Box(GameMain.GraphicsWidth * 0.5f - 210 * s, GameMain.GraphicsHeight * 0.22f, 420 * s, 360 * s);
            instance.Open();
        }

        public override void Tick(float dt) {
            if (who == null || who.Removed) {
                Close();
                return;
            }
            Character me = Character.Controlled;
            float reach = RpRules.Range(RpAct.Examine) * 1.5f;
            if (me == null || Microsoft.Xna.Framework.Vector2.DistanceSquared(me.WorldPosition, who.WorldPosition) > reach * reach) {
                Close();
                return;
            }
            if (Timing.TotalTime < nextRefresh) { return; }
            nextRefresh = Timing.TotalTime + 0.5;
            label = Names.Sighted(who);
            text.Clear();
            if (PeopleClient.Look(who.ID, out string look) && look.Length > 0) { text.Append(look).Append("\n\n"); }
            if (who.HideFace) { text.Append("Лицо закрыто.\n"); }
            Wear("Голова", who.Inventory?.GetItemInLimbSlot(InvSlotType.Head));
            Wear("Одежда", who.Inventory?.GetItemInLimbSlot(InvSlotType.OuterClothes));
            Wear("Под одеждой", who.Inventory?.GetItemInLimbSlot(InvSlotType.InnerClothes));
            foreach (Item held in who.HeldItems) { Wear("В руках", held); }
            Item card = who.Inventory?.GetItemInLimbSlot(InvSlotType.Card);
            IdCard id = card?.GetComponent<IdCard>();
            if (id != null) { text.Append("На карте написано: ").Append(string.IsNullOrEmpty(id.OwnerName) ? "—" : id.OwnerName).Append('\n'); }
            if (who.IsDead) { text.Append("Не дышит.\n"); } else if (who.IsIncapacitated) { text.Append("Без сознания.\n"); }
            if (who.LockHands) { text.Append("Руки скованы.\n"); }
            if (RpWorld.IsSurrendered(who)) { text.Append("Держит руки поднятыми.\n"); }
            if (text.Length == 0) { text.Append("Ничего примечательного."); }
            shown = text.ToString();
        }

        private void Wear(string what, Item item) {
            if (item == null) { return; }
            text.Append(what).Append(": ").Append(item.Name).Append('\n');
        }

        public override void Body(ICanvas c, UiState ui, RetroFaces f, Box body) {
            float used = Retro.Paragraph(c, f.Body, shown, body, Retro.Ink, scroll);
            if (ui.Over(10, body) && ui.In.Wheel != 0) { scroll -= ui.In.Wheel * 0.25f; }
            scroll = Math.Clamp(scroll, 0.0f, MathF.Max(0.0f, used - body.H));
        }
    }

    public sealed class OfferWindow : RetroWindow {
        private const float Seconds = 12.0f;
        private static OfferWindow instance;
        private uint offer;
        private string line = "";
        private double until;

        public override string Title => "ВАМ ПРОТЯГИВАЮТ ПРЕДМЕТ";

        public static void Show(uint id, ushort from, string item) {
            instance ??= new OfferWindow();
            string who = Entity.FindEntityByID(from) is Character giver ? Names.Sighted(giver) : "Кто-то";
            instance.offer = id;
            instance.line = $"{who} протягивает вам: {item}";
            instance.until = HudClock.Now + Seconds;
            float s = GUI.Scale;
            instance.Rect = new Box(GameMain.GraphicsWidth * 0.5f - 200 * s, GameMain.GraphicsHeight * 0.62f, 400 * s, 150 * s);
            instance.Open();
        }

        public static void Dismiss() => instance?.Close();

        public override void Tick(float dt) {
            if (HudClock.Now > until) { Close(); }
        }

        public override void Body(ICanvas c, UiState ui, RetroFaces f, Box body) {
            float row = Retro.Px(c, Retro.RowPx);
            Retro.Paragraph(c, f.Body, line, body.TakeTop(row * 2.0f), Retro.Bright);
            float left = (float)Math.Max(0.0, until - HudClock.Now) / Seconds;
            Retro.Meter(c, body.CutTop(row * 2.0f).TakeTop(Retro.Px(c, 8.0f)), left, Retro.Muted, 1.0f);
            var buttons = body.TakeBottom(Retro.Px(c, Retro.ButtonPx));
            float w = (buttons.W - Retro.Px(c, 8.0f)) * 0.5f;
            if (Retro.Button(c, ui, f, 10, buttons.TakeLeft(w), "Взять", true, 1.0f)) {
                RpClient.Answer(offer, true);
                Close();
            }
            if (Retro.Button(c, ui, f, 11, buttons.TakeRight(w), "Отказаться", true, 1.0f)) {
                RpClient.Answer(offer, false);
                Close();
            }
        }
    }

    public sealed class ShownWindow : RetroWindow {
        private static ShownWindow instance;
        private Item item;
        private string who = "";

        public override string Title => "ВАМ ПОКАЗЫВАЮТ";

        public static void Show(Character from, Item shown) {
            instance ??= new ShownWindow();
            instance.item = shown;
            instance.who = Names.Sighted(from);
            float s = GUI.Scale;
            instance.Rect = new Box(GameMain.GraphicsWidth * 0.5f - 200 * s, GameMain.GraphicsHeight * 0.3f, 400 * s, 260 * s);
            instance.Open();
        }

        public override void Tick(float dt) {
            if (item == null || item.Removed) { Close(); }
        }

        public override void Body(ICanvas c, UiState ui, RetroFaces f, Box body) {
            float row = Retro.Px(c, Retro.RowPx);
            Paint.Clipped(c, f.Small, who + " показывает:", body.TakeTop(row), Retro.Muted, Align.Left);
            body = body.CutTop(row);
            var icon = body.TakeLeft(Retro.Px(c, 96.0f)).TakeTop(Retro.Px(c, 96.0f));
            c.Fill(icon, Retro.Hover);
            if (c is BatchCanvas batch) { batch.Sprite(item.Prefab.InventoryIcon ?? item.Sprite, icon.Inset(Retro.Px(c, 6.0f)), new Rgba(255, 255, 255)); }
            var text = body.CutLeft(icon.W + Retro.Px(c, 8.0f));
            Paint.Clipped(c, f.Head, item.Name, text.TakeTop(row), Retro.Bright, Align.Left);
            Retro.Paragraph(c, f.Body, item.Description ?? "", text.CutTop(row + Retro.Px(c, 4.0f)), Retro.Ink);
        }
    }
}
