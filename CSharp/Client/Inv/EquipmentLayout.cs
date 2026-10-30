using System;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace ScpItdMod.Inv {
    internal static class EquipmentLayout {
        private static int[] widths = Array.Empty<int>();
        private static bool[] personal = Array.Empty<bool>();
        private static int[] xs = Array.Empty<int>();
        private static int[] rows = Array.Empty<int>();

        internal static void Apply(CharacterInventory inventory) {
            Character controlled = Character.Controlled;
            if (controlled == null || !controlled.IsHuman || !ReferenceEquals(controlled.Inventory, inventory) ||
                inventory.CurrentLayout != CharacterInventory.Layout.Default) { return; }
            InvSlotType[] types = inventory.SlotTypes;
            VisualSlot[] slots = inventory.visualSlots;
            if (types == null || slots == null || slots.Length != types.Length || inventory.SlotPositions?.Length != types.Length) { return; }
            int count = types.Length;
            if (widths.Length != count) {
                widths = new int[count]; personal = new bool[count];
                xs = new int[count]; rows = new int[count];
            }
            float scale = Inventory.UIScale;
            Vector2 size = Inventory.SlotSpriteSmall.size * scale * GUI.AspectRatioAdjustment;
            int height = Math.Max(1, (int)size.Y);
            int spacing = GUI.IntScale(5);
            int above = (int)MathF.Ceiling(15 * scale + Inventory.UnequippedIndicator.size.Y * scale / 2);
            int below = Inventory.ContainedIndicatorHeight + spacing * 2;
            int pitch = height + above + below;
            for (int i = 0; i < count; i++) {
                widths[i] = 0;
                personal[i] = (CharacterInventory.PersonalSlots & types[i]) == types[i];
                if (slots[i] == null || (types[i] == InvSlotType.Any && inventory.GetItemAt(i) == null) || types[i] == InvSlotType.HealthInterface) { continue; }
                int width = Math.Max(1, (int)size.X);
                ItemContainer container = inventory.GetItemAt(i)?.GetComponent<ItemContainer>();
                if (container?.InventoryTopSprite != null) { width = Math.Max(width, (int)(container.InventoryTopSprite.size.X * scale)); }
                if (container?.InventoryBottomSprite != null) { width = Math.Max(width, (int)(container.InventoryBottomSprite.size.X * scale)); }
                widths[i] = width;
            }
            EquipmentRow.ArrangeInto(GameMain.GraphicsWidth, HUDLayoutSettings.Padding, spacing, widths, personal, xs, rows);
            Rectangle personalArea = Rectangle.Empty, background = Rectangle.Empty;
            int top = GameMain.GraphicsHeight;
            for (int i = 0; i < count; i++) {
                if (widths[i] <= 0) { continue; }
                VisualSlot slot = slots[i];
                var rect = new Rectangle(xs[i], GameMain.GraphicsHeight - below - height - rows[i] * pitch, widths[i], height);
                inventory.SlotPositions[i] = rect.Location.ToVector2();
                slot.Rect = rect;
                slot.InteractRect = rect;
                slot.InteractRect.Inflate(5, 5);
                slot.DrawOffset = Vector2.Zero;
                slot.SubInventoryDir = -1;
                slot.IsMoving = false;
                if (personal[i]) { personalArea = personalArea == Rectangle.Empty ? rect : Rectangle.Union(personalArea, rect); }
            }
            inventory.personalSlotArea = personalArea;
            for (int i = 0; i < count; i++) {
                if (widths[i] <= 0 || inventory.HideSlot(i)) { continue; }
                Rectangle rect = slots[i].Rect;
                background = background == Rectangle.Empty ? rect : Rectangle.Union(background, rect);
                top = Math.Min(top, slots[i].EquipButtonRect.Top);
            }
            if (background != Rectangle.Empty) { background.Inflate(10, 30); background.Y -= 25; }
            inventory.BackgroundFrame = background;
            HUDLayoutSettings.InventoryTopY = top - (int)(15 * GUI.Scale);
        }
    }
}
