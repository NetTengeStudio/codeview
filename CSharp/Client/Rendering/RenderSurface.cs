using System;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Motion;

namespace ScpItdMod.Rendering {
    public sealed class RenderSurface : IDisposable {
        public static readonly SurfaceBudget Paper = new SurfaceBudget(48L << 20);
        public static readonly SurfaceBudget Monitors = new SurfaceBudget(32L << 20);
        public static readonly SurfaceBudget Desktops = new SurfaceBudget(16L << 20);
        public static readonly SurfaceBudget Cameras = new SurfaceBudget(8L << 20);
        public static readonly SurfaceBudget Pool = new SurfaceBudget(16L << 20);
        private readonly SurfaceBudget budget;
        private readonly long bytes;
        private bool disposed;
        public RenderTarget2D Target { get; }
        public RenderSurface(GraphicsDevice device, int width, int height, SurfaceBudget budget) {
            if (device == null || width <= 0 || height <= 0) { throw new ArgumentOutOfRangeException(nameof(width)); }
            this.budget = budget ?? throw new ArgumentNullException(nameof(budget)); bytes = checked((long)width * height * 4);
            if (!budget.Reserve(bytes)) { throw new InvalidOperationException($"GPU surface budget exceeded: {width}x{height}, retained={budget.Bytes}, limit={budget.Limit}."); }
            try { Target = new RenderTarget2D(device, width, height, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents); }
            catch { budget.Release(bytes); throw; }
        }
        public void Dispose() { if (disposed) { return; } disposed = true; try { Target.Dispose(); } finally { budget.Release(bytes); } }
    }
}
