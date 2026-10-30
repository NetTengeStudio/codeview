using System;
using System.Collections.Generic;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;
using ScpItdMod.Rendering;

namespace ScpItdMod.Paper {
    public static class Faces {
        private const int Budget = 40, BakeScale = 2;
        private const long SurfaceBytes = (long)Sheet.Width * Sheet.Height * BakeScale * BakeScale * 4;
        private const int BakesPerFrame = 2;
        private const double Evict = 30.0;
        private const double Recheck = 6.0;
        private const double AskEvery = 0.25;
        private const float IconTurn = 0.2434f;
        private const int Margin = 200;

        private sealed class Entry {
            public Item Item;
            public uint Rev = PaperRules.Unknown;
            public string Body = "";
            public string Marks = "";
            public bool Dirty;
            public double Seen = double.NegativeInfinity;
            public double Asked = double.NegativeInfinity;
            public RenderSurface FrontSurface, BackSurface;
            public RenderTarget2D Front => FrontSurface?.Target;
            public RenderTarget2D Back => BackSurface?.Target;
            public Sprite World;
            public Sprite Mirror;
            public Sprite Icon;
            public Sprite PreviousIcon;
            public bool IconOwned;
            public bool BackDirty, HasFrame;
            public Glyph[] Glyphs = new Glyph[64];
        }

        private static readonly List<Item> papers = new List<Item>();
        private static readonly Dictionary<Item, Entry> entries = new Dictionary<Item, Entry>();
        private static readonly List<Entry> baked = new List<Entry>();
        private static readonly FaceAsk[] asks = new FaceAsk[PaperRules.MaxAsks];
        private static readonly Mark[] marks = new Mark[Sheet.MaxMarks];
        private static readonly InkLayout layout = new InkLayout();
        private static AccessTools.FieldRef<Item, Sprite> activeSprite;
        private static Action<Sprite, Texture2D> setTexture;
        private static SpriteBatch batch;
        private static double lastAsk = double.NegativeInfinity;

        public static Func<ushort, bool> Stamping = _ => false;

        public static void Attach(Harmony harmony) {
            activeSprite = AccessTools.FieldRefAccess<Item, Sprite>("activeSprite");
            var setter = AccessTools.PropertySetter(typeof(Sprite), "texture") ?? throw new MissingMethodException(typeof(Sprite).FullName, "set_texture");
            setTexture = (Action<Sprite, Texture2D>)setter.CreateDelegate(typeof(Action<Sprite, Texture2D>));
            harmony.Patch(AccessTools.Constructor(typeof(Item), new[] { typeof(Rectangle), typeof(ItemPrefab), typeof(Submarine), typeof(bool), typeof(ushort) }),
                postfix: ModuleHost.Bind(typeof(Faces), nameof(ItemPostfix)));
            harmony.Patch(AccessTools.Method(typeof(Item), nameof(Item.SetActiveSprite), Type.EmptyTypes),
                postfix: ModuleHost.Bind(typeof(Faces), nameof(SetActiveSpritePostfix)));
            foreach (Item item in Item.ItemList) { Track(item); }
        }

        public static void Reset() {
            for (int i = baked.Count - 1; i >= 0; i--) { Drop(baked[i]); }
            baked.Clear();
            entries.Clear();
            papers.RemoveAll(p => p.Removed);
        }

        public static void Release() {
            Reset();
            papers.Clear();
            batch?.Dispose();
            batch = null;
        }

        public static void ItemPostfix(Item __instance) => Track(__instance);

        private static void Track(Item item) {
            if (item?.Prefab != null && item.Prefab.Tags.Contains(PaperModule.PaperTag)) { papers.Add(item); }
        }

        public static void SetActiveSpritePostfix(Item __instance) {
            if (__instance == null || !entries.TryGetValue(__instance, out Entry entry) || entry.World == null) { return; }
            Apply(entry);
        }

