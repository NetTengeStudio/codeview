using System;
using System.Collections.Generic;
using ScpItdMod.Core;
using ScpItdMod.Weather.Material;

namespace ScpItdMod.Weather {
    // The picture of the snow is a section of the same particles the feet stand on. A particle top is rough at the scale of one particle, so the section is smoothed along each
    // continuous run and the slope is taken over a span of several samples: sampled raw, every slice would carry its own random slope and the surface would show as evenly spaced bands.
    internal static class SnowCover {
        private const float Step = SnowSkin.Step, Window = SnowSkin.Half;
        // The bottom of the snow is the lowest particle of each sample, which steps by a particle radius from sample to sample along the hexagonal rows: drawn as is, the lower edge is a saw.
        // The ground under snow is smooth at the scale of a few particles, so the bottom is smoothed over a wider span than the top.
        private const int Reach = 4, GroundReach = 6;
        // Snow falls on the pack a grain at a time, and a layer that is not yet complete is a surface with holes in it: single columns a grain (six centimetres) lower than the rest, one or two samples wide.
        // The section is closed with a flat element of CloseReach samples either side (the highest height in the span, then the lowest of those), which fills a hole narrower than the element and
        // leaves everything wider alone: a footprint or a pit is a hole as wide as a boot or a blade, and its walls and floor stay as they are.
        private const int CloseReach = 2;
        // How many samples either side the horizon is searched over: about half a metre, the scale of a drift or a pit the sky can be hidden by.
        // The horizon reads the surface smoothed over a wider span than the picture: a one-column step in the snow would otherwise show as a dark seam on the lower side.
        private const int HorizonReach = 8, HorizonSmooth = 6;
        private sealed class Run {
            internal readonly List<SnowSample> Samples = new List<SnowSample>(); internal readonly List<float> Top = new List<float>(), Floor = new List<float>(), Stain = new List<float>(), Compaction = new List<float>();
            internal int Last, First; internal float[] Eroded = Array.Empty<float>(), Opened = Array.Empty<float>(), Smooth = Array.Empty<float>(), Wide = Array.Empty<float>(), Ground = Array.Empty<float>(), Powder = Array.Empty<float>(), Packing = Array.Empty<float>();
        }
        private static readonly List<Run> runs = new List<Run>();
        private static int used;
        private static Run Start(int index) {
            if (used == runs.Count) runs.Add(new Run());
            Run run = runs[used++]; run.Samples.Clear(); run.Top.Clear(); run.Floor.Clear(); run.Stain.Clear(); run.Compaction.Clear(); run.Last = index; run.First = index; return run;
        }
        // A run continues across weather-grid columns: the snow surface is one line whichever column's cell holds it, so the run is the one that ended at the previous sample on about the same floor.
        private static Run Continue(int index, float floor, float tolerance) {
            for (int k = 0; k < used; k++) { Run run = runs[k]; if (run.Last == index - 1 && MathF.Abs(run.Floor[run.Floor.Count - 1] - floor) <= tolerance) return run; }
            return Start(index);
        }
        private static float Smooth(float[] values, int at, int reach, int count) {
            float sum = 0, weight = 0;
            for (int d = -reach; d <= reach; d++) { int i = at + d; if (i < 0 || i >= count) continue; float w = reach + 1 - Math.Abs(d); sum += values[i] * w; weight += w; }
            return sum / weight;
        }
        // The section is closed (holes narrower than the element are filled), then opened with the same element: a peak narrower than it is not a layer of snow but a grain or two that the wind set down or
        // that a collapse left standing, and it is cut to the surface it stands on. A drift, a ridge a hand wide at its foot and a wall of snow are all wider and stay.
        private static void Close(Run run, int n) {
            for (int j = 0; j < n; j++) { float high = float.MinValue; for (int d = -CloseReach; d <= CloseReach; d++) { int i = j + d; if (i >= 0 && i < n) high = Math.Max(high, run.Top[i]); } run.Eroded[j] = high; }
            for (int j = 0; j < n; j++) { float low = float.MaxValue; for (int d = -CloseReach; d <= CloseReach; d++) { int i = j + d; if (i >= 0 && i < n) low = Math.Min(low, run.Eroded[i]); } run.Opened[j] = low; }
            for (int j = 0; j < n; j++) { float low = float.MaxValue; for (int d = -CloseReach; d <= CloseReach; d++) { int i = j + d; if (i >= 0 && i < n) low = Math.Min(low, run.Opened[i]); } run.Eroded[j] = low; }
            for (int j = 0; j < n; j++) { float high = float.MinValue; for (int d = -CloseReach; d <= CloseReach; d++) { int i = j + d; if (i >= 0 && i < n) high = Math.Max(high, run.Eroded[i]); } run.Opened[j] = high; }
        }
        private static float Smooth(List<float> values, int at, int reach) {
            float sum = 0, weight = 0;
            for (int d = -reach; d <= reach; d++) { int i = at + d; if (i < 0 || i >= values.Count) continue; float w = reach + 1 - Math.Abs(d); sum += values[i] * w; weight += w; }
            return sum / weight;
        }

