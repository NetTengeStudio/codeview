using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Motion;

namespace ScpItdMod.Rendering {
    public sealed class SphereBatch : IDisposable {
        public const int Tile = 128, Side = Tile * 4;
        public const float ProjectionExtent = 2.15f;
        private readonly GraphicsDevice device;
        private readonly Texture2D[] materials;
        private readonly VertexPositionNormalTexture[] vertices;
        private readonly short[] indices;
        private readonly BasicEffect effect;
        public long DrawCalls { get; private set; }
        public long TargetSwitches { get; private set; }
        public SphereBatch(GraphicsDevice device, Texture2D[] materials) {
            this.device = device ?? throw new ArgumentNullException(nameof(device));
            if (materials == null || materials.Length != 16) { throw new ArgumentException("Sixteen sphere materials are required.", nameof(materials)); }
            this.materials = (Texture2D[])materials.Clone();
            var mesh = new SphereMesh(); vertices = new VertexPositionNormalTexture[mesh.Points.Length]; indices = mesh.Indices;
            for (int i = 0; i < vertices.Length; i++) {
                SpherePoint point = mesh.Points[i]; var normal = new Vector3(point.Normal.X, point.Normal.Y, point.Normal.Z);
                vertices[i] = new VertexPositionNormalTexture(normal, normal, new Vector2(point.Uv.X, point.Uv.Y));
            }
            effect = new BasicEffect(device) { TextureEnabled = true, LightingEnabled = true, PreferPerPixelLighting = true,
                AmbientLightColor = new Vector3(0.28f), DiffuseColor = new Vector3(0.82f), SpecularColor = new Vector3(0.55f), SpecularPower = 48,
                View = Matrix.CreateLookAt(new Vector3(0, 0, 4), Vector3.Zero, Vector3.Up), Projection = Matrix.CreateOrthographic(ProjectionExtent, ProjectionExtent, 1, 10) };
            effect.DirectionalLight0.Enabled = true; effect.DirectionalLight0.Direction = Vector3.Normalize(new Vector3(0.45f, -0.65f, -1));
            effect.DirectionalLight0.DiffuseColor = Vector3.One; effect.DirectionalLight0.SpecularColor = Vector3.One;
        }
        public void Draw(RenderTarget2D target, ReadOnlySpan<ScpItdMod.Motion.Rotation3> orientation, ushort visible) {
            if (orientation.Length != 16 || target.Width != Side || target.Height != Side || !ReferenceEquals(target.GraphicsDevice, device)) { throw new ArgumentException("Sphere atlas geometry or device does not match."); }
            using var pass = GraphicsPass.Capture(device);
            device.SetRenderTarget(target); device.Clear(Color.Transparent); TargetSwitches += 2;
            device.BlendState = BlendState.Opaque; device.DepthStencilState = DepthStencilState.None;
            device.RasterizerState = RasterizerState.CullClockwise; device.SamplerStates[0] = SamplerState.LinearWrap;
            for (int i = 0; i < 16; i++) {
                if ((visible & 1 << i) == 0 || materials[i] == null || materials[i].IsDisposed) { continue; }
                var tile = new Rectangle(i % 4 * Tile, i / 4 * Tile, Tile, Tile); device.Viewport = new Viewport(tile); device.ScissorRectangle = tile;
                ScpItdMod.Motion.Rotation3 q = orientation[i];
                effect.World = Matrix.CreateFromQuaternion(new Quaternion(q.X, q.Y, q.Z, q.W)); effect.Texture = materials[i];
                effect.CurrentTechnique.Passes[0].Apply();
                device.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, vertices, 0, vertices.Length, indices, 0, indices.Length / 3); DrawCalls++;
            }
        }
        public void Dispose() { effect.Dispose(); Array.Clear(materials); }
    }
}