        public static void OnFace(in PaperFace face) {
            if (!(Entity.FindEntityByID(face.Item) is Item item) || !PaperModule.IsPaper(item)) { return; }
            Entry entry = Get(item);
            if (face.Rev != entry.Rev) {
                entry.Rev = face.Rev;
                entry.Body = face.Body ?? "";
                entry.Marks = face.Marks ?? "";
                entry.Dirty = true;
            }
            entry.Asked = Timing.TotalTime;
            if (face.Thump && !Stamping(face.Item)) { StampHand.Thump(item.WorldPosition); }
        }

        public static void Local(Item item, string body, string marksText, uint rev) {
            if (item == null) { return; }
            Entry entry = Get(item);
            body ??= "";
            marksText ??= "";
            if (entry.Rev == rev && string.Equals(entry.Marks, marksText, StringComparison.Ordinal) && string.Equals(entry.Body, body, StringComparison.Ordinal)) { return; }
            entry.Rev = rev;
            entry.Body = body;
            entry.Marks = marksText;
            entry.Dirty = true;
        }

        private static Entry Get(Item item) {
            if (!entries.TryGetValue(item, out Entry entry)) {
                entry = new Entry { Item = item };
                entries.Add(item, entry);
            }
            return entry;
        }

        public static void Frame() {
            if (GameMain.GameScreen?.Cam is not Camera cam || GameMain.GameSession == null) { return; }
            double now = Timing.TotalTime;
            Rectangle w = cam.WorldView;
            var view = new Rectangle(w.X - Margin, w.Y + Margin, w.Width + Margin * 2, w.Height + Margin * 2);
            Character me = Character.Controlled;
            int ask = 0;
            int bakes = 0;
            for (int i = papers.Count - 1; i >= 0; i--) {
                Item item = papers[i];
                if (item.Removed) {
                    papers.RemoveAt(i);
                    if (entries.Remove(item, out Entry gone)) { Drop(gone); }
                    continue;
                }
                bool visible = Visible(item, me, view);
                entries.TryGetValue(item, out Entry entry);
                if (!visible) {
                    if (entry?.Front != null && now - entry.Seen > Evict) { Drop(entry); }
                    continue;
                }
                entry ??= Get(item);
                if ((entry.Rev == PaperRules.Unknown || now - entry.Seen > Recheck) && now - entry.Asked > 2.0 && ask < asks.Length) {
                    asks[ask++] = new FaceAsk { Item = item.ID, Rev = entry.Rev };
                    entry.Asked = now;
                }
                entry.Seen = now;
                if ((entry.Dirty || entry.Front?.IsDisposed == true || entry.Front?.IsContentLost == true || entry.Front != null && !ReferenceEquals(entry.Front.GraphicsDevice, GameMain.Instance.GraphicsDevice)) && bakes < BakesPerFrame) {
                    Bake(entry);
                    bakes++;
                }
                if (entry.World != null) { Orient(entry); }
            }
            if (ask > 0 && now - lastAsk >= AskEvery && GameMain.Client != null) {
                lastAsk = now;
                PaperClient.AskFaces(new ReadOnlySpan<FaceAsk>(asks, 0, ask));
            }
        }

        private static bool Visible(Item item, Character me, Rectangle view) {
            if (item.HiddenInGame) { return false; }
            Inventory parent = item.ParentInventory;
            if (parent == null) { return Inside(view, item.WorldPosition); }
            Entity root = item.GetRootInventoryOwner();
            if (ReferenceEquals(root, me)) { return true; }
            if (root is Character holder) { return PaperModule.InHands(holder, item) && Inside(view, item.WorldPosition); }
            return me?.SelectedItem != null && ReferenceEquals(root, me.SelectedItem);
        }

        private static bool Inside(Rectangle view, Vector2 at) =>
            at.X >= view.X && at.X <= view.Right && at.Y <= view.Y && at.Y >= view.Y - view.Height;

