using System;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using ScpItdMod.Commands;
using ScpItdMod.Core;

namespace ScpItdMod.Motion {
    public static class MotionClient {
        private const string FrameFeature = "motion frame";
        private const string StatsCommandFeature = "motion stats command";
        private const string MotionCommandFeature = "motion blur command";
        private const string ReducedCommandFeature = "reduced motion command";

        public static double InputTime { get; private set; }

        public static readonly MotionRuntime Runtime = new MotionRuntime(256);

        public static void Attach(Harmony harmony) {
            MotionBridge.Connect(Runtime);
            Runtime.ReducedMotion = Prefs.Flag("motion.reduced", false);
            Rendering.MotionBlur.Enabled = Prefs.Flag("motion.blur", true);
            harmony.Patch(AccessTools.Method(typeof(GameMain), "Draw", new[] { typeof(GameTime) }),
                prefix: new HarmonyMethod(typeof(MotionClient), nameof(Frame)) { priority = Priority.First });
            ClientCommands.Register("scpitd_motionstats", "client: motion CPU, active properties and allocations", _ => Contained.Run(StatsCommandFeature, ShowStats));
            ClientCommands.Register("scpitd_reducedmotion", "client: toggle optional visual motion", _ => Contained.Run(ReducedCommandFeature, ToggleReduced));
            ClientCommands.Register("scpitd_motionblur", "client: toggle trajectory motion blur, or specify on/off", args => Contained.Run(MotionCommandFeature, () => SetBlur(args)));
        }

        public static void Frame() {
            if (Contained.IsDisabled(FrameFeature)) { return; }
            try {
                using var span = FrameProfiler.Section(ProfileSection.Motion);
                InputTime = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
                bool paused = !GameMain.WindowActive || GameMain.Instance.Paused || Screen.Selected != GameMain.GameScreen;
                Runtime.Advance(InputTime, paused);
                FrameClock.Tick(InputTime, paused);
            }
            catch (Exception error) { Contained.Fail(FrameFeature, error); }
        }

        public static void Reset() {
            Runtime.Reset();
            Rendering.MotionBlur.Release();
        }

        private static void ShowStats() {
            MotionStatistics stats = Runtime.Statistics();
            Rendering.MotionBlurRenderer blur = Rendering.MotionBlur.Renderer;
            string[] lines = {
                $"motion exposure: enabled={Rendering.MotionBlur.Enabled} draws={blur?.DrawCalls ?? 0} sample-pixels={blur?.SamplePixels ?? 0} last-samples={blur?.LastSamples ?? 0} last-travel={blur?.LastTravel ?? 0:F2}px; GPU time unmeasured",
                $"GPU surfaces: paper={Rendering.RenderSurface.Paper.Bytes}B monitors={Rendering.RenderSurface.Monitors.Bytes}B desktops={Rendering.RenderSurface.Desktops.Bytes}B cameras={Rendering.RenderSurface.Cameras.Bytes}B pool={Rendering.RenderSurface.Pool.Bytes}B; sphere draw submissions={Pool.PoolSphereRenderer.DrawCalls} target switch submissions={Pool.PoolSphereRenderer.TargetSwitches}",
                $"native tooltips: bakes={Ui.NativeTooltipMotion.Bakes} surface-allocations={Ui.NativeTooltipMotion.Allocations} drawn-frames={Ui.NativeTooltipMotion.Draws}; offscreen phase: pending={Rendering.OffscreenPhase.Pending} render-target-leaks={Rendering.OffscreenPhase.Leaks}",
                $"stats panel: bakes={Ui.StatsPanel.Bakes} drawn-frames={Ui.StatsPanel.Draws}",
                $"motion: active={stats.Active} properties={stats.Properties} timelines={stats.Timelines} cpu={stats.CpuMicroseconds:F2}us p95={stats.P95Microseconds:F2}us p99={stats.P99Microseconds:F2}us allocated={stats.AllocatedBytes}B spring-matrices={stats.SpringMatrices}; GPU time unmeasured",
            };
            foreach (string line in lines) { ModLog.Message(line); }
            Ui.StatsPanel.Show(lines);
        }

        private static void ToggleReduced() {
            Runtime.ReducedMotion = !Runtime.ReducedMotion;
            Prefs.Set("motion.reduced", Runtime.ReducedMotion ? "1" : "0");
            ModLog.Message($"Reduced motion: {Runtime.ReducedMotion}");
        }

        private static void SetBlur(string[] args) {
            bool enabled;
            if (args.Length == 0) { enabled = !Rendering.MotionBlur.Enabled; }
            else if (args[0] == "on") { enabled = true; }
            else if (args[0] == "off") { enabled = false; }
            else {
                ModLog.Message("scpitd_motionblur [on|off]");
                return;
            }
            Rendering.MotionBlur.Enabled = enabled;
            Prefs.Set("motion.blur", enabled ? "1" : "0");
            ModLog.Message($"Trajectory motion blur: {enabled}; exposure={Rendering.MotionBlur.Shutter.Seconds * 1000:F2}ms");
        }
    }
}
