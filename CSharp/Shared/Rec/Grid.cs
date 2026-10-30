using System;
using System.Collections.Generic;

namespace ScpItdMod.Rec {
    public sealed class Grid<T> where T : class {
        private readonly float cell;
        private readonly Dictionary<long, List<T>> cells = new Dictionary<long, List<T>>();
        private readonly Stack<List<T>> pool = new Stack<List<T>>();
        private readonly List<long> sparseKeys = new List<long>(32);

        public Grid(float cellSize) {
            if (!float.IsFinite(cellSize) || cellSize <= 0) { throw new ArgumentOutOfRangeException(nameof(cellSize)); }
            cell = cellSize;
        }

        public int Count { get; private set; }

        public void Clear() {
            foreach (List<T> list in cells.Values) {
                list.Clear();
                pool.Push(list);
            }
            cells.Clear();
            sparseKeys.Clear();
            Count = 0;
        }

        public void Add(T item, float x, float y) {
            if (item == null) { throw new ArgumentNullException(nameof(item)); }
            if (!Cell(x, out int cx) || !Cell(y, out int cy)) { throw new ArgumentOutOfRangeException(nameof(x)); }
            long key = Key(cx, cy);
            if (!cells.TryGetValue(key, out List<T> list)) {
                list = pool.Count > 0 ? pool.Pop() : new List<T>(8);
                cells[key] = list;
            }
            list.Add(item);
            Count++;
        }

        public void Query(float minX, float minY, float maxX, float maxY, List<T> into) {
            if (into == null) { throw new ArgumentNullException(nameof(into)); }
            into.Clear();
            if (cells.Count == 0 || minX > maxX || minY > maxY ||
                !Cell(minX, out int x0) || !Cell(maxX, out int x1) ||
                !Cell(minY, out int y0) || !Cell(maxY, out int y1)) { return; }

            long width = (long)x1 - x0 + 1;
            long height = (long)y1 - y0 + 1;
            long threshold = Math.Max(32L, (long)cells.Count * 4L);
            if (width > threshold || height > threshold || width * height > threshold) {
                sparseKeys.Clear();
                foreach (long key in cells.Keys) {
                    int x = (int)(key >> 32), y = (int)key;
                    if (x >= x0 && x <= x1 && y >= y0 && y <= y1) { sparseKeys.Add(key); }
                }
                // Preserve the dense path's x-major/y-minor ordering, including negative cells.
                sparseKeys.Sort(static (a, b) => {
                    int cmp = ((int)(a >> 32)).CompareTo((int)(b >> 32));
                    return cmp != 0 ? cmp : ((int)a).CompareTo((int)b);
                });
                for (int i = 0; i < sparseKeys.Count; i++) { into.AddRange(cells[sparseKeys[i]]); }
                return;
            }

            for (long x = x0; x <= x1; x++) {
                for (long y = y0; y <= y1; y++) {
                    if (cells.TryGetValue(Key((int)x, (int)y), out List<T> list)) { into.AddRange(list); }
                }
            }
        }

        private bool Cell(float value, out int result) {
            result = 0;
            if (!float.IsFinite(value)) { return false; }
            double coordinate = Math.Floor((double)value / cell);
            if (coordinate < int.MinValue || coordinate > int.MaxValue) { return false; }
            result = (int)coordinate;
            return true;
        }

        private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;
    }
}
