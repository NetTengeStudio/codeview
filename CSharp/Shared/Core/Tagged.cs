using System;
using System.Collections.Generic;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace ScpItdMod.Core {
    public static class Tagged {
        private sealed class Set {
            public readonly Identifier Tag;
            public readonly List<Item> Items = new List<Item>();
            public readonly HashSet<Item> Known = new HashSet<Item>();
            public int Revision;

            public Set(Identifier tag) => Tag = tag;
        }

        private static readonly List<Set> sets = new List<Set>();
        private static Harmony owner;
        public static event Action Changed;
        public static event Action<Item> ItemChanged;

        public static void Attach(Harmony harmony) {
            if (owner != null) { return; }
            owner = harmony;
            harmony.Patch(AccessTools.Constructor(typeof(Item), new[] { typeof(Rectangle), typeof(ItemPrefab), typeof(Submarine), typeof(bool), typeof(ushort) }),
                postfix: ModuleHost.Bind(typeof(Tagged), nameof(ItemPostfix)));
            harmony.Patch(AccessTools.Method(typeof(Item), nameof(Item.Load), new[] { typeof(ContentXElement), typeof(Submarine), typeof(bool), typeof(IdRemap) }),
                postfix: ModuleHost.Bind(typeof(Tagged), nameof(LoadPostfix)));
            harmony.Patch(AccessTools.PropertySetter(typeof(Item), nameof(Item.Tags)),
                postfix: ModuleHost.Bind(typeof(Tagged), nameof(ItemPostfix)));
            harmony.Patch(AccessTools.Method(typeof(Item), nameof(Item.AddTag), new[] { typeof(Identifier) }),
                postfix: ModuleHost.Bind(typeof(Tagged), nameof(ItemPostfix)));
            foreach (Item item in Item.ItemList) { Track(item); }
        }

        public static void Release() {
            owner = null;
            Changed = null; ItemChanged = null;
            foreach (Set s in sets) {
                s.Items.Clear();
                s.Known.Clear();
            }
        }

        public static int Watch(Identifier tag) {
            for (int i = 0; i < sets.Count; i++) {
                if (sets[i].Tag == tag) { return i; }
            }
            var set = new Set(tag);
            sets.Add(set);
            foreach (Item item in Item.ItemList) {
                if (!item.Removed && item.HasTag(tag) && set.Known.Add(item)) { set.Items.Add(item); }
            }
            return sets.Count - 1;
        }

        public static IReadOnlyList<Item> Of(int handle) {
            Set s = sets[handle];
            for (int i = s.Items.Count - 1; i >= 0; i--) {
                Item item = s.Items[i];
                if (!item.Removed && item.HasTag(s.Tag)) { continue; }
                s.Known.Remove(item);
                s.Items.RemoveAt(i);
                s.Revision++;
            }
            return s.Items;
        }

        public static int Revision(int handle) => sets[handle].Revision;

        public static void ItemPostfix(Item __instance) { Track(__instance); if (__instance != null && !__instance.Removed) { Changed?.Invoke(); ItemChanged?.Invoke(__instance); } }

        public static void LoadPostfix(Item __result) { Track(__result); if (__result != null && !__result.Removed) { Changed?.Invoke(); ItemChanged?.Invoke(__result); } }

        private static void Track(Item item) {
            if (item == null || item.Removed) { return; }
            for (int i = 0; i < sets.Count; i++) {
                Set s = sets[i];
                if (item.HasTag(s.Tag) && s.Known.Add(item)) {
                    s.Items.Add(item);
                    s.Revision++;
                }
            }
        }
    }
}
