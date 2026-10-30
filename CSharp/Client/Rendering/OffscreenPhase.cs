using System;
using System.Collections.Generic;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;

namespace ScpItdMod.Rendering {
    // A unit of off-screen work: it may switch render targets and must leave them to the phase that runs it.
    public interface IOffscreenJob {
        // True while the job waits in the queue, so one job is never queued twice in a frame.
        bool Queued { get; set; }
        // Returns false when the result is unavailable; the caller then falls back to its direct path.
        bool Run(SpriteBatch batch);
    }

    // Render-target switches happen only here, at frame start, outside any live SpriteBatch. Switching targets inside
    // the GUI batch flushes it mid-frame and is a known source of flicker on some drivers. The phase also owns the
    // invariant that the frame ends with the back buffer bound, and records a leak when it does not.
    public static class OffscreenPhase {
        private static readonly List<IOffscreenJob> jobs = new List<IOffscreenJob>();
        private static SpriteBatch batch;

        public static int Pending => jobs.Count;
        public static long Leaks { get; private set; }

        public static void Attach(Harmony harmony) {
            harmony.Patch(AccessTools.Method(typeof(GameMain), "Draw", new[] { typeof(GameTime) }),
                prefix: ModuleHost.Bind(typeof(OffscreenPhase), nameof(Run)));
            harmony.Patch(AccessTools.Method(typeof(GameMain), "Draw", new[] { typeof(GameTime) }),
                postfix: ModuleHost.Bind(typeof(OffscreenPhase), nameof(VerifyFrameEnd)));
        }

        public static void Enqueue(IOffscreenJob job) {
            if (job.Queued) { return; }
            job.Queued = true;
            jobs.Add(job);
        }

        public static void Run() {
            if (jobs.Count == 0) { return; }
            using var span = FrameProfiler.Section(ProfileSection.Offscreen);
            GraphicsDevice device = GameMain.Instance.GraphicsDevice;
            SpriteBatches.Ensure(ref batch, device);
            using (GraphicsPass.Capture(device)) {
                for (int i = 0; i < jobs.Count; i++) {
                    IOffscreenJob job = jobs[i];
                    job.Queued = false;
                    try { job.Run(batch); }
                    catch (Exception error) { ModLog.Error("Offscreen job failed: " + error.Message); }
                }
            }
            jobs.Clear();
        }

        public static void VerifyFrameEnd() {
            GraphicsDevice device = GameMain.Instance.GraphicsDevice;
            if (device.RenderTargetCount == 0) { return; }
            Leaks++;
            ModLog.Once("render target left bound at frame end: the back buffer is restored");
            device.SetRenderTarget(null);
        }
    }
}
