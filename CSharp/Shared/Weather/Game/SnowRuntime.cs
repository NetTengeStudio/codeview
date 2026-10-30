using System;
using Barotrauma;
using ScpItdMod.Core;
using ScpItdMod.Weather.Material;

namespace ScpItdMod.Weather {
    // The snow that characters stand on, wade through and leave tracks in. It is the MPM material itself: nothing here is a height field or a stamp.
    internal static class SnowRuntime {
        internal static SnowPack Pack { get; private set; }
        internal static SnowClimate Climate { get; private set; }
        internal static bool Ready => Pack != null && Pack.Material.Count > 0;
        // Called whenever the weather grid is (re)built. A rebuilt grid keeps the material and only changes the terrain it collides with.
        internal static void Bind(WeatherConfig config, WeatherGrid grid) {
            if (Pack == null) {
                var pack = new SnowPack(config, grid);
                float temperature = Math.Min(-1, WeatherService.Current.Temperature);
                pack.Initialize(config.InitialDepth, config.InitialDensity, temperature, true);
                Pack = pack; Climate = new SnowClimate(pack, config, WeatherNoise.Seed(Level.Loaded?.Seed ?? "vostok")); pack.Climate = Climate;
            }
            else { Pack.Rebuild(grid); Climate.Resize(); }
        }
        private const float ReportSeconds = 10;
        private static float reportClock, worst;
        internal static void Step(float dt) {
            if (Pack == null) return;
            Pack.Frame++;
            SnowCharacters.Step(Pack.Material, dt);
            // One line per ReportSeconds while somebody is standing in the snow: what the material costs per frame, so a slow round can be read from the console log.
            worst = Math.Max(worst, SnowCharacters.Milliseconds); reportClock += dt;
            if (reportClock < ReportSeconds) return;
            reportClock = 0;
            if (SnowCharacters.Feet > 0 || worst > 0) ModLog.Message($"Snow: step {SnowCharacters.Milliseconds:F1} ms (worst {worst:F1}), feet {SnowCharacters.Feet}, standing {SnowCharacters.Standing} (swinging {SnowCharacters.Reasons[1]}, no snow {SnowCharacters.Reasons[2]}, held above {SnowCharacters.Reasons[3]}, gap {SnowCharacters.Gap:F2} m), boots pressed {SnowPrint.Pressing} times ({SnowPrint.Settled} already settled), compressed particles {SnowPrint.Compressed}, you [{Your(0)} | {Your(1)}] walk x{SnowCharacters.Speed(Barotrauma.Character.Controlled):F2}, awake {Pack.Material.ActiveCount} of {Pack.Material.Count}, limited substeps {Pack.Material.Limited}{ClientCost()}");
            worst = 0; SnowPrint.Compressed = SnowPrint.Pressing = SnowPrint.Settled = 0;
        }
#if CLIENT
        private static string ClientCost() => $", client weather prepare {WeatherClient.PrepareMilliseconds:F1} ms, draw {WeatherClient.DrawMilliseconds:F1} ms";
#else
        private static string ClientCost() => string.Empty;
#endif
        private static string Your(int side) => SnowCharacters.YourReason[side] < 0 ? "no foot" : $"{new[] { "stands", "swings", "no snow", "above" }[SnowCharacters.YourReason[side]]} gap {SnowCharacters.YourGap[side]:F2}";
        internal static void Weather(in WeatherState weather, float dt) => Climate?.Step(weather, dt);
        internal static void Observe(in WeatherState weather, float dt) => Climate?.Observe(weather, dt);
        internal static void Stop() { SnowCharacters.Clear(); Pack = null; Climate = null; }

        // The walkable surface under a point: the highest ground inside reach of the foot.
        private static int Slot(Vec2 point, float above, float below) {
            SnowPack pack = Pack; if (pack == null) return -1;
            WeatherGrid grid = pack.Grid; int column = (int)MathF.Floor((point.X - grid.Origin.X) / grid.Cell);
            if ((uint)column >= (uint)grid.Width) return -1;
            int best = -1;
            for (int at = grid.ColumnStart(column), end = grid.ColumnStart(column + 1); at < end; at++) {
                int slot = grid.ColumnSlot(at); float ground = grid.SurfaceY(slot);
                if (ground > point.Y + above || ground < point.Y - below - pack.MaximumDepth) continue;
                if (best < 0 || ground > grid.SurfaceY(best)) best = slot;
            }
            return best;
        }
        internal static bool Surface(Vec2 point, float above, float below, out float low, out float high) {
            low = high = 0; int best = Slot(point, above, below); if (best < 0) return false;
            SnowPack pack = Pack; WeatherGrid grid = pack.Grid;
            float floor = grid.SurfaceY(best) - grid.Cell, ceiling = MathF.Min(grid.Ceiling(best), grid.SurfaceY(best) + pack.MaximumDepth + grid.Cell);
            return pack.Material.Column(point.X, SnowSkin.Half, floor, ceiling, out low, out high);
        }
        internal static SnowFooting Footing(Vec2 point, float below) {
            if (!Surface(point, SnowContactModel.FootAbove, below, out float low, out float high)) return SnowFooting.Bare;
            SnowPack pack = Pack;
            float depth = high - low; if (depth < SnowSkin.MinimumDepth) return SnowFooting.Bare;
            float density = pack.Material.TopDensity(point.X, SnowSkin.Step * 2, low - .01f, high + .01f, high, .1f);
            float compaction = density > 0 ? SnowPhysics.Compaction(density) : 0;
            // Powder is the loose top layer: it is as thick as the part of the column that is still near fresh density.
            float fresh = density > 0 && density < SnowPhysics.Density(SnowContactModel.PackedCompaction) ? depth : 0;
            return new SnowFooting(depth, compaction, fresh, SnowCharacters.SinkageAt(point));
        }
    }
}
