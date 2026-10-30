using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using ScpItdMod.Core;
using ScpItdMod.Inv;

namespace ScpItdMod.Auth {
    public static class AuthWorld {
        public const string RadioAsset = "Content/Data/radio.xml";
        public const string ReaderTag = "scpitd_reader";
        public const string ReaderPrefix = "access_";
        private const double CacheSeconds = 1.0;

        private sealed class Cached {
            public readonly Credentials Credentials = new Credentials();
            public double Until;
        }

        private sealed class Reader {
            public readonly HashSet<Identifier> Tags = new HashSet<Identifier>();
            public AccessPolicy Policy;
        }

        private static readonly Dictionary<Character, Cached> cache = new Dictionary<Character, Cached>();
        private static readonly ConditionalWeakTable<Item, Cached> deviceCache = new ConditionalWeakTable<Item, Cached>();
        private static readonly ConditionalWeakTable<Item, Reader> readers = new ConditionalWeakTable<Item, Reader>();
        private static readonly List<Character> stale = new List<Character>();
        private static readonly InventoryTree inventoryTree = new InventoryTree();
        private static readonly List<InventoryEntry> inventoryEntries = new List<InventoryEntry>(16);

        public static ChannelPlan Plan { get; private set; } = new ChannelPlan();

        public static void Load() {
            Plan = new ChannelPlan();
            string path = ModAssets.Resolve(RadioAsset);
            if (path.Length == 0 || !System.IO.File.Exists(path)) { return; }
            try {
                foreach (XElement band in XDocument.Load(path).Root?.Elements("Channel") ?? Array.Empty<XElement>()) {
                    int from = (int?)band.Attribute("from") ?? -1;
                    int to = (int?)band.Attribute("to") ?? -1;
                    if (!Plan.Add(from, to, band.Attribute("name")?.Value, band.Attribute("access")?.Value)) {
                        ModLog.Error($"{RadioAsset}: band {from}-{to} is invalid or overlaps another");
                    }
                }
            } catch (Exception error) {
                ModLog.Error($"{RadioAsset} cannot be read: {error.Message}");
            }
        }

        public static void Clear() {
            cache.Clear();
            deviceCache.Clear();
            readers.Clear();
        }

        public static Credentials Of(Character character) {
            if (character == null) { return null; }
            double now = Timing.TotalTime;
            if (cache.TryGetValue(character, out Cached cached) && now < cached.Until) { return cached.Credentials; }
            if (cache.Count > 256) { Trim(); }
            cached ??= new Cached();
            cache[character] = cached;
            cached.Until = now + CacheSeconds;
            Credentials c = cached.Credentials;
            c.Clear();
            string job = character.Info?.Job?.Prefab?.Identifier.Value;
            if (!string.IsNullOrEmpty(job)) { c.Jobs.Add(job); }
            AddItem(c, character.Inventory?.GetItemInLimbSlot(InvSlotType.Card));
            foreach (Item held in character.HeldItems) { AddItem(c, held); }
            return c;
        }

        public static Credentials OfDevice(Item item) {
            if (item == null) { return null; }
            double now = Timing.TotalTime;
            Cached cached = deviceCache.GetValue(item, static _ => new Cached());
            if (now >= cached.Until) {
                cached.Credentials.Clear();
                AddItem(cached.Credentials, item);
                cached.Until = now + CacheSeconds;
            }
            return cached.Credentials;
        }

        public static Character Holder(Item item) => item?.ParentInventory?.Owner as Character;

        public static bool RadioAllowed(WifiComponent radio, int channel) {
            if (radio == null || !Plan.Find(channel, out ChannelPlan.Band band)) { return true; }
            Character holder = Holder(radio.Item);
            return band.Policy.Allows(holder != null ? Of(holder) : OfDevice(radio.Item));
        }

        public static AccessPolicy ReaderPolicy(Item reader) {
            Reader cached = readers.GetValue(reader, static _ => new Reader());
            IReadOnlyCollection<Identifier> current = reader.GetTags();
            if (cached.Policy != null && cached.Tags.SetEquals(current)) { return cached.Policy; }
            cached.Tags.Clear(); cached.Tags.UnionWith(current);
            var tags = new List<string>(cached.Tags.Count);
            foreach (Identifier tag in cached.Tags) { tags.Add(tag.Value); }
            cached.Policy = AccessPolicy.FromTags(tags, ReaderPrefix);
            return cached.Policy;
        }

        private static void AddItem(Credentials c, Item item) {
            if (item == null) { return; }
            foreach (Identifier tag in item.GetTags()) { c.AddTag(tag.Value); }
            inventoryTree.CollectChildren(item, inventoryEntries);
            for (int i = 0; i < inventoryEntries.Count; i++) {
                Item inner = inventoryEntries[i].Item;
                if (inner.GetComponent<IdCard>() == null) { continue; }
                foreach (Identifier tag in inner.GetTags()) { c.AddTag(tag.Value); }
            }
        }

        private static void Trim() {
            stale.Clear();
            foreach (KeyValuePair<Character, Cached> pair in cache) {
                if (pair.Key.Removed) { stale.Add(pair.Key); }
            }
            for (int i = 0; i < stale.Count; i++) { cache.Remove(stale[i]); }
            if (cache.Count > 256) { cache.Clear(); }
        }
    }
}
