using System;
using System.Collections.Generic;
using ScpItdMod.Net;

namespace ScpItdMod.Inv {
    public static class InvSnapshot {
        public const int MaxEntries = 4096;

        public static ulong Entry(ushort holder, int container, int slot, ushort item) =>
            ((ulong)holder << 48) | ((ulong)(uint)(container & 0xFF) << 40) | ((ulong)(uint)(slot & 0xFFFFFF) << 16) | item;

        public static ushort Holder(ulong entry) => (ushort)(entry >> 48);

        public static int Slot(ulong entry) => (int)((entry >> 16) & 0xFFFFFF);

        public static ushort Item(ulong entry) => (ushort)entry;

        public static void Write(WireWriter writer, List<ulong> entries) {
            entries.Sort();
            writer.Var((ulong)entries.Count);
            for (int i = 0; i < entries.Count; i++) { writer.U64(entries[i]); }
        }

        public static bool Read(ref WireReader reader, List<ulong> entries) {
            entries.Clear();
            int count = reader.Count(MaxEntries);
            for (int i = 0; i < count && !reader.Failed; i++) { entries.Add(reader.U64()); }
            if (reader.Failed) { return false; }
            entries.Sort();
            return true;
        }

        public static void Diff(List<ulong> client, List<ulong> server, List<ulong> ghosts, List<ulong> missing) {
            ghosts.Clear();
            missing.Clear();
            int i = 0;
            int j = 0;
            while (i < client.Count || j < server.Count) {
                if (j >= server.Count || (i < client.Count && client[i] < server[j])) {
                    ghosts.Add(client[i++]);
                } else if (i >= client.Count || server[j] < client[i]) {
                    missing.Add(server[j++]);
                } else {
                    i++;
                    j++;
                }
            }
        }

        public static void Duplicates(List<ulong> entries, List<ushort> duplicated) {
            duplicated.Clear();
            var owner = new Dictionary<ushort, ulong>();
            for (int i = 0; i < entries.Count; i++) {
                ushort item = Item(entries[i]);
                ulong place = entries[i] >> 40;
                if (!owner.TryGetValue(item, out ulong first)) {
                    owner.Add(item, place);
                } else if (first != place && !duplicated.Contains(item)) {
                    duplicated.Add(item);
                }
            }
        }
    }
}
