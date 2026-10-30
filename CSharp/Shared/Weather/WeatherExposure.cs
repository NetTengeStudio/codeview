using System;

namespace ScpItdMod.Weather {
    internal sealed class WeatherExposure {
        private struct Column { internal int Cell; internal float A, B, C; }
        private Column[] columns = Array.Empty<Column>();
        private readonly float[][] rows = { Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>() };
        private readonly int[] rowIds = new int[3];
        private float[] source = Array.Empty<float>();
        private int sourceOrigin, sourceCount;
        internal void Rasterize(WeatherGrid grid, in WeatherRect bounds, int width, int height, Span<float> output) {
            if (width <= 0 || height <= 0 || output.Length < checked(width * height)) throw new ArgumentOutOfRangeException(nameof(output));
            if (!float.IsFinite(bounds.Right - bounds.Left) || !float.IsFinite(bounds.Top - bounds.Bottom) || bounds.Right <= bounds.Left || bounds.Top <= bounds.Bottom) throw new ArgumentOutOfRangeException(nameof(bounds));
            if (columns.Length < width) {
                int capacity = Math.Max(width, columns.Length * 2);
                columns = new Column[capacity];
                source = new float[checked(capacity * 3)];
                for (int i = 0; i < rows.Length; i++) rows[i] = new float[capacity];
            }
            Array.Fill(rowIds, int.MinValue);
            for (int x = 0; x < width; x++) {
                float wx = WeatherNoise.Mix(bounds.Left, bounds.Right, (x + .5f) / width);
                float u = Math.Clamp((wx - grid.Origin.X) / grid.Cell, -2, grid.Width + 1);
                int ix = (int)MathF.Floor(u);
                Weights(u - ix - .5f, out float a, out float b, out float c);
                columns[x] = new Column { Cell = ix, A = a, B = b, C = c };
            }
            sourceOrigin = Math.Min(columns[0].Cell, columns[width - 1].Cell) - 1;
            sourceCount = Math.Abs(columns[width - 1].Cell - columns[0].Cell) + 3;
            for (int y = 0; y < height; y++) {
                float wy = WeatherNoise.Mix(bounds.Top, bounds.Bottom, (y + .5f) / height);
                float v = Math.Clamp((wy - grid.Origin.Y) / grid.Cell, -2, grid.Height + 1);
                int iy = (int)MathF.Floor(v);
                Weights(v - iy - .5f, out float a, out float b, out float c);
                float[] row0 = Row(grid, iy - 1, width), row1 = Row(grid, iy, width), row2 = Row(grid, iy + 1, width);
                Span<float> destination = output.Slice(y * width, width);
                for (int x = 0; x < width; x++) destination[x] = row0[x] * a + row1[x] * b + row2[x] * c;
            }
        }
        private float[] Row(WeatherGrid grid, int row, int width) {
            int slot = row % 3; if (slot < 0) slot += 3;
            float[] values = rows[slot];
            if (rowIds[slot] == row) return values;
            rowIds[slot] = row;
            if ((uint)row >= (uint)grid.Height) { Array.Clear(values, 0, width); return values; }
            bool contiguous = sourceCount <= width * 3;
            if (contiguous) for (int x = 0; x < sourceCount; x++) source[x] = grid.ShownAir(sourceOrigin + x, row);
            for (int x = 0; x < width; x++) {
                ref readonly Column column = ref columns[x];
                int ix = column.Cell;
                if (contiguous) {
                    int at = ix - sourceOrigin;
                    values[x] = column.A * source[at - 1] + column.B * source[at] + column.C * source[at + 1];
                } else values[x] = column.A * grid.ShownAir(ix - 1, row) + column.B * grid.ShownAir(ix, row) + column.C * grid.ShownAir(ix + 1, row);
            }
            return values;
        }
        private static void Weights(float f, out float a, out float b, out float c) {
            a = .5f * (.5f - f) * (.5f - f); c = .5f * (.5f + f) * (.5f + f); b = 1 - a - c;
        }
    }
}
