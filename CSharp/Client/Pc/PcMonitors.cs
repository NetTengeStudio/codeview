using ScpItdMod.Chess;
using ScpItdMod.Durak;
using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Lights;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Auth;
using ScpItdMod.Core;
using ScpItdMod.Xp;
using ScpItdMod.Rendering;

namespace ScpItdMod.Pc {
    public static class PcMonitors {
        private sealed class Surface {
            private readonly RenderSurface buffer;
            public RenderTarget2D Texture => buffer.Target;
            public readonly Sprite Sprite;
            public Surface(GraphicsDevice device, Sprite original) {
                Rectangle source = original.SourceRect;
                buffer = new RenderSurface(device, source.Width, source.Height, RenderSurface.Monitors);
                Sprite = new Sprite(Texture, Texture.Bounds, original.offset, original.rotation) {
                    Origin = original.Origin, Depth = original.Depth, size = original.size, effects = original.effects
                };
            }
            public void Remove() { detachTexture(Sprite, null); Sprite.Remove(); GC.SuppressFinalize(Sprite); buffer.Dispose(); }
        }

        private sealed class Profile {
            public Sprite Original;
            public MonitorGlass Glass;
            public MonitorBand[] Bands;
            public Texture2D Texture;
            public Rectangle Source;
            public string Definition;
            public bool Valid;
        }

        private sealed class Entry {
            public Item Item;
            public Profile Profile;
            public Surface Surface;
            public readonly DisplayView View = new DisplayView();
            public LightSource Light;
            public uint Painted, PaintedEpoch, PaintedCamera;
            public Sprite PreviousSprite;
            public bool HasFrame;
            public double Seen;
            public float Rotation = float.NaN, Scale;
            public SpriteEffects Effects;
            public Vector2 LightOffset;
        }

        private static readonly Dictionary<ItemPrefab, Profile> profiles = new Dictionary<ItemPrefab, Profile>();
        private static readonly Dictionary<Item, Entry> known = new Dictionary<Item, Entry>();
        private static readonly List<Entry> entries = new List<Entry>();
        private static readonly Identifier TerminalTag = PcWire.TerminalTag.ToIdentifier();
        private static AccessTools.FieldRef<Item, Sprite> activeSprite;
        private static SpriteBatch batch;
        private static RenderSurface scratchSurface;
        private static RenderTarget2D scratch => scratchSurface?.Target;
        private static Action<Sprite, Texture2D> detachTexture;
        private static int terminals;