        // The picture is cut into blocks of BlockSamples samples; a block is rebuilt only when the material under it (and under the margin its smoothing reads) has changed.
        private const int BlockSamples = 32, Margin = GroundReach + 1;
        private sealed class Block {
            internal SnowSlice[] Slices = Array.Empty<SnowSlice>(); internal int Count; internal ulong Stamp; internal bool Built; internal long Seen;
            // What the section really is (Goal*, Slope*) against what is drawn (Shown*, with the speed of its fall): the picture follows a change over a short time, see Ease.
            internal float[] GoalA = Array.Empty<float>(), GoalB = Array.Empty<float>(), ShownA = Array.Empty<float>(), ShownB = Array.Empty<float>(), SpeedA = Array.Empty<float>(), SpeedB = Array.Empty<float>(), SlopeA = Array.Empty<float>(), SlopeB = Array.Empty<float>();
            internal bool Settling;
        }
        private static readonly Dictionary<int, Block> blocks = new Dictionary<int, Block>();
        private static readonly List<int> stale = new List<int>();
        private static SnowMpm owner; private static WeatherGrid ownerGrid; private static long frame;
        internal static int Built { get; private set; }

        // The surface does not jump: snow that is taken away or collapses falls to its new level under gravity (starting from a slow slide, as packed snow lets go), snow that is laid on rises under the
        // blade or the drift, and a change smaller than a frame's worth of either is followed at once, so a footprint is never drawn behind the boot that makes it. Seconds in metres per second.
        internal const float FallStart = .5f, FallGravity = 5, FallLimit = 3, RiseSpeed = .8f, Tolerance = .0005f;
        private static long clock = System.Diagnostics.Stopwatch.GetTimestamp();
        internal static int Emit(SnowPack pack, in WeatherRect view, ref SnowSlice[] output, float seconds = -1) {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (seconds < 0) seconds = Math.Clamp((now - clock) / (float)System.Diagnostics.Stopwatch.Frequency, 0, .1f);
            clock = now;
            WeatherGrid grid = pack.Grid; SnowMpm material = pack.Material;
            if (!ReferenceEquals(owner, material) || !ReferenceEquals(ownerGrid, grid)) { blocks.Clear(); owner = material; ownerGrid = grid; }
            frame++; Built = 0;
            if (!grid.Bounds.Overlaps(view)) return 0;
            int first = (int)MathF.Floor(MathF.Max(view.Left, grid.Bounds.Left) / Step), last = (int)MathF.Ceiling(MathF.Min(view.Right, grid.Bounds.Right) / Step), count = 0;
            for (int b = (int)MathF.Floor(first / (float)BlockSamples), end = (int)MathF.Floor(last / (float)BlockSamples); b <= end; b++) {
                if (!blocks.TryGetValue(b, out Block block)) blocks[b] = block = new Block();
                block.Seen = frame;
                ulong stamp = material.Stamp((b * BlockSamples - Margin) * Step - Window - .5f, ((b + 1) * BlockSamples + Margin) * Step + Window + .5f);
                if (!block.Built || block.Stamp != stamp) { bool had = block.Built; if (had) Remember(block); Build(pack, b, block); Ease(block, had); block.Stamp = stamp; block.Built = true; Built++; }
                if (block.Settling) Advance(block, seconds);
                if (count + block.Count > output.Length) Array.Resize(ref output, Math.Max(256, Math.Max(output.Length * 2, count + block.Count)));
                Array.Copy(block.Slices, 0, output, count, block.Count); count += block.Count;
            }
            stale.Clear(); foreach (var pair in blocks) if (frame - pair.Value.Seen > 600) stale.Add(pair.Key);
            for (int k = 0; k < stale.Count; k++) blocks.Remove(stale[k]);
            return count;
        }

