using System;
using Barotrauma;
using Microsoft.Xna.Framework;
using ScpItdMod.Core;

namespace ScpItdMod.Pc {
    public static class PrinterWorld {
        public static readonly Identifier Tag = PcWire.PrinterTag.ToIdentifier();
        public static Vector2 Output(Item item) {
            Rectangle slot = item.Prefab.ConfigElement.GetAttributeRect("scpitdoutput", Rectangle.Empty);
            Sprite sprite = item.Prefab.Sprite;
            Vector2 local = (new Vector2(slot.Center.X, slot.Bottom) - sprite.Origin) * item.Scale;
            float angle = ItemPose.Angle(item), cosine = MathF.Cos(angle), sine = MathF.Sin(angle);
            Vector2 offset = new Vector2(local.X * cosine - local.Y * sine, -local.X * sine - local.Y * cosine);
            return item.WorldPosition + offset;
        }
    }
}
