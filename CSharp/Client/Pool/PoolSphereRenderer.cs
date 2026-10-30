using System;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;
using ScpItdMod.Rendering;

namespace ScpItdMod.Pool {
    public sealed class PoolSphereRenderer : IDisposable {
        public const float ProjectionExtent = SphereBatch.ProjectionExtent;
        private static SphereBatch batch;
        private static GraphicsDevice graphics;
        private RenderSurface surface;
        private uint epoch, revision, tick;
        private double paintedAt;
        public Texture2D Texture => surface?.Target;
        public static long DrawCalls => batch?.DrawCalls ?? 0;
        public static long TargetSwitches => batch?.TargetSwitches ?? 0;
        public static Rectangle Source(int index) => new Rectangle(index % 4 * SphereBatch.Tile, index / 4 * SphereBatch.Tile, SphereBatch.Tile, SphereBatch.Tile);
        private static void Prepare(GraphicsDevice device) {
            if (ReferenceEquals(graphics, device) && batch != null) { return; }
            ReleaseShared(); graphics = device;
            var materials = new Texture2D[16];
            for (int i = 0; i < materials.Length; i++) { materials[i] = TextureStore.File("Content/Items/Pool/Materials/ball_" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture) + ".png"); }
            batch = new SphereBatch(device, materials);
        }
        public bool Update(PoolReplica view, double now) {
            if (!view.Ready) { return false; }
            GraphicsDevice device = GameMain.Instance.GraphicsDevice; Prepare(device);
            bool invalid = surface == null || surface.Target.IsDisposed || surface.Target.IsContentLost || !ReferenceEquals(surface.Target.GraphicsDevice, device);
            if (!invalid && !view.Meta.Moving && epoch == view.Meta.Epoch && revision == view.Meta.Revision && tick == view.Meta.Tick && paintedAt >= view.ReceivedAt + PoolReplica.Delay) { return true; }
            if (invalid) {
                surface?.Dispose(); surface = null;
                if (RenderSurface.Pool.Bytes + SphereBatch.Side * SphereBatch.Side * 4 > RenderSurface.Pool.Limit) { ModLog.Once("Pool sphere cache budget reached; unretained tables use the existing sprite atlas."); return false; }
                surface = new RenderSurface(device, SphereBatch.Side, SphereBatch.Side, RenderSurface.Pool);
            }
            Span<ScpItdMod.Motion.Rotation3> orientation = stackalloc ScpItdMod.Motion.Rotation3[16]; ushort visible = 0;
            for (int i = 0; i < 16; i++) { orientation[i] = view.Orientation(i, now); if (!view.Sample(i, now).Down) { visible |= (ushort)(1 << i); } }
            batch.Draw(surface.Target, orientation, visible);
            epoch = view.Meta.Epoch; revision = view.Meta.Revision; tick = view.Meta.Tick; paintedAt = now; return true;
        }
        public void Dispose() { surface?.Dispose(); surface = null; }
        public static void ReleaseShared() { batch?.Dispose(); batch = null; graphics = null; }
    }
}