        public static void Attach(Harmony harmony) {
            terminals = Tagged.Watch(TerminalTag);
            detachTexture = (Action<Sprite, Texture2D>)AccessTools.PropertySetter(typeof(Sprite), "texture").CreateDelegate(typeof(Action<Sprite, Texture2D>));
            activeSprite = AccessTools.FieldRefAccess<Item, Sprite>("activeSprite");
            ModNet.ReceiveSpan(DisplayWire.Channel, DisplayWire.MaxReply, Receive);
            harmony.Patch(AccessTools.Method(typeof(Item), nameof(Item.SetActiveSprite), Type.EmptyTypes), postfix: ModuleHost.Bind(typeof(PcMonitors), nameof(SetActiveSpritePostfix)));
        }
        private static void Receive(ReadOnlySpan<byte> data) {
            if (!DisplayWire.Read(data, out DisplayState state, out _) || !(Entity.FindEntityByID(state.Item) is Item item) || item.Removed) { return; }
            if (Chess.ChessWorld.IsBoard(item)) { ChessTables.Receive(item, data); return; }
            if (item.HasTag(PrinterWorld.Tag)) { PrinterDraw.Receive(item, data); return; }
            if (!item.HasTag(TerminalTag)) { return; }
            Find(item).View.Apply(data);
        }
        private static Entry Find(Item item) {
            if (known.TryGetValue(item, out Entry entry)) { return entry; }
            entry = new Entry { Item = item, Profile = Get(item.Prefab) };
            known.Add(item, entry); entries.Add(entry); return entry;
        }
        private static Profile Get(ItemPrefab prefab) {
            Sprite original = prefab.Sprite;
            string definition = prefab.ConfigElement.GetAttributeString("scpitdscreen", "");
            Texture2D texture = original?.Texture;
            if (texture == null || texture.IsDisposed) { return null; }
            Rectangle source = original.SourceRect;
            if (profiles.TryGetValue(prefab, out Profile cached) && ReferenceEquals(cached.Original, original) &&
                ReferenceEquals(cached.Texture, texture) && cached.Source == source && string.Equals(cached.Definition, definition, StringComparison.Ordinal)) { return cached; }
            var profile = new Profile { Original = original, Texture = texture, Source = source, Definition = definition };
            profile.Valid = MonitorGlass.TryParse(definition.AsSpan(), source.Width, source.Height, out MonitorGlass glass);
            if (profile.Valid) { profile.Glass = glass; profile.Bands = MonitorBandPlan.Build(glass, XpLook.ScreenW, XpLook.ScreenH); }
            else { ModLog.Error($"computer {prefab.Identifier}: missing or invalid scpitdscreen rectangle"); }
            profiles[prefab] = profile;
            return profile;
        }
        public static void SetActiveSpritePostfix(Item __instance) { if (known.TryGetValue(__instance, out Entry entry)) { Apply(entry); } }
        private static bool Powered(Entry entry) => SwipeWorld.Powered(entry.Item) && (!entry.View.Ready || entry.View.State.Powered);
        public static void Frame() {
            IReadOnlyList<Item> items = Tagged.Of(terminals);
            for (int i = 0; i < items.Count; i++) { Find(items[i]); }
            GraphicsDevice device = GameMain.Instance.GraphicsDevice;
            Rectangle view = GameMain.GameScreen.Cam.WorldView;
            for (int i = entries.Count - 1; i >= 0; i--) {
                Entry entry = entries[i]; Item item = entry.Item;
                if (item.Removed || !item.HasTag(TerminalTag)) {
                    Restore(entry); entry.Light?.Remove(); entry.Surface?.Remove(); known.Remove(item); entries.RemoveAt(i); continue;
                }
                Profile current = Get(item.Prefab);
                if (!ReferenceEquals(entry.Profile, current)) {
                    Restore(entry); entry.Surface?.Remove(); entry.Surface = null;
                    entry.Profile = current; entry.HasFrame = false;
                }
                if (entry.Profile == null || !entry.Profile.Valid) { continue; }
                bool powered = Powered(entry);
                bool visible = !item.HiddenInGame && item.ParentInventory == null &&
                    (Inside(view, item.WorldPosition, item.Scale * Math.Max(entry.Profile.Original.size.X, entry.Profile.Original.size.Y)) || Rec.ScreenCameras.Observes(item));
                if (entry.Light != null) { entry.Light.Enabled = visible && powered; }
                if (!powered || !visible) {
                    if (!powered || Timing.TotalTime - entry.Seen > 15) { Restore(entry); entry.Surface?.Remove(); entry.Surface = null; }
                    continue;
                }
                entry.Seen = Timing.TotalTime;
                if (entry.Surface == null || entry.Surface.Texture.IsDisposed || entry.Surface.Texture.IsContentLost) {
                    Restore(entry); entry.Surface?.Remove(); entry.Surface = null;
                    Rectangle original = entry.Profile.Original.SourceRect;
                    if ((long)original.Width * original.Height * 4 > RenderSurface.Monitors.Limit - RenderSurface.Monitors.Bytes) { ModLog.Once("Monitor surface budget reached; waiting for offscreen surfaces to expire."); continue; }
                    entry.Surface = new Surface(device, entry.Profile.Original); entry.HasFrame = false; entry.Painted = uint.MaxValue;
                }
                uint revision = entry.View.Ready ? entry.View.State.Revision : 0;
                bool camera = entry.View.Ready && entry.View.State.App == PcApp.Cameras;
                if (camera) { Rec.ScreenCameras.Want(item.ID); }
                uint cameraVersion = camera ? Rec.ScreenCameras.Version(item.ID) : 0;
                if (DisplaySource.Repaint(revision, entry.Painted, entry.View.State.Epoch, entry.PaintedEpoch, cameraVersion, entry.PaintedCamera)) {
                    if (scratch == null || scratch.IsDisposed || scratch.IsContentLost || !ReferenceEquals(scratch.GraphicsDevice, device)) {
                        scratchSurface?.Dispose(); scratchSurface = new RenderSurface(device, XpLook.ScreenW, XpLook.ScreenH, RenderSurface.Desktops);
                    }
                    if (!PcScreen.RenderDisplay(entry.View, scratch)) { continue; }
                    Bake(entry.Profile, entry.Surface, scratch);
                    entry.HasFrame = true; entry.Painted = revision; entry.PaintedEpoch = entry.View.State.Epoch; entry.PaintedCamera = cameraVersion;
                }
                Apply(entry); UpdateLight(entry);
            }
        }
        private static bool Inside(Rectangle view, Vector2 point, float margin) => point.X >= view.Left - margin && point.X <= view.Right + margin && point.Y >= view.Top - view.Height - margin && point.Y <= view.Top + margin;
        private static void Bake(Profile profile, Surface surface, Texture2D desktop) {
            GraphicsDevice device = surface.Texture.GraphicsDevice;
            SpriteBatches.Ensure(ref batch, device);
            device.SetRenderTarget(surface.Texture); device.Clear(Color.Transparent);
            batch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp);
            batch.Draw(profile.Original.Texture, surface.Texture.Bounds, profile.Original.SourceRect, Color.White); batch.End();
            batch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
            for (int i = 0; i < profile.Bands.Length; i++) {
                MonitorBand b = profile.Bands[i];
                batch.Draw(desktop, new Rectangle(b.X, b.Y, b.Width, b.Height),
                    new Rectangle(b.SourceX, b.SourceY, b.SourceWidth, b.SourceHeight), Color.White);
            }
            batch.End();
        }
        private static void Apply(Entry entry) {
            if (entry.Surface == null || entry.Item.Removed || !DisplaySource.CanPublish(Powered(entry), entry.HasFrame, !entry.Surface.Texture.IsDisposed && !entry.Surface.Texture.IsContentLost)) { return; }
            Sprite current = activeSprite(entry.Item);
            if (!ReferenceEquals(current, entry.Surface.Sprite)) {
                entry.PreviousSprite = current;
                activeSprite(entry.Item) = entry.Surface.Sprite;
            }
        }
        private static void Restore(Entry entry) {
            if (!entry.Item.Removed && ReferenceEquals(activeSprite(entry.Item), entry.Surface?.Sprite)) {
                activeSprite(entry.Item) = entry.PreviousSprite ?? entry.Profile?.Original;
            }
            entry.PreviousSprite = null;
        }
        private static void UpdateLight(Entry entry) {
            Item item = entry.Item; Profile profile = entry.Profile; MonitorGlass glass = profile.Glass; Sprite original = profile.Original;
            SpriteEffects effects = original.effects ^ item.SpriteEffects; float rotation = Core.ItemPose.Angle(item);
            if (entry.Rotation != rotation || entry.Scale != item.Scale || entry.Effects != effects) {
                entry.Rotation = rotation; entry.Scale = item.Scale; entry.Effects = effects;
                glass.WorldOffset(original.Origin.X, original.Origin.Y, item.Scale, rotation, (effects & SpriteEffects.FlipHorizontally) != 0, (effects & SpriteEffects.FlipVertically) != 0, out float x, out float y);
                entry.LightOffset = new Vector2(x + original.offset.X, y - original.offset.Y);
            }
            entry.Light ??= new LightSource(item.Position, 1, new Color(125, 165, 210, 110), item.Submarine);
            entry.Light.ParentSub = item.Submarine; entry.Light.Position = item.Position + entry.LightOffset;
            entry.Light.Range = Math.Max(glass.Width, glass.Height) * item.Scale * 2; entry.Light.Enabled = true;
        }
        public static void Hide() {
            for (int i = 0; i < entries.Count; i++) { Restore(entries[i]); entries[i].Surface?.Remove(); entries[i].Surface = null; if (entries[i].Light != null) { entries[i].Light.Enabled = false; } }
        }
        public static void Release() {
            for (int i = 0; i < entries.Count; i++) { Restore(entries[i]); entries[i].Light?.Remove(); entries[i].Surface?.Remove(); }
            entries.Clear(); known.Clear(); profiles.Clear(); scratchSurface?.Dispose(); scratchSurface = null; batch?.Dispose(); batch = null;
        }
    }
}
