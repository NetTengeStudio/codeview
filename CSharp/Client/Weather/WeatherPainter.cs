using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;
using ScpItdMod.Rendering;

namespace ScpItdMod.Weather {
    public sealed class WeatherPainter : IDisposable {
        private const int FullSkyWidth = 1920, FullSkyHeight = 1080;
        private static readonly Vector3 FogAlbedo = new Vector3(.86f, .9f, .96f);
        private readonly GraphicsDevice device;
        private Texture2D mask;
        private readonly Texture2D radial, white;
        private readonly RenderTarget2D sky, atmosphere;
        private Vector4 atmosphereState = new Vector4(float.NaN);
        public const double SkyRefreshSeconds = .1;
        private const float SkyKeyTolerance = 1e-3f;
        private readonly float[] skyKey = new float[21], skyDrawnKey = new float[21];
        private double skyDrawnAt = double.NaN;
        private Color[] maskPixels;
        private float[] maskExposure;
        private readonly WeatherExposure exposureSampler = new WeatherExposure();
        private readonly SpriteBatch screenBatch;
        private readonly Effect fog, skyShader;
        private readonly EffectParameter transform, fogColor, fogSettings, windSettings, cameraSettings, snowSettings, crystals, backdropMean, weatherPhase, weatherCycle, ribbonPhase, cameraBasis, maskBounds, fogTexture;
        private readonly EffectParameter skyTransform, sunMoon, rayleigh, mie, observer, conditions, motion, skyCrystals, emission, ambient, sunlight, skyView, ozone, skyHaze;
        private readonly float altitudeKm, latitude;
        private const int MaxMaskSide = 512, MaskAlign = 16;
        private readonly int baseMaskWidth;
        private int maskWidth, maskHeight;
        private readonly uint seed;
        private WeatherRect view, zone, camera;
        private float skylineY;
        private Vector2 sunDirection;
        private Vector3 skyTint = Vector3.One;
        private float distanceScale;
        private readonly SnowCoverRenderer cover;
        private readonly SnowBackdrop backdrop;
        private double previousTime, windTravel;
        private float previousWind;
        private readonly SnowParticle[] flakeBuffer = new SnowParticle[WeatherParticles.DrawBudget];
        public WeatherPainter(GraphicsDevice device, int maskWidth, int maskHeight, byte[] shader, byte[] skyBytes, byte[] snowBytes, uint seed, float altitudeMetres, float latitudeDegrees, float skyScale = 1) {
            altitudeKm = altitudeMetres / 1000; latitude = latitudeDegrees * (MathF.PI / 180);
            this.device = device; this.maskWidth = maskWidth; this.maskHeight = maskHeight; baseMaskWidth = maskWidth; this.seed = seed;
            try {
                maskPixels = new Color[maskWidth * maskHeight];
                maskExposure = new float[maskPixels.Length];
                sky = new RenderTarget2D(device, Math.Max(256, (int)(FullSkyWidth * skyScale)), Math.Max(144, (int)(FullSkyHeight * skyScale)), false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
                mask = new Texture2D(device, maskWidth, maskHeight);
                white = new Texture2D(device, 1, 1); white.SetData(new[] { Color.White });
                radial = new Texture2D(device, 64, 64); var data = new Color[4096];
                for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) {
                    float dx = (x - 31.5f) / 31.5f, dy = (y - 31.5f) / 31.5f;
                    float r2 = dx * dx + dy * dy;
                    data[y * 64 + x] = new Color(1f, 1f, 1f, MathF.Exp(-r2 * 5) * Math.Clamp((1 - r2) * 8, 0, 1));
                }
                radial.SetData(data); screenBatch = new SpriteBatch(device);
                fog = new Effect(device, shader);
                transform = Param(fog, "MatrixTransform"); fogColor = Param(fog, "FogColor"); fogSettings = Param(fog, "FogSettings"); windSettings = Param(fog, "WindSettings"); cameraSettings = Param(fog, "CameraSettings"); snowSettings = Param(fog, "SnowSettings"); crystals = Param(fog, "Crystals");
                cameraBasis = Param(fog, "CameraBasis"); maskBounds = Param(fog, "MaskBounds");
                backdropMean = Param(fog, "BackdropMean");
                fogTexture = Param(fog, "Texture");
                backdrop = new SnowBackdrop(device, fog);
                weatherPhase = Param(fog, "WeatherPhase"); weatherCycle = Param(fog, "WeatherCycle"); ribbonPhase = Param(fog, "RibbonPhase");
                fog.CurrentTechnique = fog.Techniques["Weather"] ?? throw new InvalidOperationException("Weather MGFX technique missing");
                skyShader = new Effect(device, skyBytes);
                skyTransform = Param(skyShader, "MatrixTransform"); sunMoon = Param(skyShader, "SunMoon"); rayleigh = Param(skyShader, "Rayleigh"); mie = Param(skyShader, "Mie"); observer = Param(skyShader, "Observer");
                conditions = Param(skyShader, "Conditions"); motion = Param(skyShader, "Motion"); skyCrystals = Param(skyShader, "Crystals"); emission = Param(skyShader, "Emission"); ambient = Param(skyShader, "Ambient"); sunlight = Param(skyShader, "Sunlight"); skyView = Param(skyShader, "View"); ozone = Param(skyShader, "Ozone"); skyHaze = Param(skyShader, "Haze");
                skyShader.CurrentTechnique = skyShader.Techniques["Sky"] ?? throw new InvalidOperationException("Sky MGFX technique missing");
                if (skyShader.Techniques["Atmosphere"] != null) {
                    atmosphere = new RenderTarget2D(device, 1024, 128, false, SurfaceFormat.HalfVector4, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
                    Param(skyShader, "AtmosphereSize").SetValue(new Vector2(atmosphere.Width, atmosphere.Height));
                }
                cover = new SnowCoverRenderer(device, snowBytes);
            } catch { Dispose(); throw; }
        }
        private static EffectParameter Param(Effect effect, string name) => effect.Parameters[name] ?? throw new InvalidOperationException("MGFX parameter missing: " + name);
        private static Vector4 Colour(Radiance c) => new Vector4(c.R, c.G, c.B, 1);
        public void Prepare(ScpItdMod.Weather.Material.SnowPack snow, in WeatherRect zoneBounds, in WeatherRect cameraView, in WeatherRect bounds, Vec2 observer, in WeatherState state, in SolarState sun, double seconds, double day, float dayOfYear, LightColor sceneLight, WeatherAirflow air, float skyline) {
            using var span = FrameProfiler.Section(ProfileSection.Weather);
            WeatherGrid grid = snow.Grid;
            view = bounds; zone = zoneBounds; camera = cameraView; skylineY = skyline; sunDirection = new Vector2(-sun.Sun.X, sun.Sun.Y);
            if (seconds >= previousTime) windTravel += (seconds - previousTime) * (previousWind + state.Wind.X) * .5;
            else windTravel = 0;
            previousTime = seconds; previousWind = state.Wind.X;
            RenderSky(state, sun, seconds, day, dayOfYear, sceneLight);
            float dx = MathF.Max(MathF.Abs(bounds.Left - observer.X), MathF.Abs(bounds.Right - observer.X)), dy = MathF.Max(MathF.Abs(bounds.Bottom - observer.Y), MathF.Abs(bounds.Top - observer.Y));
            distanceScale = MathF.Max(1, MathF.Sqrt(dx * dx + dy * dy));
            ResizeMask(bounds, grid.Cell);
            // The mask is rebuilt on every frame. A half-rate rebuild holds the previous frame's exposure on alternate
            // frames, which shows as a stepped drift of snow cover and drifting; the cost is measured by the Weather section.
            exposureSampler.Rasterize(grid, bounds, maskWidth, maskHeight, maskExposure);
            // The gust field is smooth, but sampling it calls the rotor-wash delegate; it is sampled on a coarse lattice and interpolated.
            int gustW = (maskWidth + GustStride - 1) / GustStride + 1, gustH = (maskHeight + GustStride - 1) / GustStride + 1;
            if (air != null) {
                if (gustField.Length < gustW * gustH) gustField = new float[gustW * gustH];
                for (int gy = 0; gy < gustH; gy++) for (int gx = 0; gx < gustW; gx++) {
                    float wx = WeatherNoise.Mix(bounds.Left, bounds.Right, (gx * GustStride + .5f) / maskWidth), wy = WeatherNoise.Mix(bounds.Top, bounds.Bottom, (gy * GustStride + .5f) / maskHeight);
                    gustField[gy * gustW + gx] = Math.Clamp(air.SpeedRatio(new Vec2(wx, wy)) * .5f, 0, 1);
                }
            }
            float wind = state.Wind.X, saltation = SnowPhysics.SaltationTop(wind), rouse = SnowPhysics.RouseNumber(wind);
            int steps = (int)((snow.MaximumDepth + 4) / grid.Cell) + 2;
            for (int y = 0; y < maskHeight; y++) {
                float wy = WeatherNoise.Mix(bounds.Top, bounds.Bottom, (y + .5f) / maskHeight);
                float gyAt = y / (float)GustStride; int gy0 = (int)gyAt; float gyFrac = gyAt - gy0;
                for (int x = 0; x < maskWidth; x++) {
                    float wx = WeatherNoise.Mix(bounds.Left, bounds.Right, (x + .5f) / maskWidth);
                    float exposure = maskExposure[y * maskWidth + x];
                    float distance = Vec2.Distance(new Vec2(wx, wy), observer) / distanceScale;
                    int highest = exposure > 0 ? grid.HighestSurface(grid.Index(wx, wy)) : -1;
                    int ground = highest >= 0 && wy <= snow.Ground(highest) + snow.MaximumDepth + 4 ? grid.Ground(wx, wy + grid.Cell, steps) : -1;
                    float height = ground >= 0 ? wy - snow.Ground(ground) - snow.Depth(ground) : 100;
                    float drifting = 0;
                    if (ground >= 0 && height >= 0) {
                        float airborne = snow.AirborneFraction(ground);
                        drifting = airborne == 0 ? 0 : airborne * (height <= 0 ? 1 : MathF.Pow(1 + height / saltation, -rouse));
                    }
                    float gust = .5f;
                    if (air != null) {
                        float gxAt = x / (float)GustStride; int gx0 = (int)gxAt; float gxFrac = gxAt - gx0;
                        int at = gy0 * gustW + gx0;
                        float top = gustField[at] + (gustField[at + 1] - gustField[at]) * gxFrac, bottom = gustField[at + gustW] + (gustField[at + gustW + 1] - gustField[at + gustW]) * gxFrac;
                        gust = top + (bottom - top) * gyFrac;
                    }
                    maskPixels[y * maskWidth + x] = new Color(exposure, distance, drifting, gust);
                }
            }
            mask.SetData(maskPixels);
        }
        private const int GustStride = 4;
        private float[] gustField = new float[0];
        private void ResizeMask(in WeatherRect bounds, float cell) {
            float width = bounds.Right - bounds.Left, height = bounds.Top - bounds.Bottom;
            float texel = Math.Clamp(width / baseMaskWidth, cell * .5f, cell);
            texel = MathF.Max(texel, MathF.Max(width, height) / MaxMaskSide);
            int w = Align((int)MathF.Ceiling(width / texel)), h = Align((int)MathF.Ceiling(height / texel));
            if (w == maskWidth && h == maskHeight) return;
            mask.Dispose();
            maskWidth = w; maskHeight = h;
            maskPixels = new Color[w * h];
            maskExposure = new float[maskPixels.Length];
            mask = new Texture2D(device, w, h);
        }
        private static int Align(int texels) => Math.Clamp((texels + MaskAlign - 1) / MaskAlign * MaskAlign, MaskAlign, MaxMaskSide);
        public const float BackdropMetres = 2000, DriftLayerMetres = 15, StarScale = 150, SiderealRate = 1.00273790935f;
        private void RenderSky(in WeatherState state, in SolarState sun, double seconds, double day, float dayOfYear, LightColor sceneLight) {
            float sunAzimuth = 0, moonAzimuth = MathF.IEEERemainder(sun.MoonAzimuth - sun.Azimuth, MathF.Tau);
            float haze = PolarAtmosphere.Mie(state.Humidity, state.Snowfall);
            float exposure = PolarAtmosphere.Exposure(sunAzimuth, sun.Altitude, altitudeKm, haze);
            Radiance up = PolarAtmosphere.Scatter(0, MathF.PI / 2 - .01f, sunAzimuth, sun.Altitude, altitudeKm, haze), light = PolarAtmosphere.Sunlight(sun.Altitude, altitudeKm, haze);
            float sunRightAscension = MathF.Tau * (dayOfYear - 80) / 365.25f;
            float sidereal = sunRightAscension + MathF.Tau * SiderealRate * (float)(day - Math.Floor(day) - .5);
            float activity = PolarSky.AuroraActivity(seed, seconds);
            using (GraphicsPass.Capture(device)) {
                skyTransform.SetValue(Matrix.CreateOrthographicOffCenter(0, 1, 1, 0, 0, 1));
                sunMoon.SetValue(new Vector4(sunAzimuth, sun.Altitude, moonAzimuth, sun.MoonAltitude));
                rayleigh.SetValue(new Vector4(PolarAtmosphere.RayleighR, PolarAtmosphere.RayleighG, PolarAtmosphere.RayleighB, PolarAtmosphere.RayleighHeight));
                mie.SetValue(new Vector4(haze, PolarAtmosphere.MieHeight, PolarAtmosphere.MieAsymmetry, PolarAtmosphere.SunIntensity));
                observer.SetValue(new Vector4(altitudeKm, exposure, latitude, sidereal));
                ozone.SetValue(new Vector4(PolarAtmosphere.OzoneR, PolarAtmosphere.OzoneG, PolarAtmosphere.OzoneB, 0));
                float height = camera.Top - camera.Bottom;
                float aspect = height / MathF.Max(1e-3f, camera.Right - camera.Left);
                float elevation = MathF.Atan2((camera.Top + camera.Bottom) * .5f - skylineY, BackdropMetres);
                skyView.SetValue(new Vector4(FieldOfView, aspect, .5f + elevation / (FieldOfView * aspect), 0));
                conditions.SetValue(new Vector4(state.Cloud, 1 - sun.Twilight, activity, state.Drifting));
                motion.SetValue(new Vector4((float)(seconds % 20000), state.Wind.X, (camera.Left + camera.Right) * .5f, sun.MoonPhase));
                skyCrystals.SetValue(new Vector4(PolarSky.DiamondDust(state), state.Snowfall, StarScale, sun.Azimuth));
                float drift = state.Drifting / MathF.Max(.05f, state.Drifting + state.Snowfall + state.Cloud * .5f);
                skyHaze.SetValue(new Vector4(state.Extinction * WeatherNoise.Mix(DriftLayerMetres, BackdropMetres, MathF.Pow(1 - Math.Clamp(drift, 0, 1), 3)), FogAlbedo.X * LightMapGain * sceneLight.R, FogAlbedo.Y * LightMapGain * sceneLight.G, FogAlbedo.Z * LightMapGain * sceneLight.B));
                ambient.SetValue(Colour(up)); sunlight.SetValue(Colour(light));
                Vector3 blue = new Vector3(up.R, up.G, up.B) / MathF.Max(1e-5f, MathF.Max(up.R, MathF.Max(up.G, up.B)));
                skyTint = Vector3.Lerp(Vector3.Lerp(Vector3.One, blue, .3f), new Vector3(.92f, .94f, .97f), Math.Clamp(state.Cloud, 0, 1));
                emission.SetValue(new Vector4(Unlit(sceneLight.R), Unlit(sceneLight.G), Unlit(sceneLight.B), 1));
                Vector4 scattering = new Vector4(sunAzimuth, sun.Altitude, altitudeKm, haze);
                if (atmosphere != null && (scattering != atmosphereState || atmosphere.IsContentLost)) {
                    device.Textures[0] = null; device.SetRenderTarget(atmosphere);
                    skyShader.CurrentTechnique = skyShader.Techniques["Atmosphere"];
                    screenBatch.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone, skyShader);
                    screenBatch.Draw(white, new Rectangle(0, 0, 1, 1), Color.White); screenBatch.End();
                    atmosphereState = scattering;
                }
                if (!SkyStale(state, sun, dayOfYear, sceneLight, activity, sidereal, seconds, elevation)) return;
                device.SetRenderTarget(sky); skyShader.CurrentTechnique = skyShader.Techniques["Sky"];
                screenBatch.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone, skyShader);
                screenBatch.Draw(atmosphere ?? white, new Rectangle(0, 0, 1, 1), Color.White);
                screenBatch.End();
            }
        }
        // The sky texture is redrawn at most every SkyRefreshSeconds, or sooner when a driving input moves past SkyKeyTolerance.
        private bool SkyStale(in WeatherState state, in SolarState sun, float dayOfYear, LightColor sceneLight, float activity, float sidereal, double seconds, float elevation) {
            float[] key = skyKey;
            key[0] = state.Cloud; key[1] = state.Drifting; key[2] = state.Snowfall; key[3] = state.Extinction; key[4] = state.Humidity; key[5] = state.Frost;
            key[6] = sun.Altitude; key[7] = sun.Azimuth; key[8] = sun.MoonAltitude; key[9] = sun.MoonAzimuth; key[10] = sun.MoonPhase; key[11] = sun.Twilight;
            key[12] = dayOfYear; key[13] = sceneLight.R; key[14] = sceneLight.G; key[15] = sceneLight.B; key[16] = activity; key[17] = sidereal;
            key[18] = camera.Right - camera.Left; key[19] = camera.Top - camera.Bottom; key[20] = elevation;
            bool stale = double.IsNaN(skyDrawnAt) || seconds < skyDrawnAt || seconds - skyDrawnAt >= SkyRefreshSeconds;
            for (int i = 0; i < key.Length && !stale; i++) stale = !(MathF.Abs(key[i] - skyDrawnKey[i]) <= SkyKeyTolerance);
            if (!stale) return false;
            Array.Copy(key, skyDrawnKey, key.Length); skyDrawnAt = seconds;
            return true;
        }
        public const float LightMapGain = 2, DarkestLightMap = .02f;
        private static float Unlit(float light) => 1 / (LightMapGain * MathF.Max(DarkestLightMap, light));
        public const float FieldOfView = 2.6f;
        public Texture2D Sky => sky;
        public void DrawSky(SpriteBatch batch, float units) =>
            batch.Draw(sky, new Vector2(camera.Left * units, -camera.Top * units), null, Color.White, 0, Vector2.Zero,
                new Vector2((camera.Right - camera.Left) * units / sky.Width, (camera.Top - camera.Bottom) * units / sky.Height), SpriteEffects.None, .99999f);
        public void DrawSnow(ScpItdMod.Weather.Material.SnowPack snow, in WeatherState state, Matrix world, float units, float pixelsPerMetre, float sceneLight = 1) => cover.Draw(snow, view, state, sunDirection, skyTint, world, units, pixelsPerMetre, sceneLight);
        public void DrawBlood(SpriteBatch batch, ReadOnlySpan<SnowBloodDrop> drops, float units, float pixelsPerMetre) {
            float diameter = MathF.Max(SnowBlood.Radius * 2, 1.2f / MathF.Max(1, pixelsPerMetre));
            for (int i = 0; i < drops.Length; i++) {
                ref readonly SnowBloodDrop drop = ref drops[i];
                Vec2 motion = drop.Position - drop.Previous;
                float length = MathF.Min(.12f, motion.Length());
                float angle = MathF.Atan2(-motion.Y, motion.X);
                batch.Draw(radial, new Vector2(drop.Position.X * units, -drop.Position.Y * units), null, new Color(.6f, .004f, .015f, .9f), angle,
                    new Vector2(32), new Vector2((diameter + length) * units / 64, diameter * units / 64), SpriteEffects.None, .001f);
            }
        }
        public void DrawFlakes(SpriteBatch batch, ScpItdMod.Weather.Material.SnowPack snow, WeatherParticles pool, int active, in WeatherState state, double seconds, float units, float pixelsPerMetre) {
            float pixel = 1 / MathF.Max(pixelsPerMetre, 1);
            int flakes = pool.Collect(state, view, active, flakeBuffer);
            for (int i = 0; i < flakes; i++) {
                ref readonly SnowParticle p = ref flakeBuffer[i];
                Vec2 delta = p.Position - p.Previous;
                SnowFlake.Footprint(p.Size, pixel, p.Layer, p.Alpha, out float size, out float alpha);
                float length = SnowFlake.Streak(delta.Length(), size);
                float angle = MathF.Atan2(-delta.Y, delta.X);
                batch.Draw(radial, new Vector2(p.Position.X * units, -p.Position.Y * units), null, new Color(1f, 1f, 1f, Math.Clamp(alpha * 1.6f, 0, 1)), angle, new Vector2(32), new Vector2((length + size) * SnowFlake.PixelCoverage * units / 64, SnowFlake.ThicknessMetres(size) * units / 64), SpriteEffects.None, .001f);
            }
        }
        private static float Phase(double value, double period) => (float)(value - Math.Floor(value / period) * period);
        public void DrawAtmosphere(int width, int height, Rectangle screen, in WeatherState state, double seconds, float pixelsPerMetre) =>
            Atmosphere(width, height, screen, state, seconds, pixelsPerMetre, new Vector4(view.Right - view.Left, 0, 0, view.Bottom - view.Top), new Vector2((view.Left + view.Right) * .5f, (view.Bottom + view.Top) * .5f));
        public void DrawAtmosphere(int width, int height, Rectangle screen, in WeatherState state, double seconds, float pixelsPerMetre, in Matrix world, float units) {
            if (!float.IsFinite(units) || units <= 0) throw new ArgumentOutOfRangeException(nameof(units));
            Matrix inverse = Matrix.Invert(world);
            Vector2 centre = Vector2.Transform(new Vector2(screen.X + screen.Width * .5f, screen.Y + screen.Height * .5f), inverse) / units;
            Vector2 horizontal = Vector2.TransformNormal(new Vector2(screen.Width, 0), inverse) / units, vertical = Vector2.TransformNormal(new Vector2(0, screen.Height), inverse) / units;
            Atmosphere(width, height, screen, state, seconds, pixelsPerMetre, new Vector4(horizontal.X, -horizontal.Y, vertical.X, -vertical.Y), new Vector2(centre.X, -centre.Y));
        }
        private void Atmosphere(int width, int height, Rectangle screen, in WeatherState state, double seconds, float pixelsPerMetre, Vector4 basis, Vector2 centre) {
            using (GraphicsPass.Capture(device)) {
                transform.SetValue(Matrix.CreateOrthographicOffCenter(0, width, height, 0, 0, 1));
                fogColor.SetValue(new Vector4(FogAlbedo, 1));
                fogSettings.SetValue(new Vector4(state.Extinction * distanceScale, 0, 0, state.Frost));
                windSettings.SetValue(new Vector4(state.Wind.X, state.Wind.Y, 0, 1 / MathF.Max(1, pixelsPerMetre)));
                cameraSettings.SetValue(new Vector4(centre.X, centre.Y, view.Right - view.Left, view.Top - view.Bottom));
                cameraBasis.SetValue(basis); maskBounds.SetValue(new Vector4(view.Left, view.Bottom, view.Right - view.Left, view.Top - view.Bottom));
                snowSettings.SetValue(new Vector4(state.Snowfall, state.Drifting, 1, 0));
                backdrop.Prepare(basis, centre, screen, width, height, state.Wind.X, state.Snowfall, seconds, windTravel);
                backdropMean.SetValue(backdrop.Mean);
                weatherPhase.SetValue(new Vector4(Phase(windTravel * .45, 1024), Phase(windTravel * .07, 1024), Phase(windTravel * 9, 1024), Phase(seconds * .03, 1024)));
                weatherCycle.SetValue(new Vector2(Phase(seconds, 200) * (MathF.Tau / 200), Phase(seconds * 1.2, 1024)));
                ribbonPhase.SetValue(new Vector2(Phase(windTravel * .55, 1024), Phase(windTravel * 1.265, 1024)));
                crystals.SetValue(new Vector4(PolarSky.DiamondDust(state), sunDirection.X, sunDirection.Y, Math.Clamp(sunDirection.Y * 4 + .5f, 0, 1)));
                device.BlendState = BlendState.NonPremultiplied; device.DepthStencilState = DepthStencilState.None; device.RasterizerState = RasterizerState.CullNone;
                device.Textures[0] = mask; device.SamplerStates[0] = SamplerState.LinearClamp;
                fogTexture.SetValue(mask);
                backdrop.Draw(false);
                fog.CurrentTechnique = fog.Techniques["Weather"];
                screenBatch.Begin(SpriteSortMode.Immediate, BlendState.NonPremultiplied, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone, fog);
                screenBatch.Draw(mask, screen, Color.White);
                screenBatch.End();
                device.Textures[0] = mask;
                backdrop.Draw(true);
            }
        }
        public void Dispose() { backdrop?.Dispose(); cover?.Dispose(); screenBatch?.Dispose(); sky?.Dispose(); atmosphere?.Dispose(); mask?.Dispose(); radial?.Dispose(); white?.Dispose(); fog?.Dispose(); skyShader?.Dispose(); }
    }
}