        private static void Bake(Entry entry) {
            if (entry.Body.Length == 0 && entry.Marks.Length == 0) {
                Drop(entry); entry.Dirty = false;
                return;
            }
            GraphicsDevice device = GameMain.Instance?.GraphicsDevice;
            Sprite prefab = entry.Item.Prefab.Sprite;
            Texture2D paper = TextureStore.File(Sheet.Art);
            if (device == null || paper == null || !Script.Hand.Ready || !Script.Heading.Ready || setTexture == null || activeSprite == null) { return; }
            if (entry.Front?.IsDisposed == true || entry.Front != null && !ReferenceEquals(entry.Front.GraphicsDevice, device)) { Drop(entry); }
            if (entry.Front == null) {
                if (baked.Count >= Budget) { DropOldest(); }
                while (RenderSurface.Paper.Bytes + SurfaceBytes > RenderSurface.Paper.Limit) { DropOldest(); }
                entry.FrontSurface = new RenderSurface(device, Sheet.Width * BakeScale, Sheet.Height * BakeScale, RenderSurface.Paper);
                entry.World = Wrap(entry.Front, prefab, 0.0f, new Vector2(Sheet.Width, Sheet.Height));
                entry.Icon = Wrap(entry.Front, prefab, IconTurn, Fit(IconTurn));
                entry.Icon.Origin = new Vector2(Sheet.Width * 0.5f, Sheet.Height * 0.5f);
                baked.Add(entry);
            }
            uint atlas = Script.AtlasFor(BakeScale);
            Script.Hand.Advance(' ');
            layout.Build(entry.Body, Sheet.BodyWidth, Script.Hand);
            int n = Script.Hand.Build(entry.Body, layout, entry.Item.ID, ReadOnlySpan<uint>.Empty, atlas, ref entry.Glyphs);
            int m = Math.Max(0, Marks.Parse(entry.Marks, marks));
            using var pass = GraphicsPass.Capture(device);
            SpriteBatches.Ensure(ref batch, device);
            {
                device.SetRenderTarget(entry.Front);
                device.Clear(Color.Transparent);
                batch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.LinearClamp, null, null, null, Matrix.CreateScale(BakeScale));
                batch.Draw(paper, Vector2.Zero, prefab.SourceRect, Color.White);
                Script.DrawHeader(batch, Vector2.Zero, 1, 0);
                Script.Draw(batch, new ReadOnlySpan<Glyph>(entry.Glyphs, 0, n), Vector2.Zero, 1.0f, 0.0f, Script.InkColor, ReadOnlySpan<float>.Empty, 0.0f);
                for (int i = 0; i < m; i++) { Script.DrawMark(batch, marks[i], Vector2.Zero, 1.0f, 0.0f, 1.0f, entry.Item.ID); }
                batch.End();
            }
            entry.Dirty = false; entry.HasFrame = true;
            entry.BackDirty = true;
            Apply(entry);
        }

        private static void Orient(Entry entry) {
            Item item = entry.Item;
            PhysicsBody body = item.body;
            bool mirrored = body != null && body.Enabled && body.BodyType != FarseerPhysics.BodyType.Static && body.Dir < 0.0f;
            entry.World.effects = item.SpriteEffects;
            Sprite want = mirrored ? MirrorOf(entry) : entry.World;
            if (want != null && !ReferenceEquals(activeSprite(item), want)) { activeSprite(item) = want; }
        }

