using System;
using System.Diagnostics;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace ScpItdMod.Core {
    // Frame sections the mod reports. Each one is a span inside a frame, so the report can name the cost behind a spike.
    public enum ProfileSection { Motion, Offscreen, Computer, Weather, Tooltip, Count }

    // Measures what a frame costs: wall time for the frame and for each section, managed allocations, and garbage
    // collections. Frame-to-frame variation is what the player sees as stutter, so the percentiles and the spike
    // count matter more than the average. The ring holds the last Window frames and costs no allocations per frame.
    public static class FrameProfiler {
        public const int Window = 600;
        public const double BudgetMilliseconds = 1000.0 / 60.0;

        private static readonly double[] frameMilliseconds = new double[Window];
        private static readonly long[] frameAllocated = new long[Window];
        private static readonly int[] frameGen0 = new int[Window];
        private static readonly int[] frameGen2 = new int[Window];
        private static readonly double[,] sectionMilliseconds = new double[(int)ProfileSection.Count, Window];
        private static readonly double[] current = new double[(int)ProfileSection.Count];
        private static long frameStart, allocatedStart;
        private static int gen0Start, gen2Start;
        private static int head, count;
        private static long spikes;

        public static int Frames => count;
        public static long Spikes => spikes;

        // A section span: created with using, costs one timestamp at each end.
        public readonly struct Span : IDisposable {
            private readonly ProfileSection section;
            private readonly long start;
            internal Span(ProfileSection section) { this.section = section; start = Stopwatch.GetTimestamp(); }
            public void Dispose() => current[(int)section] += Elapsed(start);
        }

        public static Span Section(ProfileSection section) => new Span(section);

        public static void Attach(Harmony harmony) {
            // The begin hook runs before every other draw patch and the end hook after all of them, so the frame span covers the whole mod.
            HarmonyMethod begin = ModuleHost.Bind(typeof(FrameProfiler), nameof(BeginFrame));
            begin.priority = Priority.First;
            HarmonyMethod end = ModuleHost.Bind(typeof(FrameProfiler), nameof(EndFrame));
            end.priority = Priority.Last;
            harmony.Patch(AccessTools.Method(typeof(GameMain), "Draw", new[] { typeof(GameTime) }), prefix: begin);
            harmony.Patch(AccessTools.Method(typeof(GameMain), "Draw", new[] { typeof(GameTime) }), postfix: end);
            ModCommands.Register("scpitd_frameprof", "client: frame time percentiles, sections, allocations and collections", _ => Print());
        }

        public static void BeginFrame() {
            Array.Clear(current);
            gen0Start = GC.CollectionCount(0);
            gen2Start = GC.CollectionCount(2);
            allocatedStart = GC.GetAllocatedBytesForCurrentThread();
            frameStart = Stopwatch.GetTimestamp();
        }

        public static void EndFrame() {
            // Elapsed() and the section accumulators are in seconds; the ring stores milliseconds.
            double milliseconds = Elapsed(frameStart) * 1000.0;
            frameMilliseconds[head] = milliseconds;
            frameAllocated[head] = GC.GetAllocatedBytesForCurrentThread() - allocatedStart;
            frameGen0[head] = GC.CollectionCount(0) - gen0Start;
            frameGen2[head] = GC.CollectionCount(2) - gen2Start;
            for (int s = 0; s < current.Length; s++) { sectionMilliseconds[s, head] = current[s] * 1000.0; }
            if (milliseconds > BudgetMilliseconds) { spikes++; }
            head = (head + 1) % Window;
            if (count < Window) { count++; }
        }

        private static double Elapsed(long start) => (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;

        // Percentiles of the samples in the window. Allocates, so it runs only on request, never inside a frame.
        public static double Percentile(double[] samples, int length, double fraction) {
            if (length == 0) { return 0; }
            Array.Sort(samples, 0, length);
            int index = Math.Min(length - 1, (int)Math.Ceiling(fraction * length) - 1);
            return samples[Math.Max(0, index)];
        }

        private static void Print() {
            if (count == 0) { ModLog.Message("frame profile: no frames yet"); return; }
            var sample = new double[count];
            for (int i = 0; i < count; i++) { sample[i] = frameMilliseconds[Index(i)]; }
            double p50 = Percentile(sample, count, .50), p95 = Percentile(sample, count, .95), p99 = Percentile(sample, count, .99);
            double worst = Percentile(sample, count, 1.0);
            ModLog.Message($"frame: frames={count} p50={p50:F2}ms p95={p95:F2}ms p99={p99:F2}ms max={worst:F2}ms budget={BudgetMilliseconds:F2}ms over-budget={spikes}");
            var allocated = new double[count];
            long collected0 = 0, collected2 = 0;
            for (int i = 0; i < count; i++) { int at = Index(i); allocated[i] = frameAllocated[at]; collected0 += frameGen0[at]; collected2 += frameGen2[at]; }
            ModLog.Message($"allocation per frame: p50={Percentile(allocated, count, .50) / 1024:F1}KB p99={Percentile(allocated, count, .99) / 1024:F1}KB max={Percentile(allocated, count, 1.0) / 1024:F1}KB; gen0 collections in window={collected0} gen2={collected2}");
            for (int s = 0; s < (int)ProfileSection.Count; s++) {
                for (int i = 0; i < count; i++) { sample[i] = sectionMilliseconds[s, Index(i)]; }
                ModLog.Message($"section {(ProfileSection)s}: p95={Percentile(sample, count, .95):F2}ms p99={Percentile(sample, count, .99):F2}ms max={Percentile(sample, count, 1.0):F2}ms");
            }
        }

        // Index of the i-th oldest sample.
        private static int Index(int i) => (head - count + i + Window) % Window;
    }
}
