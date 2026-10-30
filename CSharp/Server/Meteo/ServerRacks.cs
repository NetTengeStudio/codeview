using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;
using ScpItdMod.Auth;
using ScpItdMod.Core;

namespace ScpItdMod.Meteo {
    public sealed class ServerRack {
        public Item Item;
        public MeteoArchive Archive;
        public int Disks;
        public bool Online => !Item.Removed && Item.Condition > 0 && SwipeWorld.Powered(Item);
    }

    public static class ServerRacks {
        public const float LinkRange = 12000;
        private static readonly Dictionary<ushort, ServerRack> racks = new Dictionary<ushort, ServerRack>();
        private static readonly List<ushort> gone = new List<ushort>();
        private static readonly Identifier RackTag = MeteoWire.RackTag.ToIdentifier(), DiskTag = MeteoWire.DiskTag.ToIdentifier();
        private static int watch = -1;
        private static double next;

        public static IEnumerable<KeyValuePair<ushort, ServerRack>> All => racks;

        public static int Count => racks.Count;

        public static void Attach() { watch = Tagged.Watch(RackTag); }

        public static void Clear() { racks.Clear(); gone.Clear(); next = 0; }

        public static void Update(double wall) {
            if (watch < 0 || wall < next) { return; }
            next = wall + 1;
            IReadOnlyList<Item> list = Tagged.Of(watch);
            for (int i = 0; i < list.Count; i++) {
                Item item = list[i];
                if (!racks.TryGetValue(item.ID, out ServerRack rack) || !ReferenceEquals(rack.Item, item)) {
                    rack = new ServerRack { Item = item, Archive = new MeteoArchive(MeteoArchive.CapacityFor(0)) };
                    racks[item.ID] = rack;
                }
                int disks = Disks(item);
                if (disks != rack.Disks) { rack.Disks = disks; rack.Archive.Resize(MeteoArchive.CapacityFor(disks)); }
            }
            gone.Clear();
            foreach (var pair in racks) { Item item = pair.Value.Item; if (item.Removed || !item.HasTag(RackTag)) { gone.Add(pair.Key); } }
            for (int i = 0; i < gone.Count; i++) { racks.Remove(gone[i]); }
        }

        public static string HostOf(Item item) {
            foreach (Identifier tag in item.GetTags()) { if (tag.Value.StartsWith("scpitd_host_", StringComparison.OrdinalIgnoreCase)) { return tag.Value; } }
            return null;
        }

        private static int Disks(Item rack) {
            ItemContainer container = rack.GetComponent<ItemContainer>();
            if (container == null) { return 0; }
            int n = 0;
            foreach (Item inside in container.Inventory.AllItems) { if (inside.HasTag(DiskTag) && inside.Condition > 0 && ++n >= MeteoArchive.MaxDisks) { break; } }
            return n;
        }

        public static ServerRack Nearest(Item from, bool requirePower = true) {
            string host = HostOf(from);
            ServerRack best = null; float bestDistance = LinkRange * LinkRange;
            foreach (var pair in racks) {
                ServerRack r = pair.Value;
                if (r.Item.Removed || r.Item.Condition <= 0 || requirePower && !SwipeWorld.Powered(r.Item)) { continue; }
                if (host != null && !string.Equals(HostOf(r.Item), host, StringComparison.OrdinalIgnoreCase)) { continue; }
                float d = Vector2.DistanceSquared(from.WorldPosition, r.Item.WorldPosition);
                if (d < bestDistance) { bestDistance = d; best = r; }
            }
            return best;
        }

        public static bool HostOnline(string disk) {
            if (disk == null || !disk.StartsWith("host:", StringComparison.Ordinal)) { return true; }
            string tag = "scpitd_host_" + disk.Substring(5);
            bool any = false;
            foreach (var pair in racks) {
                if (!string.Equals(HostOf(pair.Value.Item), tag, StringComparison.OrdinalIgnoreCase)) { continue; }
                any = true;
                if (pair.Value.Online) { return true; }
            }
            return !any;
        }
    }
}