        private static Sprite MirrorOf(Entry entry) {
            GraphicsDevice device = GameMain.Instance?.GraphicsDevice;
            if (device == null) { return entry.World; }
            if (entry.Back == null) {
                while (RenderSurface.Paper.Bytes + SurfaceBytes > RenderSurface.Paper.Limit) { DropOldest(entry); }
                entry.BackSurface = new RenderSurface(device, Sheet.Width * BakeScale, Sheet.Height * BakeScale, RenderSurface.Paper);
                entry.Mirror = Wrap(entry.Back, entry.Item.Prefab.Sprite, 0.0f, new Vector2(Sheet.Width, Sheet.Height));
                entry.BackDirty = true;
            }
            if (entry.BackDirty || entry.Back.IsContentLost) {
                using var pass = GraphicsPass.Capture(device);
                SpriteBatches.Ensure(ref batch, device);
                {
                    device.SetRenderTarget(entry.Back);
                    device.Clear(Color.Transparent);
                    batch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp);
                    batch.Draw(entry.Front, Vector2.Zero, null, Color.White, 0.0f, Vector2.Zero, 1.0f, SpriteEffects.FlipHorizontally, 0.0f);
                    batch.End();
                }
                entry.BackDirty = false;
            }
            return entry.Mirror;
        }

        private static void Apply(Entry entry) {
            if (!entry.HasFrame || entry.World == null || entry.Item.Removed) { return; }
            activeSprite(entry.Item) = entry.World;
            if (!ReferenceEquals(entry.Item.OverrideInventorySprite, entry.Icon)) {
                entry.PreviousIcon = entry.Item.OverrideInventorySprite; entry.IconOwned = true;
                entry.Item.OverrideInventorySprite = entry.Icon;
            }
        }

        private static Sprite Wrap(Texture2D texture, Sprite prefab, float turn, Vector2 size) {
            var sprite = new PaperSprite(texture, turn) {
                size = size,
                Depth = prefab.Depth
            };
            sprite.Origin = prefab.Origin;
            return sprite;
        }

        private sealed class PaperSprite : Sprite {
            public PaperSprite(Texture2D texture, float turn) : base(texture, texture.Bounds, null, turn) { }
            public override void Draw(ISpriteBatch spriteBatch, Vector2 position, Color color, Vector2 origin, float rotation, Vector2 scale, SpriteEffects effects = SpriteEffects.None, float? depth = null) =>
                base.Draw(spriteBatch, position, color, origin * BakeScale, rotation, scale / BakeScale, effects, depth);
        }

        private static Vector2 Fit(float turn) {
            float cos = MathF.Abs(MathF.Cos(turn));
            float sin = MathF.Abs(MathF.Sin(turn));
            return new Vector2(Sheet.Width * cos + Sheet.Height * sin, Sheet.Width * sin + Sheet.Height * cos);
        }

        private static void DropOldest(Entry keep = null) {
            Entry oldest = null;
            for (int i = 0; i < baked.Count; i++) {
                if (ReferenceEquals(baked[i], keep)) { continue; }
                if (oldest == null || baked[i].Seen < oldest.Seen) { oldest = baked[i]; }
            }
            if (oldest == null) { throw new InvalidOperationException("Paper surface budget cannot fit one document."); }
            Drop(oldest);
        }

        private static void Drop(Entry entry) {
            if (entry.Front == null) { return; }
            baked.Remove(entry);
            Item item = entry.Item;
            Sprite current = item.Removed || activeSprite == null ? null : activeSprite(item);
            bool ours = current != null && (ReferenceEquals(current, entry.World) || ReferenceEquals(current, entry.Mirror));
            if (entry.IconOwned && ReferenceEquals(item.OverrideInventorySprite, entry.Icon)) { item.OverrideInventorySprite = entry.PreviousIcon; }
            entry.PreviousIcon = null; entry.IconOwned = false;
            Unwrap(entry.World);
            Unwrap(entry.Mirror);
            Unwrap(entry.Icon);
            entry.World = entry.Mirror = entry.Icon = null;
            entry.FrontSurface.Dispose();
            entry.BackSurface?.Dispose();
            entry.FrontSurface = entry.BackSurface = null; entry.HasFrame = false;
            entry.Dirty = entry.Body.Length > 0 || entry.Marks.Length > 0;
            if (ours) { item.SetActiveSprite(); }
        }

        private static void Unwrap(Sprite sprite) {
            if (sprite == null) { return; }
            setTexture(sprite, null);
            sprite.Remove();
        }
    }
}
