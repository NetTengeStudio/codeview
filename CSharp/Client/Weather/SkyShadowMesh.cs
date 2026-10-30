using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;

namespace ScpItdMod.Weather {
    internal sealed class SkyShadowMesh : IDisposable {
        private readonly GraphicsDevice device;
        private VertexBuffer buffer;
        private VertexPositionTexture[] staging = Array.Empty<VertexPositionTexture>();
        private int caps, count;
        internal float Reach { get; private set; }
        internal long GpuBytes => buffer == null ? 0 : (long)buffer.VertexCount * VertexPositionTexture.VertexDeclaration.VertexStride;
        internal SkyShadowMesh(GraphicsDevice device) { this.device = device; }
        internal void Upload(WeatherGeometry geometry, in WeatherRect bounds) {
            int needed = 0;
            float left = bounds.Left, right = bounds.Right, bottom = bounds.Bottom, top = bounds.Top;
            for (int i = 0; i < geometry.Count; i++) {
                ReadOnlySpan<Vec2> points = geometry.PolygonVertices(i, out _);
                if (points.IsEmpty) continue;
                needed = checked(needed + (points.Length - 2) * 3 + points.Length * 6);
                for (int j = 0; j < points.Length; j++) {
                    Vec2 p = points[j]; left = MathF.Min(left, p.X); right = MathF.Max(right, p.X);
                    bottom = MathF.Min(bottom, p.Y); top = MathF.Max(top, p.Y);
                }
            }
            double width = (double)right - left, height = (double)top - bottom;
            Reach = (float)Math.Sqrt(width * width + height * height);
            if (!float.IsFinite(Reach)) throw new InvalidOperationException("Shadow geometry exceeds the supported coordinate range");
            if (staging.Length < needed) staging = new VertexPositionTexture[Math.Max(needed, staging.Length * 2)];
            count = 0;
            for (int i = 0; i < geometry.Count; i++) {
                ReadOnlySpan<Vec2> points = geometry.PolygonVertices(i, out _);
                for (int j = 1; j < points.Length - 1; j++) {
                    Emit(points[0], 0, Vector2.Zero); Emit(points[j], 0, Vector2.Zero); Emit(points[j + 1], 0, Vector2.Zero);
                }
            }
            caps = count;
            for (int i = 0; i < geometry.Count; i++) {
                ReadOnlySpan<Vec2> points = geometry.PolygonVertices(i, out _);
                for (int j = 0; j < points.Length; j++) {
                    Vec2 a = points[j], b = points[(j + 1) % points.Length], delta = b - a;
                    Vector2 normal = new Vector2(delta.Y, -delta.X);
                    Emit(a, 0, normal); Emit(b, 0, normal); Emit(a, 1, normal);
                    Emit(a, 1, normal); Emit(b, 0, normal); Emit(b, 1, normal);
                }
            }
            if (count == 0) return;
            if (buffer == null || buffer.IsDisposed || buffer.VertexCount < count) {
                buffer?.Dispose(); buffer = new VertexBuffer(device, typeof(VertexPositionTexture), staging.Length, BufferUsage.WriteOnly);
            }
            buffer.SetData(staging, 0, count);
        }
        private void Emit(Vec2 p, float extruded, Vector2 attribute) => staging[count++] = new VertexPositionTexture(new Vector3(p.X, p.Y, extruded), attribute);
        internal void Draw(bool shadows) {
            int vertices = shadows ? count : caps;
            if (vertices == 0) return;
            device.SetVertexBuffer(buffer); device.DrawPrimitives(PrimitiveType.TriangleList, 0, vertices / 3);
        }
        public void Dispose() { buffer?.Dispose(); buffer = null; staging = Array.Empty<VertexPositionTexture>(); count = caps = 0; }
    }
}
