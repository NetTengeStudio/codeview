using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ScpItdMod.Weather {
    internal sealed class SnowBackdrop : IDisposable {
        private const int BatchCells = ushort.MaxValue / 4;
        private readonly GraphicsDevice device;
        private readonly Effect effect;
        private readonly EffectTechnique technique;
        private readonly EffectParameter grid, shape, motion, projection, viewport, layer;
        private readonly struct Layer {
            internal readonly float Parallax, Radius, Cell, Fall, Density, Weight;
            internal Layer(float parallax, float radius, float cell, float fall, float density, float weight) {
                Parallax = parallax; Radius = radius; Cell = cell; Fall = fall; Density = density; Weight = weight;
            }
        }
        private struct PreparedLayer { internal Vector4 Grid, Shape, Motion, Travel; internal int Count; }
        private static readonly Layer[] layers = {
            new Layer(.25f, .0035f, .9f, .85f, .9f, .55f), new Layer(.45f, .0035f, .7f, .95f, .8f, .7f),
            new Layer(.7f, .004f, .55f, 1, .6f, .85f), new Layer(2.5f, .005f, 1.2f, 1.05f, .2f, 1.4f),
            new Layer(6, .006f, 2.4f, 1.1f, .08f, 1.8f)
        };
        private readonly PreparedLayer[] prepared = new PreparedLayer[layers.Length];
        internal Vector2 Mean { get; private set; }
        private readonly VertexBuffer vertices;
        private readonly IndexBuffer indices;
        private readonly VertexPositionTexture[] vertexData;
        private readonly ushort[] indexData;
        private readonly RasterizerState rasterizer;
        private Rectangle clipping;
        private bool topologyLost;
        internal SnowBackdrop(GraphicsDevice device, Effect effect) {
            this.device = device; this.effect = effect;
            try {
                technique = effect.Techniques["SnowBackdrop"] ?? throw new InvalidOperationException("Snow backdrop technique missing");
                grid = Parameter("ParticleGrid"); shape = Parameter("ParticleShape"); motion = Parameter("ParticleMotion");
                projection = Parameter("ParticleProjection"); viewport = Parameter("ParticleViewport"); layer = Parameter("ParticleLayer");
                vertexData = new VertexPositionTexture[BatchCells * 4];
                indexData = new ushort[BatchCells * 6];
                var data = vertexData; var order = indexData;
                for (int i = 0; i < BatchCells; i++) {
                    int v = i * 4, at = i * 6;
                    data[v] = new VertexPositionTexture(new Vector3(i, 0, 0), new Vector2(-1, -1));
                    data[v + 1] = new VertexPositionTexture(new Vector3(i, 0, 0), new Vector2(1, -1));
                    data[v + 2] = new VertexPositionTexture(new Vector3(i, 0, 0), new Vector2(-1, 1));
                    data[v + 3] = new VertexPositionTexture(new Vector3(i, 0, 0), new Vector2(1, 1));
                    order[at] = (ushort)v; order[at + 1] = (ushort)(v + 1); order[at + 2] = (ushort)(v + 2);
                    order[at + 3] = (ushort)(v + 2); order[at + 4] = (ushort)(v + 1); order[at + 5] = (ushort)(v + 3);
                }
                vertices = new VertexBuffer(device, VertexPositionTexture.VertexDeclaration, data.Length, BufferUsage.WriteOnly); vertices.SetData(data);
                indices = new IndexBuffer(device, IndexElementSize.SixteenBits, order.Length, BufferUsage.WriteOnly); indices.SetData(order);
                rasterizer = new RasterizerState { CullMode = CullMode.None, ScissorTestEnable = true };
                device.DeviceReset += Reset;
            } catch { Dispose(); throw; }
        }
        private EffectParameter Parameter(string name) => effect.Parameters[name] ?? throw new InvalidOperationException("Snow backdrop parameter missing: " + name);
        internal void Prepare(Vector4 basis, Vector2 centre, Rectangle screen, int width, int height, float wind, float snowfall, double seconds, double travel) {
            float determinant = basis.X * basis.W - basis.Y * basis.Z;
            if (!float.IsFinite(determinant) || MathF.Abs(determinant) < 1e-12f) throw new ArgumentOutOfRangeException(nameof(basis));
            projection.SetValue(new Vector4(2 * basis.W / determinant, -2 * basis.Z / determinant, 2 * basis.Y / determinant, -2 * basis.X / determinant));
            viewport.SetValue(new Vector4(2f * (screen.X + screen.Width * .5f) / width - 1, 1 - 2f * (screen.Y + screen.Height * .5f) / height, (float)screen.Width / width, (float)screen.Height / height));
            clipping = Rectangle.Intersect(screen, device.Viewport.Bounds);
            float pixel = MathF.Max(new Vector2(basis.X, basis.Y).Length() / screen.Width, new Vector2(basis.Z, basis.W).Length() / screen.Height);
            float amount = Math.Clamp(snowfall, 0, 1);
            Vector2 mean = default;
            for (int i = 0; i < layers.Length; i++) {
                ref readonly Layer config = ref layers[i];
                ref PreparedLayer frame = ref prepared[i];
                frame = default;
                if (amount <= .002f) continue;
                float cell = config.Cell, parallax = config.Parallax, radius = config.Radius, worldPixel = pixel / parallax;
                float fade = 1 - Smooth(cell * .2f, cell * .6f, worldPixel);
                float speed = MathF.Sqrt(wind * wind + config.Fall * config.Fall);
                float blur = MathF.Max(0, parallax - 1) * .004f;
                float halfLength = MathF.Min(speed / 120, cell * .5f - radius * 1.45f - blur);
                float energy = amount * config.Density * (MathF.PI * radius * radius + 4 * radius * halfLength) / (cell * cell) * (1 - fade) * config.Weight;
                if (i < 3) mean.X += energy; else mean.Y += energy;
                if (fade <= 0) continue;
                float drift = Phase(travel, (double)cell * 1024), fall = Phase(seconds * config.Fall, (double)cell * 1024);
                float extentX = (MathF.Abs(basis.X) + MathF.Abs(basis.Z)) * .5f / parallax;
                float extentY = (MathF.Abs(basis.Y) + MathF.Abs(basis.W)) * .5f / parallax;
                float left = MathF.Floor((centre.X - drift - extentX) / cell) - 1;
                float bottom = MathF.Floor((centre.Y + fall - extentY) / cell) - 1;
                int columns = checked((int)MathF.Ceiling(extentX * 2 / cell) + 4), rows = checked((int)MathF.Ceiling(extentY * 2 / cell) + 4);
                frame.Count = checked(columns * rows);
                frame.Grid = new Vector4(left, bottom, columns, 0);
                frame.Shape = new Vector4(parallax, radius, MathF.Max(blur, worldPixel * .75f), config.Weight * fade);
                frame.Motion = new Vector4(wind / speed, -config.Fall / speed, halfLength, amount * config.Density);
                frame.Travel = new Vector4(drift, fall, cell, i + 1);
            }
            Mean = mean;
        }
        internal void Draw(bool foreground) {
            bool bound = false;
            for (int i = foreground ? 3 : 0, end = foreground ? layers.Length : 3; i < end; i++) {
                ref readonly PreparedLayer frame = ref prepared[i];
                if (frame.Count == 0) continue;
                if (!bound) {
                    if (topologyLost) { vertices.SetData(vertexData); indices.SetData(indexData); topologyLost = false; }
                    device.SetVertexBuffer(vertices); device.Indices = indices; device.ScissorRectangle = clipping; device.RasterizerState = rasterizer;
                    effect.CurrentTechnique = technique; bound = true;
                }
                shape.SetValue(frame.Shape); motion.SetValue(frame.Motion); layer.SetValue(frame.Travel);
                Vector4 layout = frame.Grid;
                for (int start = 0; start < frame.Count; start += BatchCells) {
                    int cells = Math.Min(BatchCells, frame.Count - start);
                    layout.W = start; grid.SetValue(layout);
                    technique.Passes[0].Apply();
                    device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, cells * 2);
                }
            }
        }
        private static float Phase(double value, double period) => (float)(value - Math.Floor(value / period) * period);
        private static float Smooth(float a, float b, float x) { float t = Math.Clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }
        private void Reset(object sender, EventArgs args) { topologyLost = true; }
        public void Dispose() { device.DeviceReset -= Reset; vertices?.Dispose(); indices?.Dispose(); rasterizer?.Dispose(); }
    }
}
