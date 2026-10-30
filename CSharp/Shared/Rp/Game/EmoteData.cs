using System;
using System.IO;
using ScpItdMod.Core;

namespace ScpItdMod.Rp {
    public static class EmoteData {
        public const string CatalogAsset = "Content/Data/emotes.xml";
        public const string ManifestAsset = "Content/Sound/Emotes/manifest.json";

        public static EmoteCatalog Load() {
            string xml = Read(CatalogAsset);
            string manifest = Read(ManifestAsset);
            EmoteCatalog catalog = EmoteCatalog.Parse(xml, manifest);
            if (catalog.Count == 0 || catalog.Problems.Count != 0) {
                throw new InvalidDataException("Invalid emote catalog: " + string.Join("; ", catalog.Problems));
            }
            return catalog;
        }

        private static string Read(string asset) {
            string path = ModAssets.Resolve(asset);
            if (path.Length == 0 || !File.Exists(path)) {
                throw new FileNotFoundException($"Emote asset is missing: {asset}", path);
            }
            return File.ReadAllText(path);
        }
    }
}
