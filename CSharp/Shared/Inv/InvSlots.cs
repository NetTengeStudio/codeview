using System;
using System.Collections.Generic;
using System.Text;

namespace ScpItdMod.Inv {
    public static class InvSlots {
        public const int Capacity = 6;
        public const string Any = "Any";

        public static string Trim(string slots, int limit) {
            if (string.IsNullOrEmpty(slots)) { return null; }
            string[] parts = Split(slots);
            int any = 0;
            for (int i = 0; i < parts.Length; i++) {
                if (Same(parts[i], Any)) { any++; }
            }
            if (any <= limit) { return null; }
            var kept = new StringBuilder(slots.Length);
            int taken = 0;
            for (int i = 0; i < parts.Length; i++) {
                if (Same(parts[i], Any) && taken++ >= limit) { continue; }
                if (kept.Length > 0) { kept.Append(','); }
                kept.Append(parts[i]);
            }
            return kept.ToString();
        }

        public static string[] Split(string slots) {
            if (string.IsNullOrEmpty(slots)) { return Array.Empty<string>(); }
            string[] parts = slots.Split(',');
            for (int i = 0; i < parts.Length; i++) { parts[i] = parts[i].Trim(); }
            return parts;
        }

        public static int MapIndex(string[] from, string[] to, int index) {
            if ((uint)index >= (uint)from.Length) { return -1; }
            string type = from[index];
            int ordinal = 0;
            for (int i = 0; i < index; i++) {
                if (Same(from[i], type)) { ordinal++; }
            }
            for (int j = 0; j < to.Length; j++) {
                if (!Same(to[j], type)) { continue; }
                if (ordinal == 0) { return j; }
                ordinal--;
            }
            return -1;
        }

        public static int[][] Migrate(string[] from, string[] to, int[][] items, out int moved, out int overflow) {
            moved = 0;
            overflow = 0;
            var result = new int[items.Length][];
            var used = new HashSet<int>();
            var mapped = new List<int>(4);
            for (int n = 0; n < items.Length; n++) {
                mapped.Clear();
                int[] indices = items[n] ?? Array.Empty<int>();
                for (int i = 0; i < indices.Length; i++) {
                    int target = MapIndex(from, to, indices[i]);
                    if (target >= 0 && !mapped.Contains(target)) { mapped.Add(target); }
                }
                if (mapped.Count == 0) { continue; }
                result[n] = mapped.ToArray();
                for (int i = 0; i < mapped.Count; i++) { used.Add(mapped[i]); }
                if (!SameIndices(indices, result[n])) { moved++; }
            }
            int nextFree = 0;
            for (int n = 0; n < items.Length; n++) {
                if (result[n] != null) { continue; }
                while (nextFree < to.Length && (used.Contains(nextFree) || !Same(to[nextFree], Any))) { nextFree++; }
                if (nextFree < to.Length) {
                    result[n] = new[] { nextFree };
                    used.Add(nextFree);
                    moved++;
                } else {
                    result[n] = new[] { to.Length };
                    overflow++;
                }
            }
            return result;
        }

        private static bool SameIndices(int[] a, int[] b) {
            if (a.Length != b.Length) { return false; }
            for (int i = 0; i < a.Length; i++) {
                if (a[i] != b[i]) { return false; }
            }
            return true;
        }

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
