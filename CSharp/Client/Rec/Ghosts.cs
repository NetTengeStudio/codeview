using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Barotrauma;
using Barotrauma.Extensions;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;

namespace ScpItdMod.Rec {
    public static class Ghosts {
        public const int MaxRigs = Tape.MaxFigures * ScreenFrameWire.MaxViews;
        private const float DepthStep = 0.000001f;

        private sealed class Wear {
            public Sprite Sprite;
            public LimbType Limb;
            public WearableType Type;
            public bool InheritOrigin;
            public bool InheritSource;
            public bool InheritDepth;
            public bool InheritScale;
            public bool HideLimb;
            public bool HidesHair;
            public float Scale;
            public float Rotation;
            public Color Color;
            public int Rank;
        }

        private sealed class Rig {
            public string Key;
            public Sprite[] Limbs;
            public LimbType[] Types;
            public float[] Scale;
            public bool[] MirrorX;
            public bool[] Hidden;
            public Color Skin;
            public readonly List<Wear> Wear = new List<Wear>();
            public Wear HairWithHat;
            public bool HairHidden;
            public ItemPrefab Right;
            public ItemPrefab Left;
            public double Used;
        }

        private static readonly Dictionary<string, Rig> rigs = new Dictionary<string, Rig>(StringComparer.Ordinal);
        private static readonly HashSet<string> failedLooks = new HashSet<string>(StringComparer.Ordinal);
        private static RagdollParams ragdoll;

        public static int Count => rigs.Count;

        public static void Release() {
            foreach (Rig r in rigs.Values) { Free(r); }
            rigs.Clear();
            failedLooks.Clear();
            ragdoll = null;
        }

        public static void Draw(SpriteBatch sb, LookDesc look, in FigurePose p, Color tint, float depthBias = 0.0f) {
            if (look == null || p.Limbs <= FigurePose.HeldSlots) { return; }
            Rig rig = Get(look);
            if (rig == null) { return; }
            rig.Used = Timing.TotalTime;
            int limbCount = Math.Min(p.Limbs - FigurePose.HeldSlots, rig.Limbs.Length);
            bool left = (p.Flags & PoseFlags.Left) != 0;
            float dir = left ? -1.0f : 1.0f;
            for (int i = 0; i < limbCount; i++) {
                if (!p.HasBodyLimb(i)) { continue; }
                Sprite sprite = rig.Limbs[i];
                if (sprite?.Texture == null) { continue; }
                var world = new Vector2(p.X + p.L.X(i) / FigurePose.Unit, -(p.Y + p.L.Y(i) / FigurePose.Unit));
                float rotation = -p.L.A(i) * FigurePose.AngleUnit;
                SpriteEffects fx = left ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
                if (rig.MirrorX[i]) { fx = fx == SpriteEffects.None ? SpriteEffects.FlipHorizontally : SpriteEffects.None; }
                Vector2 origin = sprite.Origin;
                if (left) { origin.X = sprite.SourceRect.Width - origin.X; }
                LimbType type = rig.Types[i];
                bool hide = rig.Hidden[i];
                for (int w = 0; w < rig.Wear.Count && !hide; w++) {
                    if (rig.Wear[w].Limb == type && rig.Wear[w].HideLimb) { hide = true; }
                }
                float depth = sprite.Depth + depthBias;
                if (!hide) { sprite.Draw(sb, world, tint.Multiply(rig.Skin), origin, rotation, rig.Scale[i], fx, depth); }
                SpriteEffects wfx = left ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
                float step = DepthStep;
                for (int w = 0; w < rig.Wear.Count; w++) {
                    Wear wear = rig.Wear[w];
                    if (wear.Limb != type || wear.Sprite?.Texture == null) { continue; }
                    Wear drawn = wear;
                    if (wear.Type == WearableType.Hair && rig.HairHidden) {
                        if (rig.HairWithHat == null) { continue; }
                        drawn = rig.HairWithHat;
                    }
                    DrawWear(sb, drawn, sprite, world, rotation, rig.Scale[i], depth, step, dir, wfx, tint);
                    step += DepthStep;
                }
            }
            int held = p.Limbs - FigurePose.HeldSlots;
            if ((p.Flags & PoseFlags.HoldRight) != 0) { DrawHeld(sb, rig.Right, p, held, left, tint, depthBias); }
            if ((p.Flags & PoseFlags.HoldLeft) != 0) { DrawHeld(sb, rig.Left, p, held + 1, left, tint, depthBias); }
        }

