using Barotrauma;
using ScpItdMod.Core;

namespace ScpItdMod.Inv {
    internal static class InventoryTrace {
        private struct Entry {
            internal double Time;
            internal string Event;
            internal ushort Item, Owner;
        }
        private static readonly Entry[] entries = new Entry[128];
        private static int cursor, count;
        private static bool enabled;

        internal static void Attach() {
            ModCommands.Register("scpitd_invtrace", "toggle inventory event tracing", _ => {
                enabled = !enabled; cursor = count = 0; ModLog.Message("inventory trace=" + enabled);
            });
            ModCommands.Register("scpitd_invstate", "inspect open inventory owners, slots and recorded input", _ => SubPanel.Dump());
        }

        internal static void Record(string action, Item item, Inventory inventory) {
            if (!enabled) { return; }
            entries[cursor] = new Entry { Time = Timing.TotalTime, Event = action, Item = item?.ID ?? 0, Owner = inventory?.Owner?.ID ?? 0 };
            cursor = (cursor + 1) % entries.Length;
            if (count < entries.Length) { count++; }
        }

        internal static void Dump() {
            for (int i = 0; i < count; i++) {
                Entry entry = entries[(cursor - count + i + entries.Length) % entries.Length];
                ModLog.Message($"{entry.Time:F3} {entry.Event} item={entry.Item} owner={entry.Owner}");
            }
        }
    }
}
