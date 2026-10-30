using System.Reflection;
using System;
using System.IO;
using Barotrauma;
using Barotrauma.Lights;
using Barotrauma.Particles;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;

namespace ScpItdMod.Weather {
    internal static class WeatherClient {
        private static WeatherPainter painter;
        private static WeatherParticles particles;
        private static WeatherAirflow airflow;
        private static readonly SnowBloodParticles blood = new SnowBloodParticles();
        private const float SnowFollowSeconds = .12f;
        private static float skyline = float.NaN;
        private static WeatherRect view;
        private static int backCalls, active;
        private static bool covered, drawing, immersed;
        private static float quality = 1;
        internal static float Quality {
            get => quality;
            set { quality = Math.Clamp(value, .1f, 1); Prefs.Set("weather.quality", quality.ToString(System.Globalization.CultureInfo.InvariantCulture)); painter?.Dispose(); painter = null; }
        }
        internal static void Attach(Harmony harmony) {
            harmony.Patch(AccessTools.Method(typeof(GameScreen), nameof(GameScreen.DrawMap)), prefix: ModuleHost.Bind(typeof(WeatherClient), nameof(Begin)), postfix: ModuleHost.Bind(typeof(WeatherClient), nameof(End)), finalizer: ModuleHost.Bind(typeof(WeatherClient), nameof(FinalizeDraw)));
            harmony.Patch(AccessTools.Method(typeof(LightManager), nameof(LightManager.RenderLightMap)), prefix: ModuleHost.Bind(typeof(WeatherLight), nameof(WeatherLight.BeforeLightMap)), transpiler: ModuleHost.Bind(typeof(WeatherLight), nameof(WeatherLight.InsertComposite)));
            foreach (MethodInfo halo in AccessTools.GetDeclaredMethods(typeof(LightManager))) {
                if (halo.Name.Contains("g__DrawHalo|")) { harmony.Patch(halo, prefix: ModuleHost.Bind(typeof(WeatherLight), nameof(WeatherLight.Halo))); }
            }
            harmony.Patch(AccessTools.Method(typeof(Submarine), nameof(Submarine.DrawBack)), prefix: ModuleHost.Bind(typeof(WeatherClient), nameof(Background)));
            harmony.Patch(AccessTools.Method(typeof(Submarine), nameof(Submarine.DrawFront)), postfix: ModuleHost.Bind(typeof(WeatherClient), nameof(Cover)));
        }
        internal static Vec2 WindAt(Vector2 world) => airflow != null && WeatherService.Active && WeatherService.InZone(world) ? airflow.Sample(new Vec2(world.X / 100, world.Y / 100)) : default;
        private static uint snowBurstSeed;
        internal static void BloodDrop(Vec2 point, Vec2 velocity, double born) { if (SnowRuntime.Pack != null) blood.Spawn(SnowRuntime.Pack, point, velocity, born, WeatherService.Clock.Seconds); }
        internal static void SnowBurst(Vec2 point, Vec2 velocity, float kilograms) {
            if (GameMain.ParticleManager == null) return;
            if (!ParticlePrefab.Prefabs.TryGet("scpitd_snow_clump".ToIdentifier(), out ParticlePrefab prefab)) throw new InvalidOperationException("Snow clump particle is missing");
            int count = Math.Clamp((int)(kilograms * 2 + 4), 4, 24);
            for (int i = 0; i < count; i++) {
                uint hash = unchecked(++snowBurstSeed * 747796405u);
                float angle = WeatherNoise.Unit(hash) * MathF.Tau, speed = WeatherNoise.Unit(hash ^ 0x98573abfu);
                Vector2 spread = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                GameMain.ParticleManager.CreateParticle(prefab, new Vector2(point.X, point.Y) * WeatherService.UnitsPerMetre + spread * (speed * 8),
                    (new Vector2(velocity.X, velocity.Y) + spread * (speed * .8f)) * WeatherService.UnitsPerMetre);
            }
        }
        internal static void Start() {
            skyline = float.NaN; quality = Prefs.Number("weather.quality", 1, .1f, 1);
            particles = new WeatherParticles(WeatherService.Config.Particles, WeatherService.Director.Seed);
            airflow = new WeatherAirflow { ExternalWind = WeatherBov.RotorWind, ExternalWindConcurrent = WeatherBov.RotorWindConcurrent };
            WeatherAudio.Start();
        }
        internal static void Step(float dt) {
            WeatherAudio.Step(dt);
            if (SnowRuntime.Pack != null) blood.Step(SnowRuntime.Pack, WeatherService.Clock.Seconds);
        }
        internal static float PrepareMilliseconds { get; private set; }
        internal static float DrawMilliseconds { get; private set; }
        private static float Smooth(float current, long started) => current + ((System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000f / System.Diagnostics.Stopwatch.Frequency - current) * .05f;
        private static void Begin(GraphicsDevice graphics, double deltaTime) {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            try { Prepare(graphics, deltaTime); }
            finally { PrepareMilliseconds = Smooth(PrepareMilliseconds, started); }
        }
        private static void Prepare(GraphicsDevice graphics, double deltaTime) {
            WeatherLight.EndFrame();
            if (!WeatherService.Active || WeatherService.Grid == null || !WeatherService.Authority && !WeatherNetwork.Ready) { WeatherLight.Suspend(); return; }
            Camera camera = GameMain.GameScreen.Cam;
            WeatherRect zone = WeatherService.Grid.Bounds, seen = Covering(camera);
            if (!zone.Overlaps(seen)) { WeatherLight.Suspend(); return; }
            view = zone.Intersect(seen);
            if (painter == null) {
                int width = Math.Max(64, (int)(WeatherService.Config.MaskWidth * quality));
                string profile = OperatingSystem.IsWindows() ? "dx11" : "opengl";
                painter = new WeatherPainter(graphics, width, Math.Max(32, width * graphics.Viewport.Height / Math.Max(1, graphics.Viewport.Width)), File.ReadAllBytes(ModAssets.Resolve("Shaders/weather_" + profile + ".mgfxo")), File.ReadAllBytes(ModAssets.Resolve("Shaders/sky_" + profile + ".mgfxo")), File.ReadAllBytes(ModAssets.Resolve("Shaders/snow_" + profile + ".mgfxo")), WeatherService.Director.Seed, WeatherService.Config.Altitude, WeatherService.Config.Latitude, Math.Clamp(quality, .5f, 1));
            }
            if (SnowRuntime.Pack == null) return;
            WeatherState state = WeatherService.Current; double seconds = WeatherService.Clock.Seconds;
            SolarState sun = WeatherService.Sun;
            Vector2 listener = Cinematics.CinematicClient.BlocksInput ? camera.Position : Character.Controlled?.WorldPosition ?? camera.Position;
            var observer = new Vec2(listener.X / 100, listener.Y / 100);
            immersed = zone.Contains(observer.X, observer.Y);
            WeatherLight.Prepare(WeatherService.Grid, view);
            float frame = (float)Math.Clamp(deltaTime, 0, .05);
            WeatherService.Grid.DoorSeconds = WeatherGrid.DoorTravelSeconds; WeatherService.Grid.Settle((float)Math.Clamp(deltaTime, 0, .1));
            airflow.Place(WeatherService.Grid, new Vec2((seen.Left + seen.Right) * .5f, (seen.Bottom + seen.Top) * .5f), state.Wind);
            if (!WeatherService.Clock.Frozen) airflow.Step(state.Wind, frame);
            if (float.IsNaN(skyline)) skyline = WeatherService.Grid.Skyline((zone.Left + zone.Right) * .5f, (zone.Right - zone.Left) * .5f);
            painter.Prepare(SnowRuntime.Pack, zone, Backdrop(seen), view, observer, state, sun, seconds, WeatherService.Director.Day(seconds), WeatherService.Director.DayOfYear(seconds), WeatherService.OutdoorLight, airflow, skyline);
            active = Math.Clamp((int)(WeatherService.Config.Particles * quality), 1, particles.Capacity);
            if (!WeatherService.Clock.Frozen) particles.Step(state, seconds, frame, active, seen, SnowRuntime.Pack, airflow);
            backCalls = 0; covered = false; drawing = true;
        }
        private const float CoverMargin = .12f;
        private static WeatherRect Covering(Camera camera) {
            Rectangle v = camera.WorldView;
            float roll = MathF.Abs(camera.Rotation), cos = MathF.Abs(MathF.Cos(roll)), sin = MathF.Abs(MathF.Sin(roll));
            float halfWidth = v.Width * .5f, halfHeight = v.Height * .5f;
            float reachX = (halfWidth * cos + halfHeight * sin) * (1 + CoverMargin), reachY = (halfWidth * sin + halfHeight * cos) * (1 + CoverMargin);
            float centreX = v.X + halfWidth, centreY = v.Y - halfHeight;
            return new WeatherRect((centreX - reachX) / 100f, (centreY - reachY) / 100f, (centreX + reachX) / 100f, (centreY + reachY) / 100f);
        }
        private const float BackdropMargin = .5f;
        private static WeatherRect Backdrop(in WeatherRect seen) {
            float growX = (seen.Right - seen.Left) * BackdropMargin, growY = (seen.Top - seen.Bottom) * BackdropMargin;
            return new WeatherRect(seen.Left - growX, seen.Bottom - growY, seen.Right + growX, seen.Top + growY);
        }
        private static void Background(SpriteBatch spriteBatch, bool editing) {
            if (!drawing || !immersed || editing || ++backCalls != 2) return;
            painter.DrawSky(spriteBatch, WeatherService.UnitsPerMetre);
        }
        private static void Cover(SpriteBatch spriteBatch) {
            if (!drawing || painter == null || covered) return;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            try { Paint(spriteBatch); }
            finally { DrawMilliseconds = Smooth(DrawMilliseconds, started); }
        }
        private static void Paint(SpriteBatch spriteBatch) {
            if (!drawing || painter == null || covered) return;
            if (SnowRuntime.Pack == null) return;
            covered = true;
            Camera cam = GameMain.GameScreen.Cam;
            float pixelsPerMetre = PixelsPerMetre(cam);
            WeatherState state = WeatherService.Current;
            spriteBatch.End();
            try {
                painter.DrawSnow(SnowRuntime.Pack, state, cam.Transform, WeatherService.UnitsPerMetre, pixelsPerMetre);
                spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.LinearClamp, DepthStencilState.None, transformMatrix: cam.Transform);
                try { painter.DrawFlakes(spriteBatch, SnowRuntime.Pack, particles, active, state, WeatherService.Clock.Seconds, WeatherService.UnitsPerMetre, pixelsPerMetre); painter.DrawBlood(spriteBatch, blood.Active, WeatherService.UnitsPerMetre, pixelsPerMetre); }
                finally { spriteBatch.End(); }
                if (immersed) painter.DrawAtmosphere(GameMain.GraphicsWidth, GameMain.GraphicsHeight, new Rectangle(0, 0, GameMain.GraphicsWidth, GameMain.GraphicsHeight), state, WeatherService.Clock.Seconds, pixelsPerMetre, cam.Transform, WeatherService.UnitsPerMetre);
            }
            finally { spriteBatch.Begin(SpriteSortMode.BackToFront, BlendState.NonPremultiplied, null, DepthStencilState.None, null, null, cam.Transform); }
        }
        private static float PixelsPerMetre(Camera cam) => (cam.WorldToScreen(new Vector2(WeatherService.UnitsPerMetre, 0)) - cam.WorldToScreen(Vector2.Zero)).Length();
        private static void End() {
            drawing = false; WeatherLight.EndFrame();
        }
        private static Exception FinalizeDraw(Exception __exception) { End(); return __exception; }
        internal static void Release() { End(); WeatherLight.Release(); painter?.Dispose(); painter = null; particles = null; airflow = null; blood.Clear(); WeatherAudio.Release(); }
    }
}
