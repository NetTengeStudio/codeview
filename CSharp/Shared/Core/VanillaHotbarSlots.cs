using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Barotrauma;
using HarmonyLib;
using ScpItdMod.Inv;

namespace ScpItdMod.Core {
    public static class VanillaHotbarSlots {
        public const int Limit = InvSlots.Capacity;
        public const string Marker = "scpitdslots";

        private sealed class Layout {
            public string[] From;
            public string[] To;
        }

        private static readonly ConditionalWeakTable<XElement, ContentXElement> trimmed =
            new ConditionalWeakTable<XElement, ContentXElement>();
        private static readonly ConditionalWeakTable<CharacterInventory, Layout> layouts =
            new ConditionalWeakTable<CharacterInventory, Layout>();

        public static void Attach(Harmony harmony) {
            harmony.Patch(
                AccessTools.Constructor(typeof(CharacterInventory),
                    new[] { typeof(ContentXElement), typeof(Character), typeof(bool) }),
                prefix: ModuleHost.Bind(typeof(VanillaHotbarSlots), nameof(ConstructorPrefix)));
            harmony.Patch(
                AccessTools.Method(typeof(Character), nameof(Character.SaveInventory), new[] { typeof(Inventory), typeof(XElement) }),
                postfix: ModuleHost.Bind(typeof(VanillaHotbarSlots), nameof(SavePostfix)));
            harmony.Patch(
                AccessTools.Method(typeof(Character), "SpawnInventoryItemsRecursive"),
                prefix: ModuleHost.Bind(typeof(VanillaHotbarSlots), nameof(SpawnPrefix)));
        }

        public static void ConstructorPrefix(CharacterInventory __instance, ref ContentXElement element, Character character) {
            if (element?.Element == null || character == null || !character.IsHuman) { return; }
            ContentXElement original = element;
            element = trimmed.GetValue(original.Element, source => Trim(original, source));
            if (ReferenceEquals(element, original)) { return; }
            layouts.AddOrUpdate(__instance, new Layout {
                From = InvSlots.Split(original.GetAttributeString("slots", null)),
                To = InvSlots.Split(element.GetAttributeString("slots", null))
            });
        }

        public static void SavePostfix(Inventory inventory, XElement parentElement) {
            if (parentElement == null || inventory is not CharacterInventory character || !layouts.TryGetValue(character, out _)) { return; }
            parentElement.SetAttributeValue(Marker, Limit);
        }

        public static void SpawnPrefix(Inventory inventory, ref ContentXElement element) {
            if (element?.Element == null || inventory is not CharacterInventory character ||
                !layouts.TryGetValue(character, out Layout layout) ||
                element.GetAttributeInt(Marker, 0) == Limit) {
                return;
            }
            var copy = new XElement(element.Element);
            var items = new List<XElement>();
            foreach (XElement item in copy.Elements()) { items.Add(item); }
            var indices = new int[items.Count][];
            for (int n = 0; n < items.Count; n++) { indices[n] = ParseIndices(items[n]); }
            int[][] migrated = InvSlots.Migrate(layout.From, layout.To, indices, out int moved, out int overflow);
            for (int n = 0; n < items.Count; n++) {
                items[n].SetAttributeValue("i", string.Join(",", migrated[n]));
            }
            copy.SetAttributeValue(Marker, Limit);
            element = new ContentXElement(element.ContentPackage, copy);
            if (moved > 0 || overflow > 0) {
                ModLog.Message($"inventory moved to {Limit} slots: {moved} items re-slotted, {overflow} left for the vanilla fallback");
            }
        }

        private static int[] ParseIndices(XElement item) {
            string raw = item.Attribute("i")?.Value;
            if (string.IsNullOrEmpty(raw)) { return new[] { 0 }; }
            string[] parts = raw.Split(',');
            var list = new List<int>(parts.Length);
            for (int i = 0; i < parts.Length; i++) {
                if (int.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int value)) {
                    list.Add(value);
                }
            }
            return list.Count > 0 ? list.ToArray() : new[] { 0 };
        }

        private static ContentXElement Trim(ContentXElement original, XElement source) {
            string slots = InvSlots.Trim(original.GetAttributeString("slots", null), Limit);
            if (slots == null) { return original; }
            var copy = new XElement(source);
            foreach (XAttribute attribute in copy.Attributes()) {
                if (!string.Equals(attribute.Name.LocalName, "slots", StringComparison.OrdinalIgnoreCase)) { continue; }
                attribute.Value = slots;
                return new ContentXElement(original.ContentPackage, copy);
            }
            return original;
        }
    }
}
