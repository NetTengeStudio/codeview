using System;

namespace ScpItdMod.Inv {
    public static class EquipmentRow {
        public static int[] Arrange(int screenWidth, int padding, int spacing, int[] widths, bool[] personal) {
            if (widths == null || personal == null) { throw new ArgumentNullException(widths == null ? nameof(widths) : nameof(personal)); }
            var xs = new int[widths.Length];
            var rows = new int[widths.Length];
            ArrangeInto(screenWidth, padding, spacing, widths, personal, xs, rows);
            return xs;
        }

        public static void ArrangeInto(int screenWidth, int padding, int spacing,
            ReadOnlySpan<int> widths, ReadOnlySpan<bool> personal, Span<int> xs, Span<int> rows) {
            if (widths.Length != personal.Length || widths.Length > xs.Length || widths.Length > rows.Length) {
                throw new ArgumentException("Slot inputs and output buffers must cover the same slots");
            }
            if (screenWidth <= 0) { throw new ArgumentOutOfRangeException(nameof(screenWidth)); }
            padding = Math.Clamp(padding, 0, (screenWidth - 1) / 2);
            spacing = Math.Max(0, spacing);
            int available = screenWidth - padding * 2;
            long ordinaryWidth = 0;
            int ordinaryCount = 0, row = 0, right = screenWidth - padding;
            bool occupied = false;
            for (int i = 0; i < widths.Length; i++) {
                xs[i] = 0; rows[i] = -1;
                int width = widths[i];
                if (width <= 0) { continue; }
                if (width > available) { throw new ArgumentOutOfRangeException(nameof(widths), "A slot is wider than the available viewport"); }
                if (!personal[i]) {
                    ordinaryWidth += width + (ordinaryCount++ > 0 ? (long)spacing : 0);
                    continue;
                }
                if (occupied && (long)right - spacing - width < padding) {
                    row++; right = screenWidth - padding; occupied = false;
                }
                if (occupied) { right -= spacing; }
                xs[i] = right - width; rows[i] = row;
                right = xs[i]; occupied = true;
            }
            if (ordinaryCount == 0) { return; }
            int end = occupied ? (int)Math.Max(padding, (long)right - spacing) : screenWidth - padding;
            if (ordinaryWidth > end - padding && occupied) { row++; end = screenWidth - padding; }
            int start = 0;
            while (start < widths.Length) {
                int last = start, width = 0, count = 0;
                for (; last < widths.Length; last++) {
                    if (personal[last] || widths[last] <= 0) { continue; }
                    long next = (long)width + (count > 0 ? spacing : 0) + widths[last];
                    if (next > end - padding) { break; }
                    width = (int)next; count++;
                }
                int x = Math.Min((screenWidth - width) / 2, end - width);
                for (int i = start; i < last; i++) {
                    if (personal[i] || widths[i] <= 0) { continue; }
                    xs[i] = x; rows[i] = row;
                    x += widths[i] + spacing;
                }
                start = last;
                if (start < widths.Length) { row++; end = screenWidth - padding; }
            }
        }
    }
}
