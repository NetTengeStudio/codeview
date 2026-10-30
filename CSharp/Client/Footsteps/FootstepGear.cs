using System.Collections.Generic;
using Barotrauma;

namespace ScpItdMod.Footsteps {
    public static class FootstepGear {
        private const string LayerAttribute = "nopriorityvanillasound";

        private static readonly Dictionary<ContentXElement, bool> Layering =
            new Dictionary<ContentXElement, bool>();

        public static void Reset() => Layering.Clear();

        public static bool Suppresses(Limb limb) {
            List<WearableSprite> worn = limb.WearingItems;
            for (int i = 0; i < worn.Count; i++) {
                if (!Sounds(limb, worn[i])) { continue; }
                if (!Layers(worn[i])) { return true; }
            }
            return false;
        }

        public static void PlayWorn(Limb limb) {
            List<WearableSprite> worn = limb.WearingItems;
            for (int i = 0; i < worn.Count; i++) {
                if (!Sounds(limb, worn[i])) { continue; }
                SoundPlayer.PlaySound(worn[i].Sound, limb.WorldPosition, hullGuess: limb.character?.CurrentHull);
            }
        }

        private static bool Sounds(Limb limb, WearableSprite wearable) =>
            wearable != null && limb.type == wearable.Limb && !string.IsNullOrWhiteSpace(wearable.Sound);

        private static bool Layers(WearableSprite wearable) {
            ContentXElement element = wearable.SourceElement;
            if (element == null) { return false; }
            if (Layering.TryGetValue(element, out bool layered)) { return layered; }
            layered = element.GetAttributeBool(LayerAttribute, false) ||
                (wearable.WearableComponent?.Item?.Prefab?.ConfigElement?
                    .GetAttributeBool(LayerAttribute, false) ?? false);
            Layering[element] = layered;
            return layered;
        }
    }
}
