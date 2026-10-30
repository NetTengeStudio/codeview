using System;
using Microsoft.Xna.Framework.Graphics;

namespace ScpItdMod.Rendering {
    public static class SpriteBatches {
        // The caller retains ownership and disposes the batch at the end of its lifetime.
        public static void Ensure(ref SpriteBatch batch, GraphicsDevice device) {
            if (device == null) { throw new ArgumentNullException(nameof(device)); }
            if (batch != null && !batch.IsDisposed && ReferenceEquals(batch.GraphicsDevice, device)) { return; }
            SpriteBatch previous = batch;
            batch = null;
            previous?.Dispose();
            batch = new SpriteBatch(device);
        }
    }
}
