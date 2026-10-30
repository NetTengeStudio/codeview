using System.Collections.Generic;
using Barotrauma;

namespace ScpItdMod.Footsteps {
    public enum FootstepMaterial {
        Generic = 0,
        Metal = 1,
        Snow = 2,
        DeepSnow = 3
    }

    public static class FootstepMaterials {
        private static readonly Dictionary<Identifier, FootstepMaterial> Table =
            new Dictionary<Identifier, FootstepMaterial> {
                { "platform".ToIdentifier(), FootstepMaterial.Metal },
                { "largeplatform".ToIdentifier(), FootstepMaterial.Metal },
                { "stairsleft".ToIdentifier(), FootstepMaterial.Metal },
                { "stairsleft2".ToIdentifier(), FootstepMaterial.Metal },
                { "stairsleft3".ToIdentifier(), FootstepMaterial.Metal },
                { "stairsleftlong".ToIdentifier(), FootstepMaterial.Metal },
                { "stairsleftshort".ToIdentifier(), FootstepMaterial.Metal },
                { "stairsright".ToIdentifier(), FootstepMaterial.Metal },
                { "stairsrightslong".ToIdentifier(), FootstepMaterial.Metal },
                { "stairsrightshort".ToIdentifier(), FootstepMaterial.Metal }
            };

        public const string SnowTag = "scpitd_floor_snow", MetalTag = "scpitd_floor_metal";

        public static FootstepMaterial Of(string tag, Identifier surface, Weather.SnowContact snow, bool interior) {
            switch (Weather.FloorMaterial.Resolve(tag == MetalTag, tag == SnowTag, snow, interior)) {
                case Weather.FloorKind.DeepSnow: return FootstepMaterial.DeepSnow;
                case Weather.FloorKind.Snow: return FootstepMaterial.Snow;
                case Weather.FloorKind.Metal: return FootstepMaterial.Metal;
            }
            return Of(surface);
        }

        public static FootstepMaterial Of(Identifier surface) =>
            Table.TryGetValue(surface, out FootstepMaterial material)
                ? material
                : surface.IsEmpty ? FootstepMaterial.Generic : FootstepMaterial.Metal;
    }
}
