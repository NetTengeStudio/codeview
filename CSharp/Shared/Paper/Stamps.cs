using System;
using System.Collections.Generic;
using System.Xml.Linq;

namespace ScpItdMod.Paper {
    public sealed class Stamps {
        public const string Asset = "Content/Data/stamps.xml";

        private readonly Dictionary<string, string> kindOfItem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> artOfKind = new Dictionary<string, string>(StringComparer.Ordinal);

        public int Count => artOfKind.Count;

        public static Stamps Current { get; set; } = new Stamps();

        public bool Add(string item, string kind, string art) {
            if (string.IsNullOrWhiteSpace(item) || !Marks.ValidKind(kind) || string.IsNullOrWhiteSpace(art)) { return false; }
            if (kindOfItem.ContainsKey(item) || artOfKind.ContainsKey(kind)) { return false; }
            kindOfItem.Add(item, kind);
            artOfKind.Add(kind, art);
            return true;
        }

        public string KindOf(string item) => item != null && kindOfItem.TryGetValue(item, out string kind) ? kind : null;

        public string ArtOf(string kind) => kind != null && artOfKind.TryGetValue(kind, out string art) ? art : null;

        public IEnumerable<KeyValuePair<string, string>> Items => kindOfItem;

        public static Stamps Parse(XElement root, Action<string> error) {
            var table = new Stamps();
            if (root == null) { return table; }
            foreach (XElement stamp in root.Elements("Stamp")) {
                string item = stamp.Attribute("item")?.Value;
                string kind = stamp.Attribute("kind")?.Value;
                string art = stamp.Attribute("mark")?.Value;
                if (!table.Add(item, kind, art)) { error?.Invoke($"stamp {item ?? "?"}/{kind ?? "?"} is invalid or repeated"); }
            }
            return table;
        }
    }
}
