using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using FarseerPhysics;
using Microsoft.Xna.Framework;

namespace ScpItdMod.Auth {
    public static class SwipeWorld {
        public static readonly Identifier ReaderTag = AuthWorld.ReaderTag.ToIdentifier();
        private static readonly Dictionary<Item, List<string>> grants = new Dictionary<Item, List<string>>();
        private static readonly List<string> tags = new List<string>(16);
        private static readonly List<Item> stale = new List<Item>();

        public static bool IsReader(Item item) => item != null && !item.Removed && item.HasTag(ReaderTag);

        public static void ClearGrants() => grants.Clear();

        public static bool Grant(Item card, string token) {
            if (card == null || card.Removed || string.IsNullOrWhiteSpace(token) || token.StartsWith(CardCredentials.NamePrefix, StringComparison.OrdinalIgnoreCase)) { return false; }
            if (!grants.TryGetValue(card, out List<string> list)) {
                list = new List<string>();
                grants[card] = list;
            }
            if (list.Contains(token) || list.Count >= 32) { return false; }
            list.Add(token);
            return true;
        }

        public static bool Revoke(Item card, string token) => card != null && grants.TryGetValue(card, out List<string> list) && list.Remove(token);

        public static IReadOnlyList<string> GrantsOf(Item card) => card != null && grants.TryGetValue(card, out List<string> list) ? list : null;

        public static void Prune() {
            stale.Clear();
            foreach (Item card in grants.Keys) {
                if (card.Removed) { stale.Add(card); }
            }
            for (int i = 0; i < stale.Count; i++) { grants.Remove(stale[i]); }
        }

        public static void CredentialsOf(Item card, Credentials into) {
            tags.Clear();
            if (card != null) {
                foreach (Identifier tag in card.GetTags()) { tags.Add(tag.Value); }
            }
            CardCredentials.Fill(into, tags, GrantsOf(card));
        }

        public static bool Powered(Item reader) {
            if (reader == null || reader.Removed || reader.Condition <= 0) { return false; }
            Powered powered = reader.GetComponent<Powered>();
            return powered == null || powered.HasPower;
        }

        public static Item Card(Character c) {
            if (c?.Inventory == null) { return null; }
            Item worn = c.Inventory.GetItemInLimbSlot(InvSlotType.Card);
            if (worn?.GetComponent<IdCard>() != null) { return worn; }
            foreach (Item held in c.HeldItems) {
                if (held.GetComponent<IdCard>() != null) { return held; }
            }
            return null;
        }

        public static bool Owns(Character c, Item card) =>
            c != null && card != null && !card.Removed && ReferenceEquals(card.GetRootInventoryOwner(), c);

        public static int FreeHand(Character c, Item card) {
            if (c?.Inventory == null) { return -1; }
            Item right = c.Inventory.GetItemInLimbSlot(InvSlotType.RightHand);
            Item left = c.Inventory.GetItemInLimbSlot(InvSlotType.LeftHand);
            if (right == null || ReferenceEquals(right, card)) { return 1; }
            if (left == null || ReferenceEquals(left, card)) { return 0; }
            return -1;
        }

        public static float ArmReach(Character c) {
            float arm = c?.AnimController?.ArmLength ?? 0.0f;
            return arm > 0.0f ? ConvertUnits.ToDisplayUnits(arm) : 80.0f;
        }

        public static float Reach(Character c, Item reader) => MathF.Max(reader?.InteractDistance ?? 0.0f, ArmReach(c) * 1.3f) + 20.0f;

        public static SwipeFacts Facts(Character actor, Item reader, Item card, bool busy, bool hidden) {
            var f = new SwipeFacts {
                ReaderExists = IsReader(reader),
                ActorConscious = actor != null && !actor.Removed && !actor.IsDead && !actor.IsIncapacitated && actor.Stun <= 0.0f,
                ActorBound = actor != null && actor.LockHands,
                ActorHidden = hidden,
                Busy = busy,
                HasCard = card != null && !card.Removed,
                CardOwned = Owns(actor, card),
                HandFree = FreeHand(actor, card) >= 0
            };
            if (f.ReaderExists && actor != null) {
                f.ReaderPowered = Powered(reader);
                f.Distance = Vector2.Distance(actor.WorldPosition, reader.WorldPosition);
                f.Reach = Reach(actor, reader);
            }
            return f;
        }

        public static float Approach(Character actor, Item reader) {
            float arm = actor?.AnimController?.ArmLength ?? 0.7f;
            float d = ConvertUnits.ToSimUnits(Vector2.Distance(actor.WorldPosition, reader.WorldPosition));
            return ReachTiming.For(d, arm).Approach;
        }
    }
}
