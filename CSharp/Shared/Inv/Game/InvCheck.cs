using System.Collections.Generic;
using System.Text;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using HarmonyLib;
using ScpItdMod.Core;
using ScpItdMod.Net;

namespace ScpItdMod.Inv {
    public sealed class InvCheck : IModule {
        public const string Channel = "scpitd.invcheck";
        private const string Command = "scpitd_invcheck";

        private static readonly List<ulong> mine = new List<ulong>();
        private static readonly InventoryTree inventoryTree = new InventoryTree();
        private static readonly List<InventoryEntry> walked = new List<InventoryEntry>(64);
        private static readonly HashSet<Item> visited = new HashSet<Item>(ReferenceEqualityComparer.Instance);

        public static void Collect(Inventory inventory, ushort holder, int container, List<ulong> entries, int depth = 0) {
            Collect(inventory, holder, container, entries, depth, visited);
        }

        private static void Collect(Inventory inventory, ushort holder, int container, List<ulong> entries, int depth, HashSet<Item> sharedVisited) {
            if (inventory == null) { return; }
            inventoryTree.Collect(inventory, walked, holder, container, depth, sharedVisitedItems: sharedVisited);
            Append(walked, entries);
        }

        private static void Append(List<InventoryEntry> source, List<ulong> destination) {
            for (int i = 0; i < source.Count; i++) {
                InventoryEntry entry = source[i];
                destination.Add(InvSnapshot.Entry(entry.Holder, entry.Container, entry.Slot, entry.Item.ID));
            }
        }

        public static void Snapshot(Character character, List<ulong> entries) {
            entries.Clear();
            visited.Clear();
            if (character?.Inventory != null) { Collect(character.Inventory, character.ID, 0, entries); }
            entries.Sort();
        }

#if SERVER
        private sealed class Peer { public Gate Gate; }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Client, Peer> gates = new System.Runtime.CompilerServices.ConditionalWeakTable<Client, Peer>();
        private static readonly List<ulong> theirs = new List<ulong>();
        private static readonly List<ulong> ghosts = new List<ulong>();
        private static readonly List<ulong> missing = new List<ulong>();
        private static readonly WireWriter reply = new WireWriter(512);

        public void Attach(Harmony harmony) {
            ModNet.ReceiveBytes(Channel, 8 + InvSnapshot.MaxEntries * 9, OnRequest);
            ModCommands.Register(Command, "checks every inventory for items held in two places", _ => ModLog.Message(Scan()));
        }

        public void RoundStart() => gates.Clear();

        public void Release() => gates.Clear();

        private static void OnRequest(Client sender, uint nonce, uint seq, byte[] payload) {
            Peer peer = gates.GetValue(sender, static _ => new Peer());
            if (!peer.Gate.Take(Timing.TotalTime, 1.0f, 3.0f)) { return; }
            var reader = new WireReader(payload);
            if (!InvSnapshot.Read(ref reader, theirs) || !reader.End) { return; }
            Snapshot(sender.Character, mine);
            InvSnapshot.Diff(theirs, mine, ghosts, missing);
            var text = new StringBuilder();
            if (sender.Character == null) {
                text.Append("no character on the server");
            } else if (ghosts.Count == 0 && missing.Count == 0) {
                text.Append($"in sync: {mine.Count} entries");
            } else {
                text.Append($"OUT OF SYNC: {ghosts.Count} only on client, {missing.Count} only on server");
                Describe(text, "client", ghosts, false);
                Describe(text, "server", missing, true);
            }
            if (Access.Gm(sender)) { text.Append(" | ").Append(Scan()); }
            reply.Clear();
            reply.Text(text.ToString());
            ModNet.SendBytes(Channel, sender.Connection, reply.Span, DeliveryMethod.Reliable);
        }

        private static void Describe(StringBuilder text, string side, List<ulong> entries, bool verified) {
            for (int i = 0; i < entries.Count && i < 8; i++) {
                Entity item = verified ? Entity.FindEntityByID(InvSnapshot.Item(entries[i])) : null;
                text.Append($"\n  {side}: slot {InvSnapshot.Slot(entries[i])} in #{InvSnapshot.Holder(entries[i])}: #{InvSnapshot.Item(entries[i])} {(item as Item)?.Prefab.Identifier}");
            }
        }

        public static string Scan() {
            var all = new List<ulong>();
            var duplicated = new List<ushort>();
            int wrongParent = 0;
            int removed = 0;
            visited.Clear();
            foreach (Character character in Character.CharacterList) {
                if (character.Inventory == null) { continue; }
                CollectAndCheck(character.Inventory, character.ID, 0, all, ref wrongParent, ref removed);
            }
            foreach (Item item in Item.ItemList) {
                if (item.ParentInventory != null || !visited.Add(item)) { continue; }
                inventoryTree.CollectChildren(item, walked, sharedVisitedItems: visited);
                CheckAndAppend(walked, all, ref wrongParent, ref removed);
            }
            InvSnapshot.Duplicates(all, duplicated);
            return duplicated.Count == 0 && wrongParent == 0 && removed == 0
                ? $"server inventories ok ({all.Count} entries)"
                : $"SERVER INVARIANTS BROKEN: {duplicated.Count} items in two places, {wrongParent} wrong parents, {removed} removed items still held";
        }

        private static void CollectAndCheck(Inventory inventory, ushort holder, int container, List<ulong> all, ref int wrongParent, ref int removed) {
            inventoryTree.Collect(inventory, walked, holder, container, sharedVisitedItems: visited);
            CheckAndAppend(walked, all, ref wrongParent, ref removed);
        }

        private static void CheckAndAppend(List<InventoryEntry> source, List<ulong> all, ref int wrongParent, ref int removed) {
            for (int i = 0; i < source.Count; i++) {
                InventoryEntry entry = source[i];
                if (entry.Item.Removed) { removed++; }
                if (!ReferenceEquals(entry.Item.ParentInventory, entry.Inventory)) { wrongParent++; }
                all.Add(InvSnapshot.Entry(entry.Holder, entry.Container, entry.Slot, entry.Item.ID));
            }
        }
#else
        private static readonly WireWriter request = new WireWriter(1024);

        public void Attach(Harmony harmony) {
            ModNet.ReceiveBytes(Channel, 64 * 1024, OnReply);
            ModCommands.Register(Command, "compares this client's inventory with the server", _ => Send());
        }

        public void RoundStart() {}

        public void Release() {}

        private static void Send() {
            if (Character.Controlled == null) {
                ModLog.Message("no controlled character");
                return;
            }
            Snapshot(Character.Controlled, mine);
            request.Clear();
            InvSnapshot.Write(request, mine);
            ModNet.SendBytes(Channel, request.Span, DeliveryMethod.Reliable);
        }

        private static void OnReply(byte[] payload) {
            var reader = new WireReader(payload);
            string text = reader.Text(60 * 1024);
            if (!reader.Failed) { ModLog.Message("invcheck: " + text); }
        }
#endif
    }
}