        private static void DrawWear(SpriteBatch sb, Wear wear, Sprite limb, Vector2 world, float rotation, float limbScale, float limbDepth, float step, float dir, SpriteEffects fx, Color tint) {
            Sprite s = wear.Sprite;
            if (wear.InheritSource) { s.SourceRect = limb.SourceRect; }
            Vector2 origin;
            if (wear.InheritOrigin) {
                origin = limb.Origin;
                if (dir < 0) { origin.X = s.SourceRect.Width - origin.X; }
            } else {
                origin = s.Origin;
                if (dir < 0) { origin.X = s.SourceRect.Width - origin.X; }
            }
            float depth = wear.InheritDepth ? limbDepth - step - wear.Rank * DepthStep : s.Depth;
            float scale = wear.Scale * (wear.InheritScale ? limbScale : 1.0f);
            s.Draw(sb, world, tint.Multiply(wear.Color), origin, rotation - wear.Rotation * dir, scale, fx, depth);
        }

        private static void DrawHeld(SpriteBatch sb, ItemPrefab prefab, in FigurePose p, int slot, bool left, Color tint, float depthBias) {
            Sprite s = prefab?.Sprite;
            if (s?.Texture == null || slot >= p.Limbs) { return; }
            var world = new Vector2(p.X + p.L.X(slot) / FigurePose.Unit, -(p.Y + p.L.Y(slot) / FigurePose.Unit));
            float rotation = -p.L.A(slot) * FigurePose.AngleUnit;
            Vector2 origin = s.Origin;
            if (left) { origin.X = s.SourceRect.Width - origin.X; }
            s.Draw(sb, world, tint.Multiply(prefab.SpriteColor), origin, rotation, prefab.Scale, left ? SpriteEffects.FlipHorizontally : SpriteEffects.None, s.Depth + depthBias);
        }

        private static Rig Get(LookDesc look) {
            if (rigs.TryGetValue(look.Key, out Rig rig)) { return rig; }
            if (failedLooks.Contains(look.Key)) { return null; }
            try {
                rig = Build(look);
            } catch (Exception error) {
                ModLog.Once($"ghost figure '{look.Key}' cannot be built: {error.Message}");
                failedLooks.Add(look.Key);
                return null;
            }
            if (rig == null) { return null; }
            if (rigs.Count >= MaxRigs) { Evict(); }
            rigs[look.Key] = rig;
            return rig;
        }

        private static void Evict() {
            Rig oldest = null;
            foreach (Rig r in rigs.Values) {
                if (oldest == null || r.Used < oldest.Used) { oldest = r; }
            }
            if (oldest == null) { return; }
            rigs.Remove(oldest.Key);
            Free(oldest);
        }

        private static void Free(Rig r) {
            for (int i = 0; i < r.Limbs.Length; i++) { r.Limbs[i]?.Remove(); }
            foreach (Wear w in r.Wear) { w.Sprite?.Remove(); }
            r.HairWithHat?.Sprite?.Remove();
            r.Wear.Clear();
        }

