using System;

namespace ScpItdMod.Inv {
    // Pure rules for how the container deck moves. The client feeds them item ids and cell indices;
    // Motion handles do the actual easing, so everything here is checkable without the game.
    public static class OrbitFlow {
        public const int NoSource = -1;

        // For every cell of the next frame, the cell index the same item occupied in the previous
        // frame, or NoSource when it is new. An id of 0 marks an empty cell and never matches.
        // Used for FLIP: an icon that changes cell starts at its old position and eases to the new.
        public static void Match(ReadOnlySpan<int> previous, ReadOnlySpan<int> next, Span<int> source) {
            if (source.Length < next.Length) { throw new ArgumentException("source is shorter than next", nameof(source)); }
            for (int i = 0; i < next.Length; i++) {
                source[i] = NoSource;
                int id = next[i];
                if (id == 0) { continue; }
                for (int j = 0; j < previous.Length; j++) {
                    if (previous[j] == id) { source[i] = j; break; }
                }
            }
        }

        // Ripple delay from the anchor column outwards, rows lag the columns a little.
        public static double WaveDelay(int row, int column, int columns, double interval) =>
            interval * (Math.Abs(column - (columns - 1) * 0.5) + row * 1.25);
    }
}
