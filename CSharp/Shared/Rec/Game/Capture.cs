using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace ScpItdMod.Rec {
    public sealed class Capture {
        private static readonly ConditionalWeakTable<Character, Serial> serials = new ConditionalWeakTable<Character, Serial>();
        private static uint nextSerial = 1;

        private sealed class Serial {
            public uint Value;
        }

        private sealed class Tracked {
            public int Look;
            public int Hash;
        }

        private readonly LookTable looks;
        private readonly Dictionary<Character, Tracked> tracked = new Dictionary<Character, Tracked>();
        private readonly List<Character> stale = new List<Character>();
        private readonly Func<Character, string> label;

        public Capture(LookTable looks, Func<Character, string> label) {
            this.looks = looks;
            this.label = label;
        }

        public static Subject SubjectOf(Character c) {
            Serial s = serials.GetValue(c, _ => new Serial { Value = nextSerial++ });
            return new Subject(c.ID, s.Value);
        }

        public static bool Recordable(Character c) =>
            c != null && !c.Removed && c.Enabled && c.IsHuman && c.AnimController != null;

        public bool Pose(Character c, out FigurePose p) {
            p = default;
            if (!Recordable(c)) { return false; }
            Limb[] limbs = c.AnimController.Limbs;
            Limb root = c.AnimController.MainLimb;
            if (limbs == null || root?.body == null) { return false; }
            Vector2 origin = Position(root);
            p.Subject = SubjectOf(c);
            p.Look = LookOf(c);
            p.X = origin.X;
            p.Y = origin.Y;
            PoseFlags flags = PoseFlags.None;
            if (c.AnimController.Dir < 0) { flags |= PoseFlags.Left; }
            if (c.IsDead) { flags |= PoseFlags.Dead; }
            if (c.IsKnockedDownOrRagdolled) { flags |= PoseFlags.Down; }
            if (c.HideFace) { flags |= PoseFlags.Masked; }
            if (c.AnimController.IsMovingFast) { flags |= PoseFlags.Running; }
            int n = 0;
            for (int i = 0; i < limbs.Length && n < FigurePose.MaxLimbs - FigurePose.HeldSlots; i++) {
                Limb l = limbs[i];
                if (l?.body == null || l.IsSevered) {
                    p.Missing |= (ushort)(1 << n++);
                    continue;
                }
                Vector2 d = Position(l) - origin;
                p.L.Set(n++, FigurePose.Q(d.X), FigurePose.Q(d.Y), FigurePose.QA(Rotation(l.body)));
            }
            Item right = c.Inventory?.GetItemInLimbSlot(InvSlotType.RightHand);
            Item left = c.Inventory?.GetItemInLimbSlot(InvSlotType.LeftHand);
            if (ReferenceEquals(left, right)) { left = null; }
            if (Held(right, origin, out short rx, out short ry, out short ra)) { flags |= PoseFlags.HoldRight; }
            p.L.Set(n++, rx, ry, ra);
            if (Held(left, origin, out short lx, out short ly, out short la)) { flags |= PoseFlags.HoldLeft; }
            p.L.Set(n++, lx, ly, la);
            p.Flags = flags;
            p.Limbs = n;
            return true;
        }

        private static bool Held(Item item, Vector2 origin, out short x, out short y, out short a) {
            x = y = a = 0;
            if (item == null || item.Removed) { return false; }
#if SERVER
            Vector2 at = item.WorldPosition;
#else
            Vector2 at = item.body != null ? item.body.DrawPosition : item.DrawPosition;
#endif
            float rotation = item.body != null ? Rotation(item.body) : -item.RotationRad;
            Vector2 d = at - origin;
            x = FigurePose.Q(d.X);
            y = FigurePose.Q(d.Y);
            a = FigurePose.QA(rotation);
            return true;
        }

        private static Vector2 Position(Limb limb) {
#if SERVER
            return limb.WorldPosition;
#else
            return limb.body.DrawPosition;
#endif
        }

        private static float Rotation(PhysicsBody body) {
#if SERVER
            return body.Rotation;
#else
            return body.DrawRotation;
#endif
        }

        public int LookOf(Character c) {
            string name = label?.Invoke(c) ?? c.Name;
            int hash = Hash(c) ^ (name?.GetHashCode() ?? 0) * 31;
            if (tracked.TryGetValue(c, out Tracked t) && t.Hash == hash && looks.Get(t.Look) != null) { return t.Look; }
            LookDesc d = Describe(c, name);
            int id = looks.Intern(d);
            if (t != null) { looks.Release(t.Look); } else { t = new Tracked(); tracked[c] = t; }
            t.Look = id;
            t.Hash = hash;
            return id;
        }

        public void Forget(Character c) {
            if (c == null || !tracked.Remove(c, out Tracked t)) { return; }
            looks.Release(t.Look);
        }

        public void Prune() {
            stale.Clear();
            foreach (KeyValuePair<Character, Tracked> pair in tracked) {
                if (pair.Key.Removed) { stale.Add(pair.Key); }
            }
            for (int i = 0; i < stale.Count; i++) { Forget(stale[i]); }
        }

        public void Clear() {
            foreach (Tracked t in tracked.Values) { looks.Release(t.Look); }
            tracked.Clear();
        }

        private static int Hash(Character c) {
            uint h = 2166136261u;
            CharacterInfo info = c.Info;
            if (info?.Head != null) {
                Mix(ref h, info.Head.Preset?.GetHashCode() ?? 0);
                Mix(ref h, info.Head.HairIndex);
                Mix(ref h, info.Head.BeardIndex);
                Mix(ref h, info.Head.MoustacheIndex);
                Mix(ref h, info.Head.FaceAttachmentIndex);
                Mix(ref h, (int)info.Head.SkinColor.PackedValue);
                Mix(ref h, (int)info.Head.HairColor.PackedValue);
                Mix(ref h, (int)info.Head.FacialHairColor.PackedValue);
            }
            Mix(ref h, c.HideFace ? 1 : 0);
            Mix(ref h, c.Name?.GetHashCode() ?? 0);
            if (c.Inventory != null) {
                Mix(ref h, Slot(c, InvSlotType.Head));
                Mix(ref h, Slot(c, InvSlotType.OuterClothes));
                Mix(ref h, Slot(c, InvSlotType.InnerClothes));
                Mix(ref h, Slot(c, InvSlotType.Bag));
                Mix(ref h, Slot(c, InvSlotType.RightHand));
                Mix(ref h, Slot(c, InvSlotType.LeftHand));
            }
            return (int)h;
        }

        private static void Mix(ref uint h, int value) => h = (h ^ (uint)value) * 16777619u;

        private static int Slot(Character c, InvSlotType slot) {
            Item item = c.Inventory.GetItemInLimbSlot(slot);
            if (item == null) { return 0; }
            int variant = Math.Clamp(item.GetComponent<Wearable>()?.Variant ?? 0, 0, 255);
            return item.ID | variant << 16;
        }

        public static LookDesc Describe(Character c, string label) {
            var d = new LookDesc { Label = label ?? "", Masked = c.HideFace };
            CharacterInfo.HeadInfo head = c.Info?.Head;
            if (head?.Preset != null && !c.HideFace) {
                d.HeadTags = string.Join(",", head.Preset.TagSet);
                d.Hair = (byte)Math.Clamp(head.HairIndex, 0, 255);
                d.Beard = (byte)Math.Clamp(head.BeardIndex, 0, 255);
                d.Moustache = (byte)Math.Clamp(head.MoustacheIndex, 0, 255);
                d.Face = (byte)Math.Clamp(head.FaceAttachmentIndex, 0, 255);
                d.Skin = head.SkinColor.PackedValue;
                d.HairColor = head.HairColor.PackedValue;
                d.FacialColor = head.FacialHairColor.PackedValue;
            }
            if (c.Inventory != null) {
                Put(d, WornSlot.Head, c.Inventory.GetItemInLimbSlot(InvSlotType.Head));
                Put(d, WornSlot.Outer, c.Inventory.GetItemInLimbSlot(InvSlotType.OuterClothes));
                Put(d, WornSlot.Inner, c.Inventory.GetItemInLimbSlot(InvSlotType.InnerClothes));
                Put(d, WornSlot.Bag, c.Inventory.GetItemInLimbSlot(InvSlotType.Bag));
                Item right = c.Inventory.GetItemInLimbSlot(InvSlotType.RightHand);
                Item left = c.Inventory.GetItemInLimbSlot(InvSlotType.LeftHand);
                Put(d, WornSlot.Right, right);
                if (!ReferenceEquals(left, right)) { Put(d, WornSlot.Left, left); }
            }
            return d.Seal();
        }

        private static void Put(LookDesc d, WornSlot slot, Item item) {
            if (item == null) { return; }
            d.Items[(int)slot] = item.Prefab.Identifier.Value;
            Wearable w = item.GetComponent<Wearable>();
            d.Variants[(int)slot] = (byte)Math.Clamp(w?.Variant ?? 0, 0, 255);
        }
    }
}
