using System;
using ScpItdMod.Core;

namespace ScpItdMod.Weather {
    public sealed class WeatherAirflow {
        public const int Width = 112, Height = 56, Iterations = 30;
        public const float Cell = .5f, StepSeconds = 1 / 30f, Relaxation = 8f, Confinement = 1.5f, StillExposure = .02f, Recentre = 4 * Cell;
        private readonly float[] u = new float[Width * Height], v = new float[Width * Height], u0 = new float[Width * Height], v0 = new float[Width * Height];
        private readonly float[] pressure = new float[Width * Height], divergence = new float[Width * Height], curl = new float[Width * Height], open = new float[Width * Height];
        private readonly bool[] still = new bool[Width * Height];
        private readonly float[] near = new float[Width * Height];
        public const float FreeRelaxation = .6f, ShelterReach = 6f;
        private float accumulator;
        private Vec2 ambient;
        public Vec2 Origin { get; private set; }
        public bool Placed { get; private set; }
        public Func<Vec2, Vec2> ExternalWind { get; set; }
        // Whether ExternalWind may be called from several threads at once. A source that expires or caches inside the call must leave this false, and its callers then stay on one thread.
        public bool ExternalWindConcurrent { get; set; }
        public bool Concurrent => ExternalWind == null || ExternalWindConcurrent;

        public Vec2 Centre => Origin + new Vec2(Width * Cell * .5f, Height * Cell * .5f);

        public void Place(WeatherGrid grid, Vec2 centre, Vec2 wind) {
            ambient = wind;
            Vec2 target = new Vec2(MathF.Floor(centre.X / Cell - Width * .5f) * Cell, MathF.Floor(centre.Y / Cell - Height * .5f) * Cell);
            if (!Placed) { Origin = target; Placed = true; Mask(grid); for (int i = 0; i < u.Length; i++) { u[i] = still[i] ? 0 : wind.X * open[i]; v[i] = 0; pressure[i] = 0; } return; }
            int dx = (int)MathF.Round((target.X - Origin.X) / Cell), dy = (int)MathF.Round((target.Y - Origin.Y) / Cell);
            if (Math.Abs(dx) * Cell < Recentre && Math.Abs(dy) * Cell < Recentre) { Mask(grid); return; }
            Shift(u, dx, dy); Shift(v, dx, dy); Shift(pressure, dx, dy);
            Origin = target; Mask(grid);
            for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++) {
                int sx = x + dx, sy = y + dy;
                if ((uint)sx < Width && (uint)sy < Height) continue;
                int i = y * Width + x; u[i] = still[i] ? 0 : wind.X * open[i]; v[i] = 0; pressure[i] = 0;
            }
        }

