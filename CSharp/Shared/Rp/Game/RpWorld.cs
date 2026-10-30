using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace ScpItdMod.Rp {
    public static class RpWorld {
        public static readonly Identifier CuffTag = "handlocker".ToIdentifier();
        public static readonly Identifier LockerTag = "locker".ToIdentifier();
        public static readonly Identifier HideoutTag = "scpitd_hideout".ToIdentifier();
        public const string CapacityPrefix = "scpitd_capacity_";

        private static readonly HashSet<Character> surrendered = new HashSet<Character>();
        private static readonly Dictionary<Character, Item> hidden = new Dictionary<Character, Item>();
        private static readonly Dictionary<Character, Permit> permits = new Dictionary<Character, Permit>();
#if CLIENT
        private static readonly HashSet<ushort> surrenderedState = new HashSet<ushort>();
        private static readonly HashSet<ushort> hiddenState = new HashSet<ushort>();
#endif

        private struct Permit {
            public Character Target;
            public double Until;
        }

        public static IEnumerable<Character> Surrendered => surrendered;

        public static IReadOnlyDictionary<Character, Item> Hidden => hidden;

        public static int Revision { get; private set; }

        public static void Clear() {
            surrendered.Clear();
            hidden.Clear();
            permits.Clear();
#if CLIENT
            surrenderedState.Clear(); hiddenState.Clear();
#endif
            Revision++;
        }

#if CLIENT
        public static bool IsSurrendered(Character c) => c != null && !c.Removed && surrenderedState.Contains(c.ID);
        public static bool IsHidden(Character c) => c != null && !c.Removed && hiddenState.Contains(c.ID);
#else
        public static bool IsSurrendered(Character c) => c != null && surrendered.Contains(c);
        public static bool IsHidden(Character c) => c != null && hidden.ContainsKey(c);
#endif

        public static Item HideoutOf(Character c) => c != null && hidden.TryGetValue(c, out Item item) ? item : null;

        public static bool SetSurrendered(Character c, bool value) {
            bool changed = value ? surrendered.Add(c) : surrendered.Remove(c);
            if (changed) { Revision++; }
            return changed;
        }

        public static void SetHidden(Character c, Item hideout) {
            if (hideout == null) { hidden.Remove(c); } else { hidden[c] = hideout; }
            Revision++;
        }

        public static void Grant(Character searcher, Character target, double until) {
            permits[searcher] = new Permit { Target = target, Until = until };
            Revision++;
        }

        public static void Revoke(Character searcher) {
            if (permits.Remove(searcher)) { Revision++; }
        }

        public static Character PermitTarget(Character searcher) =>
            searcher != null && permits.TryGetValue(searcher, out Permit p) ? p.Target : null;

        public static bool Permitted(Character searcher, Character target) {
            if (searcher == null || target == null || !permits.TryGetValue(searcher, out Permit permit)) { return false; }
            if (!ReferenceEquals(permit.Target, target) || Timing.TotalTime > permit.Until) { return false; }
            float range = RpRules.Range(RpAct.Search) * SearchRules.PermitRangeFactor;
            return Vector2.DistanceSquared(searcher.WorldPosition, target.WorldPosition) <= range * range;
        }

        public static void ExpirePermits(List<Character> expired) {
            expired.Clear();
            foreach (KeyValuePair<Character, Permit> pair in permits) {
                Character s = pair.Key;
                Character t = pair.Value.Target;
                bool stale = s.Removed || t == null || t.Removed || !Conscious(s) || Timing.TotalTime > pair.Value.Until;
                if (!stale) {
                    float range = RpRules.Range(RpAct.Search) * SearchRules.PermitRangeFactor;
                    stale = Vector2.DistanceSquared(s.WorldPosition, t.WorldPosition) > range * range || !SearchRules.CanSearch(StateOf(t));
                }
                if (stale) { expired.Add(s); }
            }
            for (int i = 0; i < expired.Count; i++) { permits.Remove(expired[i]); }
            if (expired.Count > 0) { Revision++; }
        }

        public static TargetState StateOf(Character c) =>
            SearchRules.State(c == null || c.IsDead, Conscious(c), c != null && c.LockHands, IsSurrendered(c));

        public static SlotKind SlotOf(Item item, Character owner) {
            if (item == null || owner?.Inventory == null) { return SlotKind.Other; }
            if (!ReferenceEquals(item.ParentInventory, owner.Inventory)) { return SlotKind.Nested; }
            int index = owner.Inventory.FindIndex(item);
            if (index < 0 || index >= owner.Inventory.SlotTypes.Length) { return SlotKind.Other; }
            return Kind(owner.Inventory.SlotTypes[index]);
        }

        public static SlotKind Kind(InvSlotType type) => type switch {
            InvSlotType.RightHand or InvSlotType.LeftHand => SlotKind.Hand,
            InvSlotType.Any => SlotKind.Pocket,
            InvSlotType.Head => SlotKind.Head,
            InvSlotType.OuterClothes => SlotKind.Outer,
            InvSlotType.InnerClothes => SlotKind.Inner,
            InvSlotType.Headset => SlotKind.Headset,
            InvSlotType.Card => SlotKind.Card,
            InvSlotType.Bag => SlotKind.Bag,
            _ => SlotKind.Other
        };

#if CLIENT
        public static void ReplaceState(List<ushort> surrenderedIds, List<ushort> hiddenIds) {
            surrenderedState.Clear(); hiddenState.Clear();
            for (int i = 0; i < surrenderedIds.Count; i++) { if (surrenderedIds[i] != 0) { surrenderedState.Add(surrenderedIds[i]); } }
            for (int i = 0; i < hiddenIds.Count; i++) { if (hiddenIds[i] != 0) { hiddenState.Add(hiddenIds[i]); } }
            Revision++;
        }
#endif

        public static void CollectState(List<ushort> surrenderedIds, List<ushort> hiddenIds) {
            surrenderedIds.Clear();
            hiddenIds.Clear();
            foreach (Character c in surrendered) { surrenderedIds.Add(c.ID); }
            foreach (Character c in hidden.Keys) { hiddenIds.Add(c.ID); }
        }

        public static bool IsHideout(Item item) {
            if (item == null || item.Removed) { return false; }
            if (item.HasTag(HideoutTag)) { return true; }
            return item.GetComponent<ItemContainer>() != null && item.HasTag(LockerTag) &&
                   !item.Prefab.Identifier.Value.Contains("window", StringComparison.OrdinalIgnoreCase);
        }

        public static int Capacity(Item hideout) {
            if (hideout == null) { return 0; }
            foreach (Identifier tag in hideout.GetTags()) {
                string v = tag.Value;
                if (v.StartsWith(CapacityPrefix, StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(v.AsSpan(CapacityPrefix.Length), out int n) && n > 0) {
                    return Math.Min(n, 8);
                }
            }
            return 1;
        }

        public static int Occupants(Item hideout, Character except) {
            int n = 0;
            foreach (KeyValuePair<Character, Item> pair in hidden) {
                if (ReferenceEquals(pair.Value, hideout) && !ReferenceEquals(pair.Key, except)) { n++; }
            }
            return n;
        }

        public static bool Occupied(Item hideout, Character except) => Occupants(hideout, except) >= Capacity(hideout);

        public static Item HeldCuffs(Character c) {
            if (c == null) { return null; }
            foreach (Item item in c.HeldItems) {
                if (item.HasTag(CuffTag)) { return item; }
            }
            return null;
        }

        public static bool IsCuffs(Item item) => item != null && !item.Removed && item.HasTag(CuffTag);

        public static Item BoundCuffs(Character c) {
            if (c?.Inventory == null) { return null; }
            Item right = c.Inventory.GetItemInLimbSlot(InvSlotType.RightHand);
            if (right != null && right.HasTag(CuffTag)) { return right; }
            Item left = c.Inventory.GetItemInLimbSlot(InvSlotType.LeftHand);
            return left != null && left.HasTag(CuffTag) ? left : null;
        }

        public static bool Owns(Character c, Item item) =>
            c != null && item != null && !item.Removed && ReferenceEquals(item.GetRootInventoryOwner(), c);

        public static bool Holds(Character c, Item item) {
            if (c == null || item == null) { return false; }
            foreach (Item held in c.HeldItems) {
                if (ReferenceEquals(held, item)) { return true; }
            }
            return false;
        }

        public static Item FirstHeld(Character c) {
            if (c == null) { return null; }
            foreach (Item held in c.HeldItems) {
                if (!held.HasTag(CuffTag)) { return held; }
            }
            return null;
        }

        public static bool Conscious(Character c) => c != null && !c.Removed && !c.IsDead && !c.IsIncapacitated && c.Stun <= 0.0f;

        public static bool CanReceive(Character c, Item item) => c?.Inventory != null && item != null && c.Inventory.CanBePut(item);

        public static RpFacts Facts(Character actor, Entity target, Item item, bool busy) {
            var f = new RpFacts {
                SelfConscious = Conscious(actor),
                SelfBound = actor != null && actor.LockHands,
                SelfHidden = IsHidden(actor),
                SelfBusy = busy,
                HoldingItem = item != null && Owns(actor, item) && !item.HasTag(CuffTag),
                HoldingCuffs = IsCuffs(item) ? Owns(actor, item) : HeldCuffs(actor) != null,
                CanDiagnose = Med.MedWorld.CanDiagnose(actor),
                HasTarget = target != null && !target.Removed
            };
            if (!f.HasTarget || actor == null) { return f; }
            f.TargetIsSelf = ReferenceEquals(target, actor);
            f.Distance = Vector2.Distance(actor.WorldPosition, target.WorldPosition);
            if (target is Character c) {
                f.TargetIsCharacter = true;
                f.TargetAlive = !c.IsDead;
                f.TargetConscious = Conscious(c);
                f.TargetBound = c.LockHands;
                f.TargetSurrendered = IsSurrendered(c);
                f.TargetHandsFree = item != null && CanReceive(c, item);
            } else if (target is Item hideout) {
                f.TargetIsHideout = IsHideout(hideout);
                f.HideoutOccupied = Occupied(hideout, actor);
            }
            return f;
        }
    }
}
