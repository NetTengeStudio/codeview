using System;
using Barotrauma;
using Barotrauma.Items.Components;

namespace ScpItdMod.Inv {
    // Maps a game item to its subcategory (enum and labels live in Shared/Inv/ItemCategory.cs).
    public static class ItemCategories {
        public static ItemCategory Of(Item item) {
            if (item == null) { return ItemCategory.Other; }
            ItemPrefab prefab = item.Prefab;
            if (prefab == null) { return ItemCategory.Other; }
            if (item.GetComponent<RangedWeapon>() != null || item.GetComponent<MeleeWeapon>() != null) { return ItemCategory.Weapon; }
            if (item.GetComponent<Projectile>() != null) { return ItemCategory.Ammo; }
            if (item.GetComponent<RepairTool>() != null) { return ItemCategory.Tool; }
            if (HasTag(prefab, "medical") || prefab.Category.HasFlag(MapEntityCategory.Medical)) { return ItemCategory.Medical; }
            if (HasTag(prefab, "weapon") || prefab.Category.HasFlag(MapEntityCategory.Weapon)) { return ItemCategory.Weapon; }
            if (HasTag(prefab, "tool") || HasTag(prefab, "cuttingequipment") || HasTag(prefab, "repairtool")) { return ItemCategory.Tool; }
            if (HasTag(prefab, "ammo") || HasTag(prefab, "pistolammoitem") || HasTag(prefab, "harpoonammo") ||
                HasTag(prefab, "handheldammo") || HasTag(prefab, "railgunammo")) { return ItemCategory.Ammo; }
            string id = prefab.Identifier.Value ?? "";
            if (HasTag(prefab, "document") || LooksLikeDocument(id)) { return ItemCategory.Document; }
            return ItemCategory.Other;
        }

        private static bool HasTag(ItemPrefab prefab, string tag) => prefab.Tags.Contains(tag.ToIdentifier());

        private static bool LooksLikeDocument(string identifier) {
            if (string.IsNullOrEmpty(identifier)) { return false; }
            return identifier.Contains("paper", StringComparison.OrdinalIgnoreCase) ||
                   identifier.Contains("document", StringComparison.OrdinalIgnoreCase) ||
                   identifier.Contains("notebook", StringComparison.OrdinalIgnoreCase) ||
                   identifier.Contains("dossier", StringComparison.OrdinalIgnoreCase) ||
                   identifier.Contains("folder", StringComparison.OrdinalIgnoreCase) ||
                   identifier.Contains("stamp", StringComparison.OrdinalIgnoreCase) ||
                   identifier.Contains("form", StringComparison.OrdinalIgnoreCase);
        }
    }
}