        private static Rig Build(LookDesc d) {
            CharacterPrefab prefab = CharacterPrefab.HumanPrefab;
            if (prefab == null) {
                ModLog.Once("ghost figure cannot be built: human prefab is unavailable");
                return null;
            }
            ragdoll ??= RagdollParams.GetDefaultRagdollParams<HumanRagdollParams>(CharacterPrefab.HumanSpeciesName, prefab.ConfigElement, prefab.ContentPackage);
            var info = new CharacterInfo(CharacterPrefab.HumanSpeciesName);
            ImmutableHashSet<Identifier> tags = Tags(d.HeadTags, prefab);
            info.RecreateHead(tags, d.Hair, d.Beard, d.Moustache, d.Face);
            info.Head.SkinColor = new Color(d.Skin);
            info.Head.HairColor = new Color(d.HairColor);
            info.Head.FacialHairColor = new Color(d.FacialColor);
            int count = 0;
            foreach (RagdollParams.LimbParams lp in ragdoll.Limbs) { count = Math.Max(count, lp.ID + 1); }
            var rig = new Rig {
                Key = d.Key, Limbs = new Sprite[count], Types = new LimbType[count], Scale = new float[count],
                MirrorX = new bool[count], Hidden = new bool[count], Skin = d.Skin == 0 ? Color.White : new Color(d.Skin)
            };
            try {
                Populate(rig, d, prefab, info);
                return rig;
            } catch {
                Free(rig);
                throw;
            }
        }

        private static void Populate(Rig rig, LookDesc d, CharacterPrefab prefab, CharacterInfo info) {
            foreach (RagdollParams.LimbParams lp in ragdoll.Limbs) {
                RagdollParams.SpriteParams sp = lp.normalSpriteParams;
                if (sp?.Element == null || lp.ID < 0) { continue; }
                ContentPath tex = sp.Element.GetAttributeContentPath("texture");
                if (tex.IsNullOrEmpty()) { tex = ContentPath.FromRaw(sp.Element.ContentPackage ?? prefab.ContentPackage, sp.GetTexturePath()); }
                Sprite sprite = null;
                try {
                    sprite = new Sprite(sp.Element, file: Limb.GetSpritePath(tex, info), sourceRectScale: ragdoll.SourceRectScale);
                    if (lp.Type == LimbType.Head) {
                        Point sheet = info.Head.SheetIndex.ToPoint();
                        sprite.SourceRect = new Rectangle(CharacterInfo.CalculateOffset(sprite, sheet), sprite.SourceRect.Size);
                    }
                    rig.Limbs[lp.ID] = sprite;
                    sprite = null;
                } finally {
                    sprite?.Remove();
                }
                rig.Types[lp.ID] = lp.Type;
                rig.Scale[lp.ID] = lp.Scale * ragdoll.LimbScale * ragdoll.TextureScale;
                rig.MirrorX[lp.ID] = lp.MirrorHorizontally;
                rig.Hidden[lp.ID] = lp.Hide;
            }
            CharacterInfo.HeadInfo head = info.Head;
            Attach(rig, head.FaceAttachment, WearableType.FaceAttachment, info.Head.HairColor);
            Attach(rig, head.BeardElement, WearableType.Beard, head.FacialHairColor);
            Attach(rig, head.MoustacheElement, WearableType.Moustache, head.FacialHairColor);
            Attach(rig, head.HairElement, WearableType.Hair, head.HairColor);
            ContentXElement hat = head.HairWithHatElement?.GetChildElement("sprite");
            if (hat != null) { rig.HairWithHat = Attachment(hat, WearableType.Hair, head.HairColor); }
            int rank = 0;
            WornSlot[] order = { WornSlot.Inner, WornSlot.Outer, WornSlot.Head, WornSlot.Bag };
            foreach (WornSlot slot in order) {
                ItemPrefab item = ItemOf(d.Item(slot));
                if (item == null) { continue; }
                ContentXElement wearable = WearableElement(item);
                if (wearable == null) { continue; }
                foreach (ContentXElement s in wearable.Elements()) {
                    if (!s.Name.LocalName.Equals("sprite", StringComparison.OrdinalIgnoreCase)) { continue; }
                    Wear w = ItemWear(s, item, d.Variants[(int)slot], info);
                    if (w == null) { continue; }
                    w.Rank = (slot == WornSlot.Outer ? 1 : 0) + (slot == WornSlot.Bag ? 4 : 0) + rank;
                    rig.HairHidden |= w.HidesHair;
                    rig.Wear.Add(w);
                }
                rank++;
            }
            rig.Right = ItemOf(d.Item(WornSlot.Right));
            rig.Left = ItemOf(d.Item(WornSlot.Left));
        }

