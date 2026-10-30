using System.Collections.Generic;

namespace ScpItdMod.Inv {
    public enum ItemCategory : byte {
        All = 0,
        Weapon = 1,
        Ammo = 2,
        Medical = 3,
        Tool = 4,
        Document = 5,
        Other = 6
    }

    // Pure presentation rules for the subcategory strip of an open container.
    // Categories only choose which slots are shown on a tab; ownership never
    // changes. Kept free of game types so Tests/InventoryUi can render the same
    // chips the client draws.
    public static class ItemCategoryChips {
        public const int Back = -1;
        public const int MinItemsForTabs = 7;

        public static string Label(ItemCategory category) => category switch {
            ItemCategory.All => "ВСЕ",
            ItemCategory.Weapon => "ОРУЖ.",
            ItemCategory.Ammo => "ПАТР.",
            ItemCategory.Medical => "МЕД.",
            ItemCategory.Tool => "ИНСТР.",
            ItemCategory.Document => "ДОК.",
            _ => "ПРОЧ."
        };

        // Chip order: optional Back, then, only when the box is big enough to need sorting
        // (MinItemsForTabs items spread over at least two categories), ВСЕ and every category
        // that holds an item. counts[0] is the total, counts[k] the number of items of category k.
        public static void Fill(List<int> kinds, int[] counts, bool withBack) {
            kinds.Clear();
            if (withBack) { kinds.Add(Back); }
            if (!NeedsTabs(counts)) { return; }
            kinds.Add((int)ItemCategory.All);
            for (int k = 1; k < counts.Length; k++) {
                if (counts[k] > 0) { kinds.Add(k); }
            }
        }

        public static bool NeedsTabs(int[] counts) {
            if (counts[0] < MinItemsForTabs) { return false; }
            int used = 0;
            for (int k = 1; k < counts.Length; k++) { if (counts[k] > 0) { used++; } }
            return used >= 2;
        }
    }
}
