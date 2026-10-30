using Barotrauma;
using Microsoft.Xna.Framework;

namespace ScpItdMod.Core {
    public static class ItemPose {
        public static float Angle(Item item) => (item.body == null ? item.RotationRad : -item.body.Rotation) + item.Prefab.Sprite.rotation;
#if CLIENT
        public static float DrawAngle(Item item) => (item.body == null ? item.RotationRad : -item.body.DrawRotation) + item.Prefab.Sprite.rotation;
        public static Vector2 DrawPosition(Item item) {
            if (item.body == null) { return item.DrawPosition; }
            item.body.UpdateDrawPosition(); return item.body.DrawPosition;
        }
#endif
    }
}