        private static ImmutableHashSet<Identifier> Tags(string headTags, CharacterPrefab prefab) {
            var b = ImmutableHashSet.CreateBuilder<Identifier>();
            if (!string.IsNullOrEmpty(headTags)) {
                foreach (string t in headTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) { b.Add(t.ToIdentifier()); }
            }
            if (b.Count == 0 && prefab.CharacterInfoPrefab?.Heads.Length > 0) { return prefab.CharacterInfoPrefab.Heads[0].TagSet; }
            return b.ToImmutable();
        }

        private static ItemPrefab ItemOf(string id) {
            if (string.IsNullOrEmpty(id)) { return null; }
            return ItemPrefab.Prefabs.TryGet(id.ToIdentifier(), out ItemPrefab p) ? p : null;
        }

        private static ContentXElement WearableElement(ItemPrefab item) {
            foreach (ContentXElement e in item.ConfigElement.Elements()) {
                if (e.Name.LocalName.Equals("wearable", StringComparison.OrdinalIgnoreCase)) { return e; }
            }
            return null;
        }

        private static void Attach(Rig rig, ContentXElement element, WearableType type, Color color) {
            if (element == null) { return; }
            foreach (ContentXElement s in element.Elements()) {
                if (!s.Name.LocalName.Equals("sprite", StringComparison.OrdinalIgnoreCase)) { continue; }
                Wear w = Attachment(s, type, color);
                if (w != null) { rig.Wear.Add(w); }
            }
        }

        private static Wear Attachment(ContentXElement s, WearableType type, Color color) {
            ContentPath tex = s.GetAttributeContentPath("texture");
            if (tex.IsNullOrEmpty()) { return null; }
            var wear = new Wear { Sprite = new Sprite(s, file: tex.Value), Limb = LimbType.Head, Type = type, Color = color };
            try { return Fill(wear, s, true); }
            catch { wear.Sprite?.Remove(); throw; }
        }

        private static Wear ItemWear(ContentXElement s, ItemPrefab item, int variant, CharacterInfo info) {
            string raw;
            if (s.DoesAttributeReferenceFileNameAlone("texture")) {
                ItemPrefab basePrefab = item.ParentPrefab ?? item;
                raw = System.IO.Path.GetDirectoryName(basePrefab.FilePath.Value) + "/" + s.GetAttributeString("texture", "");
            } else {
                raw = s.GetAttributeContentPath("texture")?.Value ?? "";
            }
            if (raw.Length == 0) { return null; }
            string path = info.ReplaceVars(raw).Replace("[VARIANT]", Math.Max(1, variant).ToString());
            if (!System.IO.File.Exists(path)) { path = info.ReplaceVars(raw).Replace("[VARIANT]", "1"); }
            var w = new Wear { Sprite = new Sprite(s, file: path), Type = WearableType.Item, Color = item.SpriteColor };
            try {
                w.Limb = Enum.TryParse(s.GetAttributeString("limb", "Head"), true, out LimbType limb) ? limb : LimbType.Head;
                w.HideLimb = s.GetAttributeBool("hidelimb", false);
                string[] hides = s.GetAttributeStringArray("hidewearablesoftype", null);
                if (hides != null) {
                    foreach (string h in hides) {
                        if (h.Equals("hair", StringComparison.OrdinalIgnoreCase)) { w.HidesHair = true; }
                    }
                }
                return Fill(w, s, false);
            } catch {
                w.Sprite?.Remove();
                throw;
            }
        }

        private static Wear Fill(Wear w, ContentXElement s, bool attachment) {
            w.InheritDepth = s.GetAttributeBool("inheritlimbdepth", true);
            w.InheritScale = s.GetAttribute("inheritscale") != null ? s.GetAttributeBool("inheritscale", attachment) : s.GetAttributeBool("inherittexturescale", attachment);
            w.InheritOrigin = s.GetAttributeBool("inheritorigin", attachment);
            w.InheritSource = s.GetAttributeBool("inheritsourcerect", attachment);
            w.Scale = s.GetAttributeFloat("scale", 1.0f);
            w.Rotation = MathHelper.ToRadians(s.GetAttributeFloat("rotation", 0.0f));
            return w;
        }
    }
}
