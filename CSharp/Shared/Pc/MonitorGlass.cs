using System;
using System.Globalization;

namespace ScpItdMod.Pc {
    public readonly struct MonitorGlass {
        public readonly int X, Y, Width, Height, Radius;

        private MonitorGlass(int x, int y, int width, int height, int radius) {
            X = x;
            Y = y;
            Width = width;
            Height = height;
            Radius = radius;
        }

        public static bool TryParse(ReadOnlySpan<char> text, int spriteWidth, int spriteHeight, out MonitorGlass glass) {
            glass = default;
            Span<int> values = stackalloc int[5];
            for (int i = 0; i < values.Length; i++) {
                int comma = text.IndexOf(',');
                if ((i < values.Length - 1) != (comma >= 0)) { return false; }
                ReadOnlySpan<char> part = comma < 0 ? text : text.Slice(0, comma);
                if (!int.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out values[i])) { return false; }
                if (comma >= 0) { text = text.Slice(comma + 1); }
            }
            int x = values[0], y = values[1], w = values[2], h = values[3], r = values[4];
            if (w <= 0 || h <= 0 || w > spriteWidth || h > spriteHeight ||
                x > spriteWidth - w || y > spriteHeight - h || r > Math.Min(w, h) / 2) { return false; }
            glass = new MonitorGlass(x, y, w, h, r);
            return true;
        }

        public int Inset(int row) {
            if ((uint)row >= (uint)Height) { return Width / 2; }
            float distance = Radius - Math.Min(row + 0.5f, Height - row - 0.5f);
            return distance <= 0 ? 0 : (int)MathF.Ceiling(Radius - MathF.Sqrt((float)Radius * Radius - distance * distance) - 0.5f);
        }

        public void WorldOffset(float originX, float originY, float scale, float rotation, bool flipX, bool flipY, out float x, out float y) {
            float dx = (X + Width * 0.5f - originX) * (flipX ? -scale : scale);
            float dy = (Y + Height * 0.5f - originY) * (flipY ? -scale : scale);
            float cos = MathF.Cos(rotation), sin = MathF.Sin(rotation);
            x = dx * cos - dy * sin;
            y = -dx * sin - dy * cos;
        }
    }
}
