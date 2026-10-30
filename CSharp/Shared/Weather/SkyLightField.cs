using System;
using ScpItdMod.Core;

namespace ScpItdMod.Weather {
    public readonly struct SkyLightRegion {
        public readonly int X, Y, Width, Height;
        public SkyLightRegion(int x, int y, int width, int height) { X = x; Y = y; Width = width; Height = height; }
        public bool Empty => Width == 0 || Height == 0;
    }

    public static class SkyLightField {
        public static SkyLightRegion Visible(WeatherGrid grid, in WeatherRect view) {
            if (grid == null) throw new ArgumentNullException(nameof(grid));
            if (!float.IsFinite(view.Left) || !float.IsFinite(view.Bottom) || !float.IsFinite(view.Right) || !float.IsFinite(view.Top)
                || view.Right < view.Left || view.Top < view.Bottom) throw new ArgumentOutOfRangeException(nameof(view));
            if (view.Left == view.Right || view.Bottom == view.Top || !grid.Bounds.Overlaps(view)) return default;
            int x = Edge(view.Left, grid.Origin.X, grid.Cell, grid.Width, false), y = Edge(view.Bottom, grid.Origin.Y, grid.Cell, grid.Height, false);
            int right = Edge(view.Right, grid.Origin.X, grid.Cell, grid.Width, true), top = Edge(view.Top, grid.Origin.Y, grid.Cell, grid.Height, true);
            return new SkyLightRegion(x, y, right - x, top - y);
        }

        public const int TileCells = 256;
        public static SkyLightRegion Tile(WeatherGrid grid, int tx, int ty) {
            if (grid == null) throw new ArgumentNullException(nameof(grid));
            if ((uint)tx > (uint)((grid.Width - 1) / TileCells) || (uint)ty > (uint)((grid.Height - 1) / TileCells)) throw new ArgumentOutOfRangeException(nameof(tx));
            int x = tx * TileCells, y = ty * TileCells;
            return new SkyLightRegion(x, y, Math.Min(TileCells, grid.Width - x), Math.Min(TileCells, grid.Height - y));
        }

        public static float Sample(WeatherGrid grid, float worldX, float worldY) {
            if (grid == null) throw new ArgumentNullException(nameof(grid));
            return Sample(grid, worldX, worldY, SkyLightEnvironment.Diffuse(new LightColor(1, 1, 1))).R;
        }

        public static LightColor Sample(WeatherGrid grid, float worldX, float worldY, in SkyLightEnvironment light) {
            if (grid == null) throw new ArgumentNullException(nameof(grid));
            var point = new Vec2(worldX, worldY);
            if (!float.IsFinite(worldX) || !float.IsFinite(worldY) || !grid.Bounds.Contains(worldX, worldY) || grid.Geometry.Sample(point, 0).Distance < 0) return default;
            float px = (worldX - grid.Origin.X) / grid.Cell - .5f, py = (worldY - grid.Origin.Y) / grid.Cell - .5f;
            int x = (int)MathF.Floor(px), y = (int)MathF.Floor(py); float u = px - x, v = py - y;
            LightColor result = default; float total = 0;
            for (int row = 0; row < 2; row++) for (int column = 0; column < 2; column++) {
                int cx = Math.Clamp(x + column, 0, grid.Width - 1), cy = Math.Clamp(y + row, 0, grid.Height - 1), at = cy * grid.Width + cx;
                float weight = (column == 0 ? 1 - u : u) * (row == 0 ? 1 - v : v);
                if (weight <= 0 || !grid.Geometry.SegmentClear(point, grid.CellCenter(at))) continue;
                result += light.Evaluate(grid.LightDirections(at)) * weight; total += weight;
            }
            return total > 1e-6f ? result * (1 / total) : light.Evaluate(grid.LightDirectionsAt(worldX, worldY));
        }

        public static void RasterizeDirections(WeatherGrid grid, in SkyLightRegion region, Span<uint> pixels) {
            Validate(grid, region);
            if (region.Empty) return;
            int width = checked(region.Width + 2), height = checked(region.Height + 2);
            if (pixels.Length < checked(width * height)) throw new ArgumentException("Sky direction buffer is too small.", nameof(pixels));
            int left = Math.Max(0, region.X - 1), bottom = Math.Max(0, region.Y - 1);
            grid.PrepareSkyLight(left, bottom, Math.Min(grid.Width, region.X + region.Width + 1) - left, Math.Min(grid.Height, region.Y + region.Height + 1) - bottom);
            int output = 0;
            for (int y = region.Y + region.Height; y >= region.Y - 1; y--) {
                int row = Math.Clamp(y, 0, grid.Height - 1) * grid.Width;
                for (int x = region.X - 1; x <= region.X + region.Width; x++) pixels[output++] = grid.LightDirections(row + Math.Clamp(x, 0, grid.Width - 1));
            }
        }

        public static void RasterizeTile(WeatherGrid grid, in SkyLightRegion region, Span<uint> pixels) {
            Validate(grid, region);
            if (region.Empty) return;
            int width = checked(region.Width + 2), height = checked(region.Height + 2);
            if (pixels.Length < checked(width * height)) throw new ArgumentException("Sky-light tile buffer is too small.", nameof(pixels));
            int left = Math.Max(0, region.X - 1), bottom = Math.Max(0, region.Y - 1);
            grid.PrepareSkyLight(left, bottom, Math.Min(grid.Width, region.X + region.Width + 1) - left, Math.Min(grid.Height, region.Y + region.Height + 1) - bottom);
            int output = 0;
            for (int y = region.Y + region.Height; y >= region.Y - 1; y--) {
                int row = Math.Clamp(y, 0, grid.Height - 1) * grid.Width;
                for (int x = region.X - 1; x <= region.X + region.Width; x++) {
                    int at = row + Math.Clamp(x, 0, grid.Width - 1);
                    uint level = (uint)(grid.LightExposure(at) * 255f);
                    pixels[output++] = 0xff000000u | (grid.Wall(at) ? 0x0000ff00u : 0) | level;
                }
            }
        }

        private static void Validate(WeatherGrid grid, in SkyLightRegion region) {
            if (grid == null) throw new ArgumentNullException(nameof(grid));
            if (region.X < 0 || region.Y < 0 || region.Width < 0 || region.Height < 0
                || region.Width > grid.Width || region.Height > grid.Height
                || region.X > grid.Width - region.Width || region.Y > grid.Height - region.Height) throw new ArgumentOutOfRangeException(nameof(region));
        }

        private static int Edge(float value, float origin, float cell, int limit, bool upper) {
            double at = ((double)value - origin) / cell;
            return (int)Math.Clamp(upper ? Math.Ceiling(at) : Math.Floor(at), 0, limit);
        }

        public static void Rasterize(WeatherGrid grid, in SkyLightRegion region, Span<uint> pixels) {
            Validate(grid, region);
            int count = checked(region.Width * region.Height);
            if (pixels.Length < count) throw new ArgumentException("Sky-light buffer is too small.", nameof(pixels));
            grid.PrepareSkyLight(region.X, region.Y, region.Width, region.Height);
            int output = 0;
            for (int y = region.Y + region.Height - 1; y >= region.Y; y--) {
                int row = y * grid.Width + region.X;
                for (int x = 0; x < region.Width; x++) {
                    uint level = (uint)(Math.Clamp(grid.LightExposure(row + x), 0, 1) * 255f);
                    pixels[output++] = 0xff000000u | level | (level << 8) | (level << 16);
                }
            }
        }
    }
}
