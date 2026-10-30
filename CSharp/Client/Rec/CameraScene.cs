using System;
using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ScpItdMod.Rec {
    public sealed class CameraScene {
        private readonly Dictionary<(string Prefab, bool Item), Sprite> sprites = new Dictionary<(string, bool), Sprite>();
        public static bool Capturing { get; private set; }

        // Draw only historical, immutable descriptions. This path deliberately never
        // calls Item.Draw, Structure.Draw or mutates any live Barotrauma object.
        public void Draw(SpriteBatch batch, ScreenFrame frame) {
            Capturing = true;
            try {
                if (frame.HistoricalSceneReady) {
                    IReadOnlyList<SceneObject> scene = frame.Scene;
                    for (int i = 0; i < scene.Count; i++) {
                        if ((scene[i].Flags & SceneFlags.Below) != 0) { DrawObject(batch, scene[i]); }
                    }
                }
                for (int i = 0; i < frame.Count; i++) {
                    FigurePose pose = frame.Pose(i);
                    Ghosts.Draw(batch, frame.Look(i), pose, Color.White);
                }
                if (frame.HistoricalSceneReady) {
                    IReadOnlyList<SceneObject> scene = frame.Scene;
                    for (int i = 0; i < scene.Count; i++) {
                        if ((scene[i].Flags & SceneFlags.Above) != 0) { DrawObject(batch, scene[i]); }
                    }
                }
            } finally { Capturing = false; }
        }

        private void DrawObject(SpriteBatch batch, in SceneObject obj) {
            bool item = (obj.Flags & SceneFlags.Item) != 0;
            var key = (obj.Prefab, item);
            if (!sprites.TryGetValue(key, out Sprite sprite)) {
                if (item) {
                    if (!ItemPrefab.Prefabs.TryGet(obj.Prefab, out ItemPrefab prefab)) { return; }
                    sprite = prefab.Sprite;
                } else {
                    if (!StructurePrefab.Prefabs.TryGet(obj.Prefab, out StructurePrefab prefab)) { return; }
                    sprite = prefab.Sprite;
                }
                if (sprite == null) { return; }
                sprites[key] = sprite;
            }
            if (sprite.Texture == null || sprite.Texture.IsDisposed) { return; }
            SpriteEffects fx = SpriteEffects.None;
            if ((obj.Flags & SceneFlags.FlipX) != 0) { fx |= SpriteEffects.FlipHorizontally; }
            if ((obj.Flags & SceneFlags.FlipY) != 0) { fx |= SpriteEffects.FlipVertically; }
            fx ^= sprite.effects;
            // Structure.Draw's GetRotationForSprite negates rotation when the
            // prefab sprite has exactly one base flip (independent of entity flips).
            float rotation = obj.Rotation;
            if (!item) {
                bool prefabX = (sprite.effects & SpriteEffects.FlipHorizontally) != SpriteEffects.None;
                bool prefabY = (sprite.effects & SpriteEffects.FlipVertically) != SpriteEffects.None;
                if (prefabX != prefabY) { rotation = -rotation; }
            }
            Color tint = new Color { PackedValue = obj.Color };
            Vector2 at = new Vector2(obj.X, -obj.Y);
            if (item) {
                sprite.Draw(batch, at, tint, sprite.Origin, rotation,
                    new Vector2(obj.ScaleX, obj.ScaleY), spriteEffect: fx);
            } else {
                sprite.DrawTiled(batch, at, new Vector2(obj.Width, obj.Height), fx,
                    rotation: rotation, origin: new Vector2(obj.Width * 0.5f, obj.Height * 0.5f),
                    color: tint, textureScale: new Vector2(obj.ScaleX, obj.ScaleY));
            }
        }
    }
}