using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace ScpItdMod.Core {
    public static class VanillaSlotSprite {
        private const string SlotAsset = "Content/HUD/inventoryslot.png";

        private static readonly Rectangle Source = new Rectangle(0, 0, 119, 120);

        private static Sprite sprite;

        public static void Attach(Harmony harmony) =>
            harmony.Patch(
                AccessTools.PropertyGetter(typeof(Inventory), nameof(Inventory.SlotSpriteSmall)),
                postfix: ModuleHost.Bind(typeof(VanillaSlotSprite), nameof(SlotSpritePostfix)));

        public static void SlotSpritePostfix(ref Sprite __result) => __result = Replacement() ?? __result;

        public static void Release() => sprite = null;

        private static Sprite Replacement() {
            if (sprite != null && sprite.Texture != null) { return sprite; }
            sprite = TextureStore.Art(SlotAsset, Source);
            if (sprite == null) { return null; }
            sprite.size = new Vector2(Source.Width * Inventory.SlotSpriteSmallScale,
                Source.Height * Inventory.SlotSpriteSmallScale);
            return sprite;
        }
    }
}
