using System;
using Barotrauma;
using HarmonyLib;
using ScpItdMod.Core;

namespace ScpItdMod.Rp {
    public static class RpAccess {
        [ThreadStatic] private static int openingContainer;

        public static void Attach(Harmony harmony) {
            harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.IsInventoryAccessibleTo)),
                postfix: ModuleHost.Bind(typeof(RpAccess), nameof(AccessiblePostfix)));
            harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.CanAccessInventory)),
                prefix: ModuleHost.Bind(typeof(RpAccess), nameof(OpenPrefix)),
                finalizer: ModuleHost.Bind(typeof(RpAccess), nameof(OpenFinalizer)));
            harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.CanInteractWith), new[] { typeof(Item), typeof(float).MakeByRefType(), typeof(bool) }),
                postfix: ModuleHost.Bind(typeof(RpAccess), nameof(InteractPostfix)));
        }

        public static bool Governs(Character owner, Character viewer) =>
            owner != null && viewer != null && !ReferenceEquals(owner, viewer) && !owner.IsDead && !owner.IsBot && owner.IsHuman;

        public static void AccessiblePostfix(Character __instance, Character character, ref bool __result) {
            if (!Governs(__instance, character)) { return; }
            __result = __instance.Inventory != null && RpWorld.Permitted(character, __instance) && SearchRules.CanSearch(RpWorld.StateOf(__instance));
        }

        public static void OpenPrefix(Inventory inventory, out bool __state) {
            __state = inventory?.Owner is Item;
            if (__state) { openingContainer++; }
        }

        public static void OpenFinalizer(bool __state) {
            if (__state && openingContainer > 0) { openingContainer--; }
        }

        public static void InteractPostfix(Character __instance, Item item, ref bool __result) {
            if (!__result || openingContainer > 0 || item?.ParentInventory == null) { return; }
            if (!(item.GetRootInventoryOwner() is Character owner) || !Governs(owner, __instance)) { return; }
            SlotKind kind = RpWorld.SlotOf(item, owner);
            __result = SearchRules.Take(kind, RpWorld.StateOf(owner), RpWorld.Permitted(__instance, owner)) == TakeDeny.None;
        }
    }
}
