using System;
using System.Collections.Immutable;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace ScpItdMod.People {
    public static class LooksCodec {
        public static Appearance Capture(CharacterInfo info) {
            CharacterInfo.HeadInfo head = info?.Head;
            if (head?.Preset == null) { return default; }
            return new Appearance {
                Tags = string.Join(",", head.Preset.TagSet),
                Hair = (byte)Math.Clamp(head.HairIndex, 0, 255),
                Beard = (byte)Math.Clamp(head.BeardIndex, 0, 255),
                Moustache = (byte)Math.Clamp(head.MoustacheIndex, 0, 255),
                Face = (byte)Math.Clamp(head.FaceAttachmentIndex, 0, 255),
                Skin = head.SkinColor.PackedValue,
                HairColor = head.HairColor.PackedValue,
                FacialColor = head.FacialHairColor.PackedValue
            };
        }

        public static void Apply(CharacterInfo info, in Appearance a) {
            if (info == null || !a.Set) { return; }
            var tags = ImmutableHashSet.CreateBuilder<Identifier>();
            foreach (string tag in a.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries)) { tags.Add(tag.Trim().ToIdentifier()); }
            info.RecreateHead(tags.ToImmutable(), a.Hair, a.Beard, a.Moustache, a.Face);
            info.Head.SkinColor = Allowed(new Color(a.Skin), info.SkinColors, info.Head.SkinColor);
            info.Head.HairColor = Allowed(new Color(a.HairColor), info.HairColors, info.Head.HairColor);
            info.Head.FacialHairColor = Allowed(new Color(a.FacialColor), info.FacialHairColors, info.Head.FacialHairColor);
        }

        private static Color Allowed(Color wanted, ImmutableArray<(Color Color, float Commonness)> palette, Color fallback) {
            foreach ((Color c, float _) in palette) {
                if (c == wanted) { return wanted; }
            }
            return fallback;
        }
    }
}
