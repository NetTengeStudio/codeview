using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Rendering;
using ScpItdMod.Weather.Material;

namespace ScpItdMod.Weather {
    internal sealed class SnowCoverRenderer : IDisposable {
        private struct Vertex : IVertexType {
            private static readonly VertexDeclaration declaration = new VertexDeclaration(
                new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
                new VertexElement(12, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 0),
                new VertexElement(28, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 1),
                new VertexElement(36, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 2),
                new VertexElement(44, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 3));
            public Vector3 Position;
            public Vector4 Material;
            public Vector2 Cover;
            public Vector2 Extra;
            public Vector2 Horizon;
            VertexDeclaration IVertexType.VertexDeclaration => declaration;
        }
        private readonly GraphicsDevice device;
        private readonly Effect effect;
        private readonly EffectParameter transform, sun, pixelSize, sky;
        private SnowSlice[] slices = Array.Empty<SnowSlice>();
        private Vertex[] vertices = Array.Empty<Vertex>();

        internal SnowCoverRenderer(GraphicsDevice device, byte[] shader) {
            this.device = device; effect = new Effect(device, shader);
            try {
                transform = effect.Parameters["MatrixTransform"] ?? throw new InvalidOperationException("Snow transform missing");
                sun = effect.Parameters["Sun"] ?? throw new InvalidOperationException("Snow sun missing");
                pixelSize = effect.Parameters["PixelMetres"] ?? throw new InvalidOperationException("Snow pixel size missing");
                sky = effect.Parameters["Sky"] ?? throw new InvalidOperationException("Snow sky missing");
                (effect.Parameters["BloodDepth"] ?? throw new InvalidOperationException("Snow blood depth missing")).SetValue(SnowPhysics.ActiveDepth);
            } catch { effect.Dispose(); throw; }
        }

        internal void Draw(SnowPack snow, in WeatherRect view, in WeatherState state, Vector2 sunDirection, Vector3 skyTint, Matrix world, float units, float pixelsPerMetre, float sceneLight) {
            int count = SnowCover.Emit(snow, view, ref slices);
            int needed = checked(count * 6);
            if (vertices.Length < needed) vertices = new Vertex[Math.Max(needed, vertices.Length * 2)];
            float pixel = 1 / MathF.Max(1, pixelsPerMetre); int written = 0;
            for (int i = 0; i < count; i++) {
                ref readonly SnowSlice s = ref slices[i];
                if (s.B.X <= view.Left || s.A.X >= view.Right || MathF.Max(s.A.Top, s.B.Top) < view.Bottom || MathF.Min(s.A.Ground, s.B.Ground) > view.Top) continue;
                Vertex a = Point(s.A, 0), b = Point(s.B, 0), c = Point(s.A, s.A.Depth + pixel), d = Point(s.B, s.B.Depth + pixel);
                vertices[written++] = a; vertices[written++] = c; vertices[written++] = b;
                vertices[written++] = b; vertices[written++] = c; vertices[written++] = d;
            }
            if (written == 0) return;
            using var pass = GraphicsPass.Capture(device);
            transform.SetValue(Matrix.CreateScale(units, units, 1) * world * Matrix.CreateOrthographicOffCenter(0, device.Viewport.Width, device.Viewport.Height, 0, 0, 1));
            sun.SetValue(new Vector4(sunDirection, 1 - state.Cloud, sceneLight));
            pixelSize.SetValue(pixel);
            sky.SetValue(new Vector4(skyTint, 1));
            device.BlendState = BlendState.NonPremultiplied; device.DepthStencilState = DepthStencilState.None; device.RasterizerState = RasterizerState.CullNone;
            effect.CurrentTechnique.Passes[0].Apply();
            device.DrawUserPrimitives(PrimitiveType.TriangleList, vertices, 0, written / 3);
        }

        private static Vertex Point(in SnowSample sample, float height) => new Vertex {
            Position = new Vector3(sample.X, -sample.Ground - height, 0),
            Material = new Vector4(sample.Depth, sample.Slope, sample.Compaction, sample.Track), Cover = new Vector2(height, sample.Load), Extra = new Vector2(sample.Fresh, sample.Blood),
            Horizon = new Vector2(sample.HorizonLeft, sample.HorizonRight)
        };

        public void Dispose() => effect.Dispose();
    }
}
