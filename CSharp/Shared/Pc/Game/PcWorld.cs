using Barotrauma;
using Microsoft.Xna.Framework;
using ScpItdMod.Auth;
using ScpItdMod.Rp;

namespace ScpItdMod.Pc {
    public static class PcWorld {
        public const float Reach = 260;
        private static readonly Identifier TerminalTag = PcWire.TerminalTag.ToIdentifier();
        public static bool CanUse(Character actor, Item item) =>
            RpWorld.Conscious(actor) && !actor.LockHands && !RpWorld.IsHidden(actor) && !RpWorld.IsSurrendered(actor) &&
            item != null && !item.Removed && item.HasTag(TerminalTag) &&
            Vector2.DistanceSquared(actor.WorldPosition, item.WorldPosition) <= Reach * Reach && actor.CanInteractWith(item);
        public static bool Available(Character actor, Item item) => CanUse(actor, item) && SwipeWorld.Powered(item);
    }
}
