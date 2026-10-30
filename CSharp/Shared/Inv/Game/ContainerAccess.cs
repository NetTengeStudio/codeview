using System;
using Barotrauma;
using Barotrauma.Items.Components;

namespace ScpItdMod.Inv {
    internal static class ContainerAccess {
        internal static bool TryRoot(Inventory inventory, out Entity root) {
            root = null;
            Inventory slow = inventory, fast = inventory;
            while (fast != null) {
                slow = Parent(slow); fast = Parent(Parent(fast));
                if (slow != null && ReferenceEquals(slow, fast)) { return false; }
            }
            while (inventory != null) {
                Entity owner = inventory.Owner;
                if (owner == null || owner.Removed) { return false; }
                if (owner is not Item item || item.ParentInventory == null) { root = owner; return true; }
                Inventory parent = item.ParentInventory;
                if (parent.FindIndex(item) < 0) { return false; }
                inventory = parent;
            }
            return false;
        }

        private static Inventory Parent(Inventory inventory) => (inventory?.Owner as Item)?.ParentInventory;

        internal static bool CanOpen(Character actor, ItemInventory inventory) {
            if (actor == null || actor.Removed || actor.IsDead || !actor.CanInteract || inventory?.Container is not ItemContainer container ||
                !container.DrawInventory || inventory.Locked || !TryRoot(inventory, out Entity root)) { return false; }
#if CLIENT
            if (!ReferenceEquals(root, actor) && !ReferenceEquals(root, actor.SelectedCharacter) &&
                !ReferenceEquals(root, actor.SelectedItem) && !ReferenceEquals(root, actor.SelectedSecondaryItem) &&
                !(root is MapEntity mapRoot && (actor.SelectedItem?.linkedTo.Contains(mapRoot) ?? false))) { return false; }
            if (ReferenceEquals(root, actor) && inventory.Owner is Item item && actor.Inventory.IsInLimbSlot(item, InvSlotType.HealthInterface) &&
                CharacterHealth.OpenHealthWindow?.Character != actor) { return false; }
#endif
            return actor.CanAccessInventory(inventory);
        }
    }
}
