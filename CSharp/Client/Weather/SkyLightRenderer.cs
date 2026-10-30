using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;
using ScpItdMod.Rendering;

namespace ScpItdMod.Weather {
    public sealed class SkyLightRenderer : IDisposable {
        private readonly GraphicsDevice device;
        private SpriteBatch batch;
        private Effect effect;
        private SkyShadowMesh mesh;
        private SkyAmbientCache ambientCache;
        private BlendState sunWrite, moonWrite, directWrite;
        private RenderTarget2D mask, illumination;
        private readonly RenderTargetBinding[][] targetChecks = new RenderTargetBinding[9][];
        private WeatherGrid bound;
        private uint revision;
        private Matrix visibilityTransform;
        private bool visibilityReady, directReady;
        private float visibilityUnits;
        private Vector2 preparedSun, preparedMoon;
        private int viewportWidth, viewportHeight;
        private const int VisibilityGuard = 256, VisibilityScale = 2;
        public uint VisibilityBuilds { get; private set; }
        public uint GeometryUploads { get; private set; }
        public uint AmbientDraws { get; private set; }
        public uint DirectionalBuilds { get; private set; }
        public uint AmbientTileBuilds => ambientCache?.Builds ?? 0;
        public uint AmbientWorkRevision => ambientCache?.WorkRevision ?? 0;
        public long GpuBytes => Bytes(mask) + Bytes(illumination) + (mesh?.GpuBytes ?? 0) + (ambientCache?.GpuBytes ?? 0);
        private static long Bytes(RenderTarget2D target) => target == null ? 0 : (long)target.Width * target.Height * 4;
        private readonly EffectParameter matrix, inputTexture, shadowDirection, shadowReach, transfer, ambient, sun, moon, lightWindow;
        public SkyLightRenderer(GraphicsDevice device, byte[] shader) {
            this.device = device ?? throw new ArgumentNullException(nameof(device));
            if (shader == null) throw new ArgumentNullException(nameof(shader));
            try {
                batch = new SpriteBatch(device); effect = new Effect(device, shader); mesh = new SkyShadowMesh(device); ambientCache = new SkyAmbientCache(device);
                sunWrite = WriteChannels(ColorWriteChannels.Green); moonWrite = WriteChannels(ColorWriteChannels.Blue); directWrite = WriteChannels(ColorWriteChannels.Green | ColorWriteChannels.Blue);
                matrix = Required("MatrixTransform"); inputTexture = Required("InputTexture"); shadowDirection = Required("ShadowDirection");
                shadowReach = Required("ShadowReach"); transfer = Required("Transfer"); ambient = Required("AmbientColor");
                sun = Required("SunColor"); moon = Required("MoonColor"); lightWindow = Required("LightWindow");
            } catch { Dispose(); throw; }
            device.DeviceReset += Reset;
        }
        private static BlendState WriteChannels(ColorWriteChannels channels) => new BlendState {
            ColorSourceBlend = Blend.One, ColorDestinationBlend = Blend.Zero,
            AlphaSourceBlend = Blend.One, AlphaDestinationBlend = Blend.Zero, ColorWriteChannels = channels
        };
        private EffectParameter Required(string name) => effect.Parameters[name] ?? throw new InvalidOperationException("Sky-light shader parameter missing: " + name);
        private static Vector3 Colour(LightColor c) => new Vector3(c.R, c.G, c.B);
        private static Vector2 Source(LightColor colour, Vec2 direction) => colour.Luma > 0 && direction.Y > 0 ? new Vector2(direction.X, direction.Y) : Vector2.Zero;
        private void Validate(float units) {
            if (effect == null) throw new ObjectDisposedException(nameof(SkyLightRenderer));
            if (!float.IsFinite(units) || units <= 0) throw new ArgumentOutOfRangeException(nameof(units));
        }
        private static Matrix CacheTransform(Matrix camera, int width, int height, out int cacheWidth, out int cacheHeight) {
            int guardX = Math.Max(0, Math.Min(VisibilityGuard, (4096 - width) / 2));
            int guardY = Math.Max(0, Math.Min(VisibilityGuard, (4096 - height) / 2));
            cacheWidth = width + guardX * 2; cacheHeight = height + guardY * 2;
            if (guardX > 0) camera.M41 = MathF.Floor(camera.M41 / guardX) * guardX + guardX;
            if (guardY > 0) camera.M42 = MathF.Floor(camera.M42 / guardY) * guardY + guardY;
            return camera;
        }
        private void SafeTargets() {
            int count = device.RenderTargetCount;
            if (count >= targetChecks.Length) throw new InvalidOperationException("Too many render targets");
            RenderTargetBinding[] targets = targetChecks[count] ??= new RenderTargetBinding[count]; device.GetRenderTargets(targets);
            try {
                for (int i = 0; i < count; i++) if (targets[i].RenderTarget is RenderTarget2D target && target.RenderTargetUsage == RenderTargetUsage.DiscardContents)
                    throw new InvalidOperationException("Prepare sky visibility before drawing into a discard render target");
            } finally { Array.Clear(targets); }
        }
        private void Target(ref RenderTarget2D target, int width, int height) {
            if (target != null && !target.IsDisposed && target.Width == width && target.Height == height) return;
            target?.Dispose(); target = new RenderTarget2D(device, width, height, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
        }
        private void MeshMatrix(int width, int height) {
            Matrix scale = Matrix.CreateScale(width / (float)illumination.Width, height / (float)illumination.Height, 1);
            matrix.SetValue(Matrix.CreateScale(visibilityUnits, -visibilityUnits, 1) * visibilityTransform * scale * Matrix.CreateOrthographicOffCenter(0, width, height, 0, 0, -1));
        }
        private void Shadow(Vector2 direction) {
            device.Textures[0] = null; device.SetRenderTarget(mask); device.Clear(Color.White);
            MeshMatrix(mask.Width, mask.Height); shadowDirection.SetValue(direction); shadowReach.SetValue(mesh.Reach);
            device.BlendState = BlendState.Opaque; device.DepthStencilState = DepthStencilState.None; device.RasterizerState = RasterizerState.CullNone;
            effect.CurrentTechnique = effect.Techniques["Shadow"]; effect.CurrentTechnique.Passes[0].Apply(); mesh.Draw(true); DirectionalBuilds++;
        }
        private void Copy(Texture2D source, RenderTarget2D destination, Vector4 channel, BlendState blend) {
            device.Textures[0] = null; device.SetRenderTarget(destination);
            matrix.SetValue(Matrix.CreateOrthographicOffCenter(0, destination.Width, destination.Height, 0, 0, -1));
            inputTexture.SetValue(source); transfer.SetValue(channel); effect.CurrentTechnique = effect.Techniques["Transfer"];
            batch.Begin(SpriteSortMode.Immediate, blend, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone, effect);
            try { batch.Draw(source, new Rectangle(0, 0, destination.Width, destination.Height), Color.White); } finally { batch.End(); }
        }
        public void Prepare(WeatherGrid grid, Matrix transform, float units, int width, int height, in SkyLightEnvironment light) {
            Validate(units);
            if (grid == null) throw new ArgumentNullException(nameof(grid));
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            Matrix cached = CacheTransform(transform, width, height, out int cacheWidth, out int cacheHeight);
            bool geometryChanged = !ReferenceEquals(bound, grid) || revision != grid.LightGeometry.Revision;
            bool rebuild = geometryChanged || !visibilityReady || visibilityTransform != cached || visibilityUnits != units || viewportWidth != width || viewportHeight != height || illumination.IsContentLost;
            Vector2 solar = Source(light.Sunlight, light.SunDirection), lunar = Source(light.Moonlight, light.MoonDirection);
            ambientCache.Advance(grid, Matrix.CreateScale(units, -units, 1) * cached, cacheWidth, cacheHeight, transform);
            bool updateSun = rebuild || !directReady || preparedSun != solar, updateMoon = rebuild || !directReady || preparedMoon != lunar;
            if (!rebuild && !updateSun && !updateMoon) return;
            SafeTargets();
            using (GraphicsPass.Capture(device)) {
                directReady = false;
                if (geometryChanged) { visibilityReady = false; mesh.Upload(grid.LightGeometry, grid.Bounds); GeometryUploads++; bound = grid; revision = grid.LightGeometry.Revision; }
                Target(ref illumination, cacheWidth, cacheHeight);
                Target(ref mask, (cacheWidth + VisibilityScale - 1) / VisibilityScale, (cacheHeight + VisibilityScale - 1) / VisibilityScale);
                visibilityTransform = cached; visibilityUnits = units;
                if (rebuild) {
                    device.Textures[0] = null; device.SetRenderTarget(illumination); device.Clear(Color.Transparent);
                    MeshMatrix(cacheWidth, cacheHeight);
                    device.BlendState = BlendState.Opaque; device.DepthStencilState = DepthStencilState.None; device.RasterizerState = RasterizerState.CullNone;
                    effect.CurrentTechnique = effect.Techniques["Ambient"]; effect.CurrentTechnique.Passes[0].Apply();
                    ambientCache.Draw(grid); AmbientDraws++;
                    viewportWidth = width; viewportHeight = height; visibilityReady = true; VisibilityBuilds++;
                }
                if (updateSun) {
                    if (solar != Vector2.Zero) { Shadow(solar); Copy(mask, illumination, new Vector4(0, 1, 0, 0), sunWrite); }
                    else if (!rebuild) Copy(mask, illumination, Vector4.Zero, sunWrite);
                }
                if (updateMoon) {
                    if (lunar != Vector2.Zero) { Shadow(lunar); Copy(mask, illumination, new Vector4(0, 0, 1, 0), moonWrite); }
                    else if (!rebuild) Copy(mask, illumination, Vector4.Zero, moonWrite);
                }
                device.Textures[0] = null; device.SetRenderTarget(illumination); MeshMatrix(cacheWidth, cacheHeight);
                device.BlendState = rebuild ? BlendState.Opaque : updateSun && updateMoon ? directWrite : updateSun ? sunWrite : moonWrite;
                device.DepthStencilState = DepthStencilState.None; device.RasterizerState = RasterizerState.CullNone;
                effect.CurrentTechnique = effect.Techniques["Material"]; effect.CurrentTechnique.Passes[0].Apply(); mesh.Draw(false);
                preparedSun = solar; preparedMoon = lunar; directReady = true;
            }
        }
        public void Composite(WeatherGrid grid, in WeatherRect view, Matrix transform, in SkyLightEnvironment light, float units) {
            Validate(units);
            if (grid == null || !grid.Bounds.Overlaps(view) || light.Ambient.Luma + light.Sunlight.Luma + light.Moonlight.Luma <= 0) return;
            int width = device.Viewport.Width, height = device.Viewport.Height;
            Matrix cached = CacheTransform(transform, width, height, out _, out _);
            if (!ReferenceEquals(bound, grid) || revision != grid.LightGeometry.Revision || !visibilityReady || !directReady || visibilityTransform != cached || visibilityUnits != units || viewportWidth != width || viewportHeight != height || illumination.IsContentLost || preparedSun != Source(light.Sunlight, light.SunDirection) || preparedMoon != Source(light.Moonlight, light.MoonDirection))
                throw new InvalidOperationException("Sky visibility must be prepared for this geometry, camera and source directions before lightmap composition");
            BlendState blend = device.BlendState; DepthStencilState depth = device.DepthStencilState;
            RasterizerState rasterizer = device.RasterizerState; SamplerState sampler = device.SamplerStates[0]; Texture texture = device.Textures[0];
            try {
                matrix.SetValue(transform * Matrix.CreateOrthographicOffCenter(0, width, height, 0, 0, -1)); inputTexture.SetValue(illumination);
                lightWindow.SetValue(new Vector4(width / (float)illumination.Width, height / (float)illumination.Height, (visibilityTransform.M41 - transform.M41) / illumination.Width, (visibilityTransform.M42 - transform.M42) / illumination.Height));
                ambient.SetValue(Colour(light.Ambient)); sun.SetValue(preparedSun == Vector2.Zero ? Vector3.Zero : Colour(light.Sunlight)); moon.SetValue(preparedMoon == Vector2.Zero ? Vector3.Zero : Colour(light.Moonlight));
                effect.CurrentTechnique = effect.Techniques["Resolve"];
                batch.Begin(SpriteSortMode.Immediate, BlendState.Additive, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone, effect);
                try {
                    WeatherRect bounds = grid.Bounds;
                    batch.Draw(illumination, new Vector2(bounds.Left * units, -bounds.Top * units), null, Color.White, 0, Vector2.Zero,
                        new Vector2((bounds.Right - bounds.Left) * units / illumination.Width, (bounds.Top - bounds.Bottom) * units / illumination.Height), SpriteEffects.None, 0);
                } finally { batch.End(); }
            } finally { device.BlendState = blend; device.DepthStencilState = depth; device.RasterizerState = rasterizer; device.SamplerStates[0] = sampler; device.Textures[0] = texture; }
        }
        public void Draw(WeatherGrid grid, in WeatherRect view, Matrix transform, LightColor sky, float units) => Draw(grid, view, transform, SkyLightEnvironment.Diffuse(sky), units);
        public void Draw(WeatherGrid grid, in WeatherRect view, Matrix transform, in SkyLightEnvironment light, float units) {
            Validate(units);
            if (grid == null || !grid.Bounds.Overlaps(view) || light.Ambient.Luma + light.Sunlight.Luma + light.Moonlight.Luma <= 0) return;
            Prepare(grid, transform, units, device.Viewport.Width, device.Viewport.Height, light); Composite(grid, view, transform, light, units);
        }
        private void Reset(object sender, EventArgs args) { bound = null; visibilityReady = directReady = false; ambientCache.Invalidate(); }
        public void Dispose() {
            device.DeviceReset -= Reset;
            try { ambientCache?.Dispose(); }
            finally {
                ambientCache = null;
                batch?.Dispose(); batch = null; effect?.Dispose(); effect = null; mesh?.Dispose(); mesh = null; sunWrite?.Dispose(); sunWrite = null; moonWrite?.Dispose(); moonWrite = null; directWrite?.Dispose(); directWrite = null;
                mask?.Dispose(); mask = null; illumination?.Dispose(); illumination = null; bound = null;
            }
        }
    }
}
