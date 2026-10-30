using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;
using ScpItdMod.Motion;

namespace ScpItdMod.Rendering {
    public readonly struct MotionBlurShadow {
        public readonly Vec2 Offset;
        public readonly float Opacity;
        public MotionBlurShadow(Vec2 offset, float opacity) { Offset = offset; Opacity = Math.Clamp(opacity, 0, 1); }
    }
    public sealed class MotionBlurRenderer : IDisposable {
        private readonly GraphicsDevice device;
        private readonly Effect effect;
        private readonly BlendState premultiplied;
        private readonly EffectParameter image, source, textureSize, tint, count, sampleX, sampleY, shadowParameter;
        private readonly Vector4[] rowsX = new Vector4[MotionShutter.MaxSamples], rowsY = new Vector4[MotionShutter.MaxSamples];
        private readonly VertexPositionTexture[] vertices = new VertexPositionTexture[4];
        private static readonly short[] indices = { 0, 1, 2, 0, 2, 3 };
        private bool disposed, drawing;
        public GraphicsDevice Device => device;
        public long DrawCalls { get; private set; }
        public long SamplePixels { get; private set; }
        public int LastSamples { get; private set; }
        public float LastTravel { get; private set; }
        public MotionBlurRenderer(GraphicsDevice device, byte[] shader) {
            this.device = device ?? throw new ArgumentNullException(nameof(device));
            effect = new Effect(device, shader ?? throw new ArgumentNullException(nameof(shader)));
            try {
                image = Parameter("xImage"); source = Parameter("xSource"); textureSize = Parameter("xTextureSize"); tint = Parameter("xTint"); count = Parameter("xCount"); sampleX = Parameter("xSampleX"); sampleY = Parameter("xSampleY");
                shadowParameter = Parameter("xShadow");
                effect.CurrentTechnique = effect.Techniques["MotionExposure"] ?? throw new InvalidDataException("MotionExposure technique is absent.");
                if (sampleX.Elements.Count != MotionShutter.MaxSamples || sampleY.Elements.Count != MotionShutter.MaxSamples) { throw new InvalidDataException("Motion shader sample budget differs from MotionShutter."); }
                premultiplied = new BlendState { ColorSourceBlend = Blend.One, ColorDestinationBlend = Blend.InverseSourceAlpha, AlphaSourceBlend = Blend.One, AlphaDestinationBlend = Blend.InverseSourceAlpha };
            } catch { effect.Dispose(); premultiplied?.Dispose(); throw; }
        }
        private EffectParameter Parameter(string name) => effect.Parameters[name] ?? throw new InvalidDataException("Motion shader parameter absent: " + name);
        private static float Linear(byte value) { float f = value / 255f; return f <= .04045f ? f / 12.92f : MathF.Pow((f + .055f) / 1.055f, 2.4f); }
        public bool Draw<T>(Texture2D texture, Rectangle region, Color color, in T path, in MotionShutter shutter,
            Vec2 origin, SpriteBatch batch = null, Matrix? transform = null, MotionBlurShadow shadow = default, Action flush = null) where T : IMotionPath2D {
            if (disposed) { throw new ObjectDisposedException(nameof(MotionBlurRenderer)); }
            if (drawing) { throw new InvalidOperationException("Motion blur renderer is not reentrant."); }
            if (!SpriteBatchPause.Supported(batch) || batch?.GetCurrentEffect() != null) { return false; }
            Matrix matrix = transform ?? SpriteBatchPause.MatrixOf(batch);
            if (matrix.M13 != 0 || matrix.M14 != 0 || matrix.M23 != 0 || matrix.M24 != 0 || matrix.M43 != 0 || matrix.M44 != 1) { return false; }
            var view = new Affine2(matrix.M11, matrix.M12, matrix.M21, matrix.M22, matrix.M41, matrix.M42);
            Span<MotionExposureTap> taps = stackalloc MotionExposureTap[MotionShutter.MaxSamples];
            MotionFootprint footprint = MotionExposure.Build(path, shutter, origin, view, taps);
            LastSamples = footprint.Samples; LastTravel = footprint.Travel;
            if (footprint.Samples == 0) { return false; }
            if (texture == null || texture.IsDisposed || !ReferenceEquals(texture.GraphicsDevice, device) || region.Width < 1 || region.Height < 1 || region.Left < 0 || region.Top < 0 || region.Right > texture.Width || region.Bottom > texture.Height) { throw new ArgumentException("Motion source texture or atlas region is invalid.", nameof(texture)); }
            drawing = true;
            try {
                flush?.Invoke();
                using var pause = new SpriteBatchPause(batch);
                using var state = GraphicsPass.Capture(device);
                for (int i = 0; i < footprint.Samples; i++) {
                    MotionExposureTap tap = taps[i]; rowsX[i] = new Vector4(tap.X.X, tap.X.Y, tap.X.Z, tap.X.W); rowsY[i] = new Vector4(tap.Y.X, tap.Y.Y, tap.Y.Z, tap.Y.W);
                }
                Viewport viewport = device.Viewport;
                float xScale = 2f / viewport.Width, yScale = -2f / viewport.Height;
                float left = footprint.Left + MathF.Min(0, shadow.Offset.X), top = footprint.Top + MathF.Min(0, shadow.Offset.Y);
                float right = footprint.Right + MathF.Max(0, shadow.Offset.X), bottom = footprint.Bottom + MathF.Max(0, shadow.Offset.Y);
                vertices[0] = Vertex(left, top, xScale, yScale); vertices[1] = Vertex(right, top, xScale, yScale);
                vertices[2] = Vertex(right, bottom, xScale, yScale); vertices[3] = Vertex(left, bottom, xScale, yScale);
                image.SetValue(texture); textureSize.SetValue(new Vector2(texture.Width, texture.Height));
                source.SetValue(new Vector4((float)region.Left / texture.Width, (float)region.Top / texture.Height, (float)region.Right / texture.Width, (float)region.Bottom / texture.Height));
                tint.SetValue(new Vector4(Linear(color.R), Linear(color.G), Linear(color.B), color.A / 255f));
                shadowParameter.SetValue(new Vector4(shadow.Offset.X, shadow.Offset.Y, shadow.Opacity, 0));
                count.SetValue(footprint.Samples); sampleX.SetValue(rowsX); sampleY.SetValue(rowsY);
                device.BlendState = premultiplied; device.DepthStencilState = DepthStencilState.None; device.SamplerStates[0] = SamplerState.PointClamp;
                effect.CurrentTechnique.Passes[0].Apply();
                device.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, vertices, 0, 4, indices, 0, 2);
                DrawCalls++; SamplePixels += (long)Math.Ceiling((footprint.Right - footprint.Left) * (footprint.Bottom - footprint.Top)) * footprint.Samples;
                return true;
            } finally { drawing = false; }
        }
        private static VertexPositionTexture Vertex(float x, float y, float xScale, float yScale) =>
            new VertexPositionTexture(new Vector3(x * xScale - 1, y * yScale + 1, 0), new Vector2(x, y));
        public void Dispose() { if (disposed) { return; } if (drawing) { throw new InvalidOperationException("Cannot dispose an active motion pass."); } disposed = true; effect.Dispose(); premultiplied.Dispose(); }
    }

    public static class MotionBlur {
        private static MotionBlurRenderer renderer;
        private static bool unavailable;
        public static bool Enabled { get; set; } = true;
        public static MotionShutter Shutter { get; set; } = MotionShutter.Default;
        public static MotionBlurRenderer Renderer => renderer;
        public static void Initialize(GraphicsDevice device, byte[] shader) {
            var next = new MotionBlurRenderer(device, shader); MotionBlurRenderer previous = renderer; renderer = next; unavailable = false; previous?.Dispose();
        }
        private static bool Ready(GraphicsDevice device) {
            if (unavailable) { return false; }
            if (renderer != null && ReferenceEquals(renderer.Device, device) && !device.IsDisposed) { return true; }
            string backend = OperatingSystem.IsWindows() ? "dx11" : "opengl";
            string path = ModAssets.Resolve("Shaders/motionblur_" + backend + ".mgfxo");
            try { Initialize(device, File.ReadAllBytes(path)); return true; }
            catch (Exception error) when (error is IOException || error is InvalidOperationException || error is ArgumentException || error is NotSupportedException) {
                unavailable = true; ModLog.Error("Motion blur shader unavailable: " + path + ": " + error.Message); return false;
            }
        }
        public static bool Draw<T>(SpriteBatch batch, Texture2D texture, Rectangle region, Color tint, in T path, Vec2 origin, MotionBlurShadow shadow = default) where T : IMotionPath2D =>
            Enabled && Ready(batch.GraphicsDevice) && renderer.Draw(texture, region, tint, path, Shutter, origin, batch, shadow: shadow);
        // Explicit exposure for a surface whose blur should differ from the shared shutter (for example item descriptions).
        public static bool Draw<T>(SpriteBatch batch, Texture2D texture, Rectangle region, Color tint, in T path, Vec2 origin, MotionShutter shutter, MotionBlurShadow shadow = default) where T : IMotionPath2D =>
            Enabled && Ready(batch.GraphicsDevice) && renderer.Draw(texture, region, tint, path, shutter, origin, batch, shadow: shadow);
        public static bool Draw<T>(GraphicsDevice device, Texture2D texture, Rectangle region, Color tint, in T path, Vec2 origin, MotionBlurShadow shadow = default, Action flush = null) where T : IMotionPath2D =>
            Enabled && Ready(device) && renderer.Draw(texture, region, tint, path, Shutter, origin, shadow: shadow, flush: flush);
        public static void Release() { renderer?.Dispose(); renderer = null; unavailable = false; }
    }
}
