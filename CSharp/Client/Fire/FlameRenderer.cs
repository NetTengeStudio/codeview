using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Rendering;

namespace ScpItdMod.Fire {
    // Everything one barrel hands the GPU for one frame. The flame, its sparks and its smoke are functions of this and of time alone.
    public struct FlameFrame {
        public Vector2 Mouth;          // display units (world x, minus world y): the centre of the barrel's open top
        public float Radius, Length;   // metres: mouth radius and mean flame length
        public float Clock, PuffHertz, Vigour, MetresPerPixel, Seed;
        public Vector2 Wind;           // m/s
        public float EmberChance, SmokeOpacity, EmberBrightness;
        public int EmberSlots, SmokeSlots;
    }

    // Draws the barrel fire (Shaders/fire.fx). The vertex buffers are built once and never written again: every particle is a slot that
    // the vertex shader evaluates in closed form, so the CPU cost of a burning barrel is a few uniforms and three draw calls.
    internal sealed class FlameRenderer : IDisposable {
        internal const int MaxEmbers = 96, MaxSmoke = 24;
        private struct Vertex : IVertexType {
            private static readonly VertexDeclaration declaration = new VertexDeclaration(new VertexElement(0, VertexElementFormat.Vector4, VertexElementUsage.Position, 0));
            public Vector4 Position;
            VertexDeclaration IVertexType.VertexDeclaration => declaration;
        }
        private static readonly BlendState Glow = new BlendState { ColorSourceBlend = Blend.One, ColorDestinationBlend = Blend.One, AlphaSourceBlend = Blend.Zero, AlphaDestinationBlend = Blend.One };
        private readonly GraphicsDevice device;
        private readonly Effect effect;
        private readonly EffectParameter transform, rig, beat, air, soot, heat;
        private readonly VertexBuffer flame, embers, smoke;
        private readonly IndexBuffer indices;

        internal FlameRenderer(GraphicsDevice device, byte[] shader) {
            this.device = device;
            effect = new Effect(device, shader);
            try {
                transform = Param("MatrixTransform"); rig = Param("Rig"); beat = Param("Beat"); air = Param("Air"); soot = Param("Soot"); heat = Param("Heat");
                flame = Slots(1); embers = Slots(MaxEmbers); smoke = Slots(MaxSmoke);
                var order = new short[MaxEmbers * 6];
                for (int i = 0; i < MaxEmbers; i++) { int v = i * 4, o = i * 6; order[o] = (short)v; order[o + 1] = (short)(v + 1); order[o + 2] = (short)(v + 2); order[o + 3] = (short)(v + 2); order[o + 4] = (short)(v + 1); order[o + 5] = (short)(v + 3); }
                indices = new IndexBuffer(device, IndexElementSize.SixteenBits, order.Length, BufferUsage.WriteOnly); indices.SetData(order);
            } catch { Dispose(); throw; }
        }
        private EffectParameter Param(string name) => effect.Parameters[name] ?? throw new InvalidOperationException("MGFX parameter missing: " + name);
        // A slot is a quad whose four corners carry (corner x, corner y, slot id, 0); the vertex shader does the rest.
        private VertexBuffer Slots(int count) {
            var vertices = new Vertex[count * 4];
            for (int i = 0; i < count; i++)
                for (int k = 0; k < 4; k++) vertices[i * 4 + k].Position = new Vector4((k & 1) * 2 - 1, (k >> 1) * 2 - 1, i, 0);
            var buffer = new VertexBuffer(device, typeof(Vertex), vertices.Length, BufferUsage.WriteOnly); buffer.SetData(vertices);
            return buffer;
        }

        // Smoke first, in the scene's own pass (the light map then tints it); the glow after the light map, so it stays emissive.
        internal void DrawSmoke(in FlameFrame f, Matrix view) {
            if (f.SmokeSlots <= 0 || f.SmokeOpacity <= 0) return;
            using var pass = GraphicsPass.Capture(device);
            Prepare(f, view, "Smoke", BlendState.NonPremultiplied);
            device.SetVertexBuffer(smoke); device.Indices = indices;
            device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, Math.Min(f.SmokeSlots, MaxSmoke) * 2);
        }
        internal void DrawGlow(in FlameFrame f, Matrix view) {
            using var pass = GraphicsPass.Capture(device);
            Prepare(f, view, "Flame", Glow);
            device.SetVertexBuffer(flame); device.Indices = indices;
            device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, 2);
            int slots = Math.Min(f.EmberSlots, MaxEmbers);
            if (slots <= 0) return;
            effect.CurrentTechnique = effect.Techniques["Sparks"]; effect.CurrentTechnique.Passes[0].Apply();
            device.SetVertexBuffer(embers);
            device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, slots * 2);
        }
        private void Prepare(in FlameFrame f, Matrix view, string technique, BlendState blend) {
            transform.SetValue(view * Matrix.CreateOrthographicOffCenter(0, device.Viewport.Width, device.Viewport.Height, 0, 0, 1));
            rig.SetValue(new Vector4(f.Mouth, f.Radius, f.Length));
            beat.SetValue(new Vector4(f.Clock, f.PuffHertz, f.Vigour, f.MetresPerPixel));
            air.SetValue(new Vector4(f.Wind, f.Seed, f.EmberChance));
            soot.SetValue(new Vector4(f.SmokeOpacity, f.SmokeSlots, f.EmberBrightness, 0));
            heat.SetValue(new Vector4(FlameModel.FlameTipKelvin, FlameModel.FlameBaseKelvin, FlameModel.EmberCoolKelvin, FlameModel.LeanPerWind));
            device.BlendState = blend; device.DepthStencilState = DepthStencilState.None; device.RasterizerState = RasterizerState.CullNone;
            effect.CurrentTechnique = effect.Techniques[technique]; effect.CurrentTechnique.Passes[0].Apply();
        }

        public void Dispose() { flame?.Dispose(); embers?.Dispose(); smoke?.Dispose(); indices?.Dispose(); effect?.Dispose(); }
    }
}
