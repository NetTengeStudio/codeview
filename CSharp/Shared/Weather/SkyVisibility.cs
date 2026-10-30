using System;
using ScpItdMod.Core;

namespace ScpItdMod.Weather {
    public sealed class SkyVisibility {
        public const int Directions = 32;
        private static readonly Vec2[] directions = MakeDirections();
        private readonly WeatherGrid grid;
        private readonly float reach;
        private uint[] samples, ready;
        private uint revision;
        public SkyVisibility(WeatherGrid grid) {
            this.grid = grid ?? throw new ArgumentNullException(nameof(grid));
            reach = MathF.Sqrt((grid.Width * grid.Cell) * (grid.Width * grid.Cell) + (grid.Height * grid.Cell) * (grid.Height * grid.Cell)) * 2;
        }
        private static Vec2[] MakeDirections() {
            var result = new Vec2[Directions];
            for (int i = 0; i < result.Length; i++) { float angle = (i + .5f) * (MathF.PI / Directions); result[i] = new Vec2(MathF.Cos(angle), MathF.Sin(angle)); }
            return result;
        }
        public uint At(Vec2 point) {
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !grid.Bounds.Contains(point.X, point.Y)) return 0;
            GeometrySample surface = grid.LightGeometry.Sample(point, .025f);
            if (surface.Distance < 0) return 0;
            if (surface.Distance <= 1e-6f) point += surface.Normal * 1e-4f;
            uint visible = 0;
            for (int i = 0; i < Directions; i++) if (grid.LightGeometry.SegmentClear(point, point + directions[i] * reach)) visible |= 1u << (Directions - i - 1);
            return visible;
        }
        public float Sample(int at) => System.Numerics.BitOperations.PopCount(Mask(at)) * (1f / Directions);
        public uint Mask(int at) {
            if ((uint)at >= (uint)grid.Cells) return 0;
            if (samples == null) { samples = new uint[grid.Cells]; ready = new uint[(grid.Cells + 31) / 32]; revision = grid.LightGeometry.Revision; }
            if (revision != grid.LightGeometry.Revision) { Array.Clear(ready); revision = grid.LightGeometry.Revision; }
            uint bit = 1u << (at & 31);
            if ((ready[at >> 5] & bit) == 0) { samples[at] = At(grid.CellCenter(at)); ready[at >> 5] |= bit; }
            return samples[at];
        }
        public void Prepare(int x0, int y0, int width, int height) {
            if (x0 < 0 || y0 < 0 || width < 0 || height < 0 || width > grid.Width || height > grid.Height || x0 > grid.Width - width || y0 > grid.Height - height) throw new ArgumentOutOfRangeException(nameof(width));
            for (int y = y0; y < y0 + height; y++) for (int x = x0; x < x0 + width; x++) Mask(y * grid.Width + x);
        }
    }
}
