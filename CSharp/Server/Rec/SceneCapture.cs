using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace ScpItdMod.Rec {
    // Runs server-side only. Every captured description is detached from gameplay entities.
    public static class SceneCapture {
        public const string ObjectLimitReason = "scene object limit";
        private sealed class Serial { public uint Value; public SceneShadow Shadow; }
        private static readonly ConditionalWeakTable<MapEntity, Serial> serials = new ConditionalWeakTable<MapEntity, Serial>();
        private static uint nextGeneration = 1;
        private const float Cell = 256.0f;
        private static readonly Grid<MapEntity> spatial = new Grid<MapEntity>(Cell);
        private static readonly List<MapEntity> large = new List<MapEntity>(64);
        private static readonly List<MapEntity> candidates = new List<MapEntity>(256);
        private static readonly HashSet<MapEntity> seen = new HashSet<MapEntity>();
        private static double indexedAt = double.NaN;
        public readonly struct CaptureFailure : IEquatable<CaptureFailure> {
            public readonly string Reason, Prefab;
            public readonly ushort Entity;
            public readonly int Width, Height;
            public readonly float ScaleX, ScaleY;
            public CaptureFailure(string reason, MapEntity entity) {
                Reason = reason; Entity = entity?.ID ?? 0;
                Rectangle rect = entity?.WorldRect ?? default; Width = rect.Width; Height = rect.Height;
                Prefab = entity is Item item ? item.Prefab?.Identifier.Value : entity is Structure wall ? wall.Prefab?.Identifier.Value : "";
                ScaleX = entity is Structure structure ? structure.Scale * structure.TextureScale.X : entity?.Scale ?? 0;
                ScaleY = entity is Structure other ? other.Scale * other.TextureScale.Y : entity?.Scale ?? 0;
            }
            public bool Equals(CaptureFailure other) => Reason == other.Reason && Entity == other.Entity && Prefab == other.Prefab && Width == other.Width && Height == other.Height && ScaleX.Equals(other.ScaleX) && ScaleY.Equals(other.ScaleY);
            public override string ToString() => Reason == null ? "none" : $"{Reason} entity=#{Entity} prefab={Prefab} rect={Width}x{Height} scale={ScaleX:R},{ScaleY:R}";
        }
        public static CaptureFailure Failure { get; private set; }
        private static bool Fail(List<SceneObject> output, string reason, MapEntity entity = null) {
            Failure = new CaptureFailure(reason, entity); output.Clear(); return false;
        }
        public static uint IndexBuilds { get; private set; }

        public static void Reset() {
            serials.Clear(); spatial.Clear(); large.Clear(); candidates.Clear(); seen.Clear();
            indexedAt = double.NaN;
            IndexBuilds = 0; Failure = default;
        }

        private static void Rebuild(double tick) {
            if (double.IsFinite(tick) && tick == indexedAt) { return; }
            spatial.Clear(); large.Clear();
            IndexBuilds++;
            foreach (MapEntity entity in MapEntity.MapEntityList) {
                if (entity == null || entity.Removed || entity.IsHidden ||
                    !(entity is Item) && !(entity is Structure)) { continue; }
                Rectangle r = entity.WorldRect;
                if (r.Width <= 0 || r.Height <= 0) { continue; }
                long x0 = (long)MathF.Floor(r.Left / Cell), x1 = (long)MathF.Floor((r.Right - 1f) / Cell);
                long y0 = (long)MathF.Floor((r.Y - r.Height) / Cell), y1 = (long)MathF.Floor((r.Y - 1f) / Cell);
                long width = x1 - x0 + 1, height = y1 - y0 + 1;
                if (width <= 0 || height <= 0 || width > 8 || height > 8 || width * height > 16) {
                    large.Add(entity); continue;
                }
                for (long x = x0; x <= x1; x++) {
                    for (long y = y0; y <= y1; y++) { spatial.Add(entity, (float)(x * Cell + 1), (float)(y * Cell + 1)); }
                }
            }
            indexedAt = tick;
        }

        private static Serial Identity(MapEntity entity) => serials.GetValue(entity, _ => {
            if (++nextGeneration == 0) { ++nextGeneration; }
            return new Serial { Value = nextGeneration };
        });

        public static bool Collect(float x, float y, float halfW, float halfH, List<SceneObject> output) =>
            Collect(double.NaN, x, y, halfW, halfH, output);

        public static bool Collect(double tick, float x, float y, float halfW, float halfH, List<SceneObject> output) {
            output.Clear(); Failure = default;
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(halfW) || !float.IsFinite(halfH) ||
                halfW <= 0 || halfH <= 0) { return Fail(output, "invalid camera bounds"); }
            Rebuild(tick);
            spatial.Query(x - halfW, y - halfH, x + halfW, y + halfH, candidates);
            candidates.AddRange(large);
            seen.Clear();
            foreach (MapEntity entity in candidates) {
                if (!seen.Add(entity)) { continue; }
                if (entity == null || entity.Removed || !(entity is Item) && !(entity is Structure)) { continue; }
                Rectangle r = entity.WorldRect;
                if (r.Right < x - halfW || r.Left > x + halfW || r.Y < y - halfH || r.Y - r.Height > y + halfH) { continue; }
                if (entity is Item item && (item.IsHidden || item.HiddenInGame || item.ParentInventory != null)) { continue; }
                if (entity.IsHidden || entity.ID == 0 || r.Width <= 0 || r.Height <= 0) { continue; }
                if (r.Width > ushort.MaxValue || r.Height > ushort.MaxValue) { return Fail(output, "geometry exceeds wire range", entity); }
                string prefab;
                float worldX, worldY, rotation, sx, sy;
                uint color;
                SceneFlags flags = SceneFlags.None;
                if (entity is Item obj) {
                    if (obj.Prefab == null) { return Fail(output, "missing item prefab", entity); }
                    prefab = obj.Prefab.Identifier.Value;
                    worldX = obj.WorldPosition.X; worldY = obj.WorldPosition.Y;
                    rotation = MathHelper.ToRadians(obj.Rotation);
                    sx = sy = obj.Scale;
                    color = obj.SpriteColor.PackedValue;
                    flags |= SceneFlags.Item;
                    flags |= SceneSpriteFlip.Resolve(obj.FlippedX, obj.FlippedY,
                        obj.Prefab.CanSpriteFlipX, obj.Prefab.CanSpriteFlipY);
                } else {
                    var wall = (Structure)entity;
                    if (wall.Prefab == null) { return Fail(output, "missing structure prefab", entity); }
                    prefab = wall.Prefab.Identifier.Value;
                    worldX = r.X + r.Width * 0.5f; worldY = r.Y - r.Height * 0.5f;
                    rotation = MathHelper.ToRadians(wall.Rotation);
                    sx = wall.TextureScale.X * wall.Scale; sy = wall.TextureScale.Y * wall.Scale;
                    color = wall.SpriteColor.PackedValue;
                    flags |= SceneSpriteFlip.Resolve(wall.FlippedX, wall.FlippedY,
                        wall.Prefab.CanSpriteFlipX, wall.Prefab.CanSpriteFlipY);
                }
                if (entity.DrawBelowWater) { flags |= SceneFlags.Below; }
                if (entity.DrawOverWater) { flags |= SceneFlags.Above; }
                if (string.IsNullOrEmpty(prefab) || prefab.Length > 80 ||
                    !float.IsFinite(worldX) || !float.IsFinite(worldY) ||
                    !float.IsFinite(rotation) || !float.IsFinite(sx) || !float.IsFinite(sy)) { return Fail(output, "invalid prefab or transform", entity); }
                if (output.Count == SceneTape.MaxObjects) {
                    return Fail(output, ObjectLimitReason, entity);
                }
                Serial identity = Identity(entity);
                identity.Shadow = ShadowCapture.Collect(entity, identity.Shadow);
                SceneObject snapshot = new SceneObject(entity.ID, identity.Value, prefab, flags, worldX, worldY,
                    rotation, sx, sy, (ushort)r.Width, (ushort)r.Height, color, identity.Shadow);
                if (!snapshot.Valid) { return Fail(output, "snapshot loses representable geometry", entity); }
                output.Add(snapshot);
            }
            output.Sort(static (a, b) => a.Key.CompareTo(b.Key));
            return true;
        }
    }
}