        private struct Memory { internal float Shown, Speed; }
        private static readonly Dictionary<long, Memory> remembered = new Dictionary<long, Memory>();
        // A sample is the same sample after a rebuild when it lies at the same place of the same surface: its index along x and the ground it stands on, to half a metre.
        private static long Key(in SnowSample sample) => (long)MathF.Round(sample.X / Step) * 65536 + (int)MathF.Round(sample.Ground * 2) + 32768;
        private static void Remember(Block block) {
            remembered.Clear();
            for (int k = 0; k < block.Count; k++) {
                remembered[Key(block.Slices[k].A)] = new Memory { Shown = block.ShownA[k], Speed = block.SpeedA[k] };
                remembered[Key(block.Slices[k].B)] = new Memory { Shown = block.ShownB[k], Speed = block.SpeedB[k] };
            }
        }
        // After a rebuild the new section is the goal; each sample starts from what was drawn there before (or is at its goal when it is new).
        private static void Ease(Block block, bool had) {
            int n = block.Count;
            if (block.GoalA.Length < n) {
                int size = Math.Max(64, n * 2); block.GoalA = new float[size]; block.GoalB = new float[size]; block.ShownA = new float[size]; block.ShownB = new float[size];
                block.SpeedA = new float[size]; block.SpeedB = new float[size]; block.SlopeA = new float[size]; block.SlopeB = new float[size];
            }
            block.Settling = false;
            for (int k = 0; k < n; k++) {
                ref SnowSlice slice = ref block.Slices[k];
                block.GoalA[k] = slice.A.Depth; block.GoalB[k] = slice.B.Depth; block.SlopeA[k] = slice.A.Slope; block.SlopeB[k] = slice.B.Slope;
                Memory a = default, b = default; bool oldA = had && remembered.TryGetValue(Key(slice.A), out a), oldB = had && remembered.TryGetValue(Key(slice.B), out b);
                block.ShownA[k] = oldA ? a.Shown : slice.A.Depth; block.SpeedA[k] = oldA ? a.Speed : 0; block.ShownB[k] = oldB ? b.Shown : slice.B.Depth; block.SpeedB[k] = oldB ? b.Speed : 0;
                block.Settling |= MathF.Abs(block.ShownA[k] - block.GoalA[k]) > Tolerance || MathF.Abs(block.ShownB[k] - block.GoalB[k]) > Tolerance;
            }
        }
        private static float Follow(float shown, float goal, ref float speed, float seconds) {
            float gap = goal - shown;
            if (MathF.Abs(gap) <= Tolerance) { speed = 0; return goal; }
            if (gap > 0) { speed = 0; float rise = RiseSpeed * seconds; return rise >= gap ? goal : shown + rise; }
            speed = MathF.Min(MathF.Max(speed, FallStart) + FallGravity * seconds, FallLimit);
            float fall = speed * seconds; if (fall >= -gap) { speed = 0; return goal; }
            return shown - fall;
        }
        // Moves the drawn depths towards the goals and writes them (and the slope they make) into the slices; once every sample has arrived the slopes of the section itself are restored.
        private static void Advance(Block block, float seconds) {
            bool moving = false;
            for (int k = 0; k < block.Count; k++) {
                ref SnowSlice slice = ref block.Slices[k];
                block.ShownA[k] = Follow(block.ShownA[k], block.GoalA[k], ref block.SpeedA[k], seconds); block.ShownB[k] = Follow(block.ShownB[k], block.GoalB[k], ref block.SpeedB[k], seconds);
                bool arrived = block.ShownA[k] == block.GoalA[k] && block.ShownB[k] == block.GoalB[k]; moving |= !arrived;
                slice.A.Depth = block.ShownA[k]; slice.B.Depth = block.ShownB[k];
                if (slice.A.Fresh > 0) slice.A.Fresh = slice.A.Depth; if (slice.B.Fresh > 0) slice.B.Fresh = slice.B.Depth;
                float slope = arrived ? 0 : (slice.B.Top - slice.A.Top) / MathF.Max(1e-4f, slice.B.X - slice.A.X);
                slice.A.Slope = arrived ? block.SlopeA[k] : slope; slice.B.Slope = arrived ? block.SlopeB[k] : slope;
            }
            block.Settling = moving;
        }

