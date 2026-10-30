using Barotrauma;
using Microsoft.Xna.Framework;
using ScpItdMod.Rp;

namespace ScpItdMod.Chess {
    public static class ChessWorld {
        public static readonly Identifier Tag = "scpitd_chess".ToIdentifier();
        public static bool IsBoard(Item item) => item != null && !item.Removed && item.HasTag(Tag);
        public static bool CanUse(Character actor, Item item) => IsBoard(item) && item.Condition > 0 && item.ParentInventory == null &&
            RpWorld.Conscious(actor) && !actor.LockHands && !RpWorld.IsHidden(actor) && !RpWorld.IsSurrendered(actor) &&
            ReferenceEquals(actor.SelectedItem, item) && Vector2.DistanceSquared(actor.WorldPosition, item.WorldPosition) <= 160 * 160;
    }
}
