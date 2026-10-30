using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;

namespace ScpItdMod.Inv {
    public readonly struct InventoryEntry {
        public readonly Item Item;
        public readonly Inventory Inventory;
        public readonly int Slot;
        public readonly int Depth;
        public readonly ushort Holder;
        public readonly int Container;

        internal InventoryEntry(Item item, Inventory inventory, int slot, int depth, ushort holder, int container) {
            Item = item;
            Inventory = inventory;
            Slot = slot;
            Depth = depth;
            Holder = holder;
            Container = container;
        }
    }

    public sealed class InventoryTree {
        private readonly struct Pending {
            public readonly Inventory Inventory;
            public readonly int Depth;
            public readonly ushort Holder;
            public readonly int Container;

            public Pending(Inventory inventory, int depth, ushort holder, int container) {
                Inventory = inventory;
                Depth = depth;
                Holder = holder;
                Container = container;
            }
        }

        private readonly List<Pending> pending = new List<Pending>(16);
        private readonly HashSet<Inventory> visitedInventories = new HashSet<Inventory>(ReferenceEqualityComparer.Instance);
        private readonly HashSet<Item> visitedItems = new HashSet<Item>(ReferenceEqualityComparer.Instance);

        public bool Collect(Inventory root, List<InventoryEntry> entries, ushort holder = 0, int container = 0,
            int initialDepth = 0, bool visibleContainersOnly = false, int maxEntries = int.MaxValue, HashSet<Item> sharedVisitedItems = null) {
            if (entries == null) { throw new ArgumentNullException(nameof(entries)); }
            if (initialDepth < 0) { throw new ArgumentOutOfRangeException(nameof(initialDepth)); }
            if (maxEntries < 0) { throw new ArgumentOutOfRangeException(nameof(maxEntries)); }
            entries.Clear();
            ResetWalk(sharedVisitedItems);
            Enqueue(root, initialDepth, holder, container);
            return Traverse(entries, visibleContainersOnly, maxEntries, sharedVisitedItems);
        }

        public bool CollectChildren(Item root, List<InventoryEntry> entries, bool visibleContainersOnly = false,
            int maxEntries = int.MaxValue, HashSet<Item> sharedVisitedItems = null) {
            if (entries == null) { throw new ArgumentNullException(nameof(entries)); }
            if (maxEntries < 0) { throw new ArgumentOutOfRangeException(nameof(maxEntries)); }
            entries.Clear();
            ResetWalk(sharedVisitedItems);
            if (root == null) { return true; }
            (sharedVisitedItems ?? visitedItems).Add(root);
            EnqueueChildren(root, 1, visibleContainersOnly);
            return Traverse(entries, visibleContainersOnly, maxEntries, sharedVisitedItems);
        }

        private void ResetWalk(HashSet<Item> sharedVisitedItems) {
            pending.Clear();
            visitedInventories.Clear();
            if (sharedVisitedItems == null) { visitedItems.Clear(); }
        }

        private void Enqueue(Inventory inventory, int depth, ushort holder, int container) {
            if (inventory != null && visitedInventories.Add(inventory)) {
                pending.Add(new Pending(inventory, depth, holder, container));
            }
        }

        private void EnqueueChildren(Item item, int depth, bool visibleContainersOnly) {
            int index = 0;
            foreach (ItemContainer component in item.GetComponents<ItemContainer>()) {
                if (!visibleContainersOnly || component.DrawInventory) { Enqueue(component.Inventory, depth, item.ID, index); }
                index++;
            }
        }

        private bool Traverse(List<InventoryEntry> entries, bool visibleContainersOnly, int maxEntries, HashSet<Item> sharedVisitedItems) {
            HashSet<Item> descended = sharedVisitedItems ?? visitedItems;
            for (int workIndex = 0; workIndex < pending.Count; workIndex++) {
                Pending work = pending[workIndex];
                for (int slot = 0; slot < work.Inventory.Capacity; slot++) {
                    foreach (Item item in work.Inventory.GetItemsAt(slot)) {
                        if (item == null) { continue; }
                        if (entries.Count == maxEntries) { return false; }
                        entries.Add(new InventoryEntry(item, work.Inventory, slot, work.Depth, work.Holder, work.Container));
                        if (!descended.Add(item)) { continue; }
                        int componentIndex = 0;
                        foreach (ItemContainer component in item.GetComponents<ItemContainer>()) {
                            Inventory child = component.Inventory;
                            bool visible = !visibleContainersOnly || component.DrawInventory;
                            if (visible && child != null && visitedInventories.Add(child)) {
                                pending.Add(new Pending(child, work.Depth + 1, item.ID, componentIndex));
                            }
                            componentIndex++;
                        }
                    }
                }
            }
            return true;
        }
    }
}