        private void Shift(float[] field, int dx, int dy) {
            Array.Copy(field, u0, field.Length);
            for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++) {
                int sx = x + dx, sy = y + dy;
                field[y * Width + x] = (uint)sx < Width && (uint)sy < Height ? u0[sy * Width + sx] : 0;
            }
        }

        private void Mask(WeatherGrid grid) {
            for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++) {
                int i = y * Width + x; float wx = Origin.X + (x + .5f) * Cell, wy = Origin.Y + (y + .5f) * Cell;
                int at = grid.Index(wx, wy); float e = grid.Exposure(at);
                if (at < 0) e = 1;
                still[i] = at >= 0 && (grid.IsSolid(at) || e < StillExposure); open[i] = still[i] ? 0 : e;
                if (still[i]) { u[i] = v[i] = 0; }
            }
            for (int i = 0; i < near.Length; i++) near[i] = still[i] ? 0 : float.MaxValue;
            for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++) {
                int i = y * Width + x;
                if (x > 0) near[i] = MathF.Min(near[i], near[i - 1] + 1);
                if (y > 0) near[i] = MathF.Min(near[i], near[i - Width] + 1);
            }
            for (int y = Height - 1; y >= 0; y--) for (int x = Width - 1; x >= 0; x--) {
                int i = y * Width + x;
                if (x < Width - 1) near[i] = MathF.Min(near[i], near[i + 1] + 1);
                if (y < Height - 1) near[i] = MathF.Min(near[i], near[i + Width] + 1);
            }
            for (int i = 0; i < near.Length; i++) near[i] = Math.Clamp(1 - near[i] * Cell / ShelterReach, 0, 1);
        }

        public void Step(Vec2 wind, float dt) {
            if (!Placed || !(dt > 0)) return;
            ambient = wind;
            accumulator = MathF.Min(accumulator + dt, 4 * StepSeconds);
            while (accumulator >= StepSeconds) { accumulator -= StepSeconds; Advance(StepSeconds); }
        }

        private void Advance(float dt) {
            for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++) {
                int i = y * Width + x; if (still[i]) continue;
                if (x == 0 || x == Width - 1 || y == Height - 1) { u[i] = ambient.X * open[i]; v[i] = ambient.Y * open[i]; continue; }
                float relax = 1 - MathF.Exp(-dt / (FreeRelaxation + (Relaxation - FreeRelaxation) * near[i]));
                u[i] += (ambient.X * open[i] - u[i]) * relax; v[i] += (ambient.Y * open[i] - v[i]) * relax;
            }
            Confine(dt);
            Array.Copy(u, u0, u.Length); Array.Copy(v, v0, v.Length);
            float scale = dt / Cell;
            for (int y = 1; y < Height - 1; y++) for (int x = 1; x < Width - 1; x++) {
                int i = y * Width + x; if (still[i]) continue;
                float bx = x - u0[i] * scale, by = y - v0[i] * scale;
                u[i] = Bilinear(u0, bx, by); v[i] = Bilinear(v0, bx, by);
            }
            Project();
        }

        private void Confine(float dt) {
            for (int y = 1; y < Height - 1; y++) for (int x = 1; x < Width - 1; x++) {
                int i = y * Width + x;
                curl[i] = still[i] ? 0 : (v[i + 1] - v[i - 1] - u[i + Width] + u[i - Width]) / (2 * Cell);
            }
            for (int y = 2; y < Height - 2; y++) for (int x = 2; x < Width - 2; x++) {
                int i = y * Width + x; if (still[i]) continue;
                float gx = (MathF.Abs(curl[i + 1]) - MathF.Abs(curl[i - 1])) / (2 * Cell), gy = (MathF.Abs(curl[i + Width]) - MathF.Abs(curl[i - Width])) / (2 * Cell);
                float length = MathF.Sqrt(gx * gx + gy * gy) + 1e-5f;
                u[i] += Confinement * near[i] * Cell * (gy / length) * curl[i] * dt;
                v[i] -= Confinement * near[i] * Cell * (gx / length) * curl[i] * dt;
            }
        }

        private void Project() {
            for (int y = 1; y < Height - 1; y++) for (int x = 1; x < Width - 1; x++) {
                int i = y * Width + x;
                divergence[i] = still[i] ? 0 : (U(i + 1) - U(i - 1) + V(i + Width) - V(i - Width)) / (2 * Cell);
            }
            for (int k = 0; k < Iterations; k++)
                for (int y = 1; y < Height - 1; y++) for (int x = 1; x < Width - 1; x++) {
                    int i = y * Width + x; if (still[i]) continue;
                    pressure[i] = (P(i, i - 1) + P(i, i + 1) + P(i, i - Width) + P(i, i + Width) - divergence[i] * Cell * Cell) * .25f;
                }
            for (int y = 1; y < Height - 1; y++) for (int x = 1; x < Width - 1; x++) {
                int i = y * Width + x; if (still[i]) continue;
                u[i] -= (P(i, i + 1) - P(i, i - 1)) / (2 * Cell);
                v[i] -= (P(i, i + Width) - P(i, i - Width)) / (2 * Cell);
            }
        }

        private float U(int i) => still[i] ? 0 : u[i];
        private float V(int i) => still[i] ? 0 : v[i];
        private float P(int centre, int other) => still[other] ? pressure[centre] : pressure[other];

        private static float Bilinear(float[] field, float x, float y) {
            x = Math.Clamp(x, 0, Width - 1.001f); y = Math.Clamp(y, 0, Height - 1.001f);
            int ix = (int)x, iy = (int)y; float fx = x - ix, fy = y - iy; int i = iy * Width + ix;
            return (field[i] * (1 - fx) + field[i + 1] * fx) * (1 - fy) + (field[i + Width] * (1 - fx) + field[i + Width + 1] * fx) * fy;
        }

        public Vec2 Sample(Vec2 world) {
            Vec2 external = ExternalWind?.Invoke(world) ?? default;
            if (!Placed) return ambient + external;
            float x = (world.X - Origin.X) / Cell - .5f, y = (world.Y - Origin.Y) / Cell - .5f;
            if (x < 0 || y < 0 || x > Width - 1 || y > Height - 1) return ambient + external;
            return new Vec2(Bilinear(u, x, y), Bilinear(v, x, y)) + external;
        }

        public float SpeedRatio(Vec2 world) {
            float reference = MathF.Max(1, ambient.Length());
            return Sample(world).Length() / reference;
        }
    }
}
