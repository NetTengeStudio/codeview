using System;
using Barotrauma;
using Microsoft.Xna.Framework;
using ScpItdMod.Core;

namespace ScpItdMod.Weather {
    public static class WeatherService {
        public static WeatherConfig Config { get; private set; }
        private static WeatherRuntime runtime;
        public static WeatherDirector Director => runtime?.Director;
        public static WeatherState Current => runtime?.Current ?? default;
        public static SolarState Sun => runtime?.Sun ?? default;
        public static LightColor OutdoorLight => runtime?.OutdoorLight ?? default;
        public static bool Synchronized => runtime?.Ready ?? false;
        internal static bool NeedsSnapshot => runtime?.NeedsSnapshot ?? true;
        public static event Action<float> Updated;
        public static WeatherGrid Grid { get; internal set; }
        public static readonly WeatherClock Clock = new WeatherClock();
        internal static readonly SnowFlights Flights = new SnowFlights();
        public static bool Active { get; private set; }
        public static bool Authority => GameMain.NetworkMember == null || GameMain.NetworkMember.IsServer;
        public static bool FlightAllowed => !Active || PolarExposure.FlightAllowed(Current, Config);
        public static bool OutsideWorkAllowed => !Active || Current.Visibility >= 20 && Current.WindSpeed < Config.StormWind;
        public static uint Epoch => runtime?.Epoch ?? 0;
        public static bool InZone(Vector2 worldPoint) => Grid != null && Grid.Bounds.Contains(worldPoint.X / UnitsPerMetre, worldPoint.Y / UnitsPerMetre);
        public static event Action<bool> StormWarning;
        private static float accumulator;
        private const string SnowSimulationName = "snow simulation", FlightsName = "snow flights", BloodName = "snow blood", SupportName = "snow support", NetworkName = "weather network", ClientName = "weather client";
        private static readonly SubsystemGuard guard = new SubsystemGuard(FaultPolicy.TripLimit, typeof(WeatherService).Assembly, ModLog.Error);
        private static readonly Action snowSimulation = SimulateSnow, flightsStep = StepFlights, bloodStep = StepBlood, supportUpdate = SnowSupportBodies.Update, supportRelease = SnowSupportBodies.Release, networkTick = WeatherNetwork.Tick;
#if CLIENT
        private static readonly Action clientStep = StepClient;
        private static void StepClient() => WeatherClient.Step(frameSeconds);
#endif
        private static float frameSeconds;
        private static void StepFlights() => Flights.Step(SnowRuntime.Pack, Current, frameSeconds);
        private static void StepBlood() => WeatherBlood.Step(frameSeconds);
        private static void SimulateSnow() {
            SnowRuntime.Step(frameSeconds);
            if (Authority) WeatherGameplay.Physics(frameSeconds);
            accumulator += frameSeconds;
            while (accumulator >= SnowStep) {
                accumulator -= SnowStep;
                if (Authority) {
                    WeatherGameplay.SnowSources();
                    SnowRuntime.Weather(Current, SnowStep);
                    WeatherGameplay.Step(SnowStep);
                }
                else SnowRuntime.Observe(Current, SnowStep);
            }
        }
        private static bool warned;
        public const float UnitsPerMetre = 100, SnowStep = .25f;
        public static void Load() => Config = WeatherConfig.Load(ModAssets.Resolve("Content/Weather/weather.xml"), ModAssets.Resolve("Content/Weather/weather.xsd"));
        public static void StartRound() {
            Stop();
            if (Submarine.MainSub?.Info == null || !Config.Applies(Submarine.MainSub.Info.Name)) return;
            uint epoch = unchecked((uint)Environment.TickCount) ^ WeatherNoise.Seed(Level.Loaded?.Seed);
            runtime = new WeatherRuntime(Config, WeatherNoise.Seed(Level.Loaded?.Seed ?? Submarine.MainSub.Info.Name), epoch, Authority, Clock);
            Active = true; guard.Reset();
            WeatherMap.Reset(); WeatherMap.Rebuild(); WeatherGameplay.Reset(); WeatherNetwork.Reset();
            WeatherBov.Connect();
#if CLIENT
            WeatherClient.Start();
#endif
        }
        internal static void Step(float dt) {
            if (!Active || dt <= 0 || !float.IsFinite(dt)) return;
            if (!Synchronized) { guard.Run(NetworkName, networkTick); return; }
            runtime.Step(dt);
            WeatherMap.CheckMoved();
            if (WeatherMap.RebuildDue) WeatherMap.Rebuild();
            WeatherMap.Refresh(Current);
            WeatherBov.Update(Current);
            frameSeconds = dt;
            if (!Clock.Frozen) guard.Run(SnowSimulationName, snowSimulation);
            if (Authority && SnowRuntime.Ready) guard.Run(FlightsName, flightsStep);
            if (Authority && !Clock.Frozen) guard.Run(BloodName, bloodStep);
            guard.Run(SupportName, supportUpdate, supportRelease);
            bool alarm = !OutsideWorkAllowed || Current.Warning > .5f;
            if (alarm != warned) { warned = alarm; StormWarning?.Invoke(alarm); }
            guard.Run(NetworkName, networkTick);
            Updated?.Invoke(dt);
#if CLIENT
            guard.Run(ClientName, clientStep);
#endif
        }
        internal static WeatherReceiveResult ApplyState(ReadOnlySpan<byte> payload) => runtime.ApplyState(payload);
        internal static bool SynchronizeClock(uint epoch, uint sequence, double seconds, double roundTrip, bool frozen) => runtime.SynchronizeClock(epoch, sequence, seconds, roundTrip, frozen);
        internal static void WriteState(Net.WireWriter writer, bool snapshot, uint revision) => runtime.WriteState(writer, snapshot, revision);
        public static LightColor SkylightAt(Vector2 world) => !Active || Grid == null || !InZone(world) ? default
            : SkyLightField.Sample(Grid, world.X / UnitsPerMetre, world.Y / UnitsPerMetre, WeatherLighting.Environment(Current, Sun, OutdoorLight));
        public static WeatherControl Set(WeatherControlKind kind, float a, float b = 0, float seconds = 30) {
            if (!Active || !Authority) throw new InvalidOperationException("Weather requires the authoritative Vostok round");
            WeatherControl control = runtime.Set(kind, a, b, seconds);
            WeatherNetwork.Changed(); return control;
        }
        public static void SetRegime(WeatherRegime regime, float seconds = 90) => Set(WeatherControlKind.Regime, (float)regime, seconds: seconds);
        public static void Freeze(bool value) { if (!Active || !Authority) throw new InvalidOperationException("Weather is server-owned and requires an active round"); runtime.Freeze(value); WeatherNetwork.Changed(); }
        public static void DisturbSnow(Vector2 worldPoint, float radiusMetres, float depthMetres, bool remove = false) {
            if (!Authority || !Active || !SnowRuntime.Ready) return;
            var point = new Vec2(worldPoint.X, worldPoint.Y) / UnitsPerMetre;
            if (remove) SnowShovel.Dig(SnowRuntime.Pack, point, radiusMetres * 2, Math.Max(depthMetres, .01f), double.MaxValue); else SnowTools.PressInto(SnowRuntime.Pack, point, radiusMetres, depthMetres);
            WeatherNetwork.SnowChanged();
        }
        public static bool IndoorAt(Vector2 world) => Active && Grid != null && Grid.IsInterior(Grid.Index(world.X / UnitsPerMetre, world.Y / UnitsPerMetre));
        public static float SkyAt(Vector2 world) => !Active || Grid == null || !InZone(world) ? 1 : Grid.ExposureAt(world.X / UnitsPerMetre, world.Y / UnitsPerMetre);
        public static SnowFooting Footing(Vector2 footWorld, float below = SnowContactModel.FootBelow) => !Active || !SnowRuntime.Ready ? SnowFooting.Bare : SnowRuntime.Footing(new Vec2(footWorld.X, footWorld.Y) / UnitsPerMetre, below);
        public static SnowFooting Footing(Character character) => Footing(FootPoint(character));
        public static Vector2 FootPoint(Character character) {
            Ragdoll body = character?.AnimController;
            Limb left = body?.GetLimb(LimbType.LeftFoot), right = body?.GetLimb(LimbType.RightFoot);
            Limb foot = left == null || right != null && right.WorldPosition.Y < left.WorldPosition.Y ? right : left;
            return foot?.WorldPosition ?? character?.WorldPosition ?? Vector2.Zero;
        }
        public static float Underfoot(Vector2 worldPoint, float belowMetres, out float depthMetres, out float compaction) {
            SnowFooting footing = Footing(worldPoint, belowMetres);
            depthMetres = footing.Depth; compaction = footing.Compaction;
            return !Active || Grid == null ? 0 : Grid.Exposure(Grid.Index(worldPoint.X / UnitsPerMetre, worldPoint.Y / UnitsPerMetre));
        }
        public static void Stop() {
            WeatherBlood.Clear();
#if CLIENT
            WeatherClient.Release();
#endif
            Flights.Clear(); WeatherBov.Release(); WeatherGameplay.Reset(); WeatherMap.Reset(); SnowSupportBodies.Release(); SnowRuntime.Stop();
            Active = false; Grid = null; runtime = null; Clock.Reset(); accumulator = 0; warned = false;
            WeatherNetwork.Reset();
        }
    }
}
