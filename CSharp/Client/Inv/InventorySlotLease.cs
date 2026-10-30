using System;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace ScpItdMod.Inv {
    internal sealed class InventorySlotLease : IDisposable {
        private readonly ItemInventory inventory;
        private readonly VisualSlot[] slots;
        private VisualSlot[] previousSlots;
        private readonly bool previousSubInventory;

        internal InventorySlotLease(ItemInventory inventory) {
            this.inventory = inventory;
            previousSlots = inventory.visualSlots;
            previousSubInventory = inventory.isSubInventory;
            slots = new VisualSlot[inventory.Capacity];
            for (int i = 0; i < slots.Length; i++) { slots[i] = new VisualSlot(Rectangle.Empty); }
            inventory.visualSlots = slots;
            inventory.isSubInventory = true;
            ClearSelection();
        }

        internal void Bind() {
            if (ReferenceEquals(inventory.visualSlots, slots)) { return; }
            previousSlots = inventory.visualSlots;
            inventory.visualSlots = slots;
            ClearSelection();
        }

        private void ClearSelection() {
            if (ReferenceEquals(Inventory.SelectedSlot?.ParentInventory, inventory) ||
                ReferenceEquals(Inventory.SelectedSlot?.Inventory, inventory)) { Inventory.selectedSlot = null; }
        }

        public void Dispose() {
            ClearSelection();
            Inventory.highlightedSubInventorySlots.RemoveWhere(reference => ReferenceEquals(reference.Inventory, inventory));
            if (ReferenceEquals(inventory.visualSlots, slots)) { inventory.visualSlots = previousSlots; }
            inventory.isSubInventory = previousSubInventory;
        }
    }
}