        private static readonly SnowStrip strip = new SnowStrip();
        private static void Build(SnowPack pack, int b, Block block) {
            WeatherGrid grid = pack.Grid; SnowMpm material = pack.Material; used = 0; block.Count = 0;
            int from = b * BlockSamples, to = (b + 1) * BlockSamples;
            // The particles under the whole block are read once; every sample then asks the strip.
            float bottom = float.MaxValue, top = float.MinValue;
            for (int i = from - Margin; i <= to + Margin; i++) {
                int column = (int)MathF.Floor((i * Step - grid.Origin.X) / grid.Cell);
                if ((uint)column >= (uint)grid.Width) continue;
                for (int at = grid.ColumnStart(column), end = grid.ColumnStart(column + 1); at < end; at++) {
                    int slot = grid.ColumnSlot(at); float ground = grid.SurfaceY(slot);
                    bottom = MathF.Min(bottom, ground - grid.Cell); top = MathF.Max(top, MathF.Min(grid.Ceiling(slot), ground + pack.MaximumDepth + grid.Cell));
                }
            }
            if (bottom <= top) material.Fill(strip, (from - Margin) * Step - Window, bottom, (to + Margin) * Step + Window, top); else strip.Clear();
            for (int i = from - Margin; i <= to + Margin; i++) {
                float x = i * Step; int column = (int)MathF.Floor((x - grid.Origin.X) / grid.Cell);
                if ((uint)column >= (uint)grid.Width) continue;
                for (int at = grid.ColumnStart(column), end = grid.ColumnStart(column + 1); at < end; at++) {
                    int slot = grid.ColumnSlot(at); float ground = grid.SurfaceY(slot);
                    float floor = ground - grid.Cell, ceiling = MathF.Min(grid.Ceiling(slot), ground + pack.MaximumDepth + grid.Cell);
                    if (!strip.Column(x, Window, floor, ceiling, out float low, out float high) || high - low < SnowSkin.MinimumDepth) continue;
                    float density = strip.TopState(x, Window, low, ceiling, high, .1f, out float stain), compaction = density > 0 ? SnowPhysics.Compaction(density) : 0;
                    Run run = Continue(i, low, grid.Cell * 1.5f);
                    run.Last = i; run.Top.Add(high); run.Floor.Add(low); run.Stain.Add(stain); run.Compaction.Add(compaction);
                    run.Samples.Add(new SnowSample { X = x, Compaction = compaction, Fresh = density > 0 && density < SnowPhysics.Density(SnowContactModel.PackedCompaction) ? 1 : 0 });
                }
            }
            for (int k = 0; k < used; k++) {
                Run run = runs[k]; int n = run.Samples.Count; if (n < 2) continue;
                if (run.Smooth.Length < n) { run.Eroded = new float[n * 2]; run.Opened = new float[n * 2]; run.Smooth = new float[n * 2]; run.Wide = new float[n * 2]; run.Ground = new float[n * 2]; run.Powder = new float[n * 2]; run.Packing = new float[n * 2]; }
                float[] smooth = run.Smooth, wide = run.Wide, ground = run.Ground, powder = run.Powder, packing = run.Packing;
                // Compaction is measured per column, so raw it steps at every column edge where the density changes; it is spread over the ground reach like the floor.
                for (int j = 0; j < n; j++) packing[j] = Smooth(run.Compaction, j, GroundReach);
                // Fresh powder is decided sample by sample: a hard 0/1 switch would print every switch as a vertical band across the drift, so the flag is spread over the reach of the smoothing.
                for (int j = 0; j < n; j++) powder[j] = run.Samples[j].Fresh > 0 ? 1 : 0;
                Close(run, n);
                for (int j = 0; j < n; j++) { smooth[j] = Smooth(run.Opened, j, Reach, n); wide[j] = Smooth(run.Top, j, HorizonSmooth); ground[j] = Smooth(run.Floor, j, GroundReach); }
                for (int j = 0; j < n; j++) {
                    SnowSample s = run.Samples[j]; int a = Math.Max(0, j - Reach), c = Math.Min(n - 1, j + Reach);
                    s.Blood = run.Stain[j]; s.Compaction = packing[j]; s.Ground = ground[j]; s.Depth = Math.Max(0, smooth[j] - ground[j]); int slopeA = Math.Max(0, j - GroundReach), slopeC = Math.Min(n - 1, j + GroundReach); s.Slope = (wide[slopeC] - wide[slopeA]) / ((slopeC - slopeA) * Step);
                    float left = 0, right = 0;
                    for (int h = Math.Max(0, j - HorizonReach); h < j; h++) left = Math.Max(left, (wide[h] - wide[j]) / ((j - h) * Step));
                    for (int h = j + 1; h <= Math.Min(n - 1, j + HorizonReach); h++) right = Math.Max(right, (wide[h] - wide[j]) / ((h - j) * Step));
                    s.HorizonLeft = left; s.HorizonRight = right;
                    float share = 0, weight = 0;
                    for (int h = a; h <= c; h++) { float w = Reach + 1 - Math.Abs(h - j); share += powder[h] * w; weight += w; }
                    s.Fresh = share / weight * s.Depth; run.Samples[j] = s;
                }
                for (int j = 0; j + 1 < n; j++) {
                    int index = run.First + j; if (index < from || index >= to) continue;
                    if (block.Count == block.Slices.Length) Array.Resize(ref block.Slices, Math.Max(64, block.Slices.Length * 2));
                    block.Slices[block.Count++] = new SnowSlice { A = run.Samples[j], B = run.Samples[j + 1] };
                }
            }
        }
    }
}
