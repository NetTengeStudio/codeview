using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework;
using ScpItdMod.Core;
using ScpItdMod.Weather.Material;

namespace ScpItdMod.Weather {
    internal static class WeatherBov {
        private static readonly object owner = new object();
        private static Action<object, Vector4, Vector2> publish;
        private static Action<object> release;
        private static SnowVehicleContacts vehicleContacts;
        private static Func<Vector2, ushort, Vector2> rotorWind;
        private static Action<object, Func<Vector2, Vector2>> publishSnow;
        private static Action<object, Action<Vector2, float, float>> publishSnowDisturb;
#if CLIENT
        private delegate float GainCall(out bool muffled);
        private static GainCall gain;
        internal static float EnvironmentGain(out bool muffled) { muffled = false; return gain != null ? gain(out muffled) : 1; }
#endif
        internal static void Connect() {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()) {
                Type type = assembly.GetType(BovBridgeContract.TypeName, false); if (type == null || type.GetProperty("IsActive")?.GetValue(null) is false) continue;
                try { Attach(type); }
                catch (Exception problem) when (problem is InvalidOperationException or ArgumentException) { Release(); ModLog.Error("Vostok weather: BOV atmosphere adapter not used, the round continues without it: " + problem.Message); }
                return;
            }
            ModLog.Message("Vostok weather: BOV atmosphere adapter inactive; no WeatherBridge assembly loaded");
        }
        // Every member the adapter needs is looked up before anything is published to BOV, so a bridge of another version or a build that lacks a member leaves no half-connected state behind.
        private static void Attach(Type type) {
            Dictionary<string, MethodInfo> members = BovBridgeContract.Resolve(type);
            MethodInfo surface = members["PublishSurface"];
            ParameterInfo[] surfaceParameters = surface.GetParameters();
            MethodInfo local = typeof(WeatherBov).GetMethod(nameof(SampleSurface), BindingFlags.Static | BindingFlags.NonPublic);
            Delegate sampler;
            try { sampler = Delegate.CreateDelegate(surfaceParameters[1].ParameterType, local); }
            catch (ArgumentException problem) { throw new InvalidOperationException("BOV surface sampler signature differs from the one this mod provides: " + problem.Message, problem); }
            publish = members["Publish"].CreateDelegate<Action<object, Vector4, Vector2>>();
            release = members["Release"].CreateDelegate<Action<object>>();
            publishSnow = members["PublishSnow"].CreateDelegate<Action<object, Func<Vector2, Vector2>>>();
            publishSnowDisturb = members["PublishSnowDisturb"].CreateDelegate<Action<object, Action<Vector2, float, float>>>();
            rotorWind = members["RotorAirflowAt"].CreateDelegate<Func<Vector2, ushort, Vector2>>();
            Action<object, object> support = members["PublishSnowSupport"].CreateDelegate<Action<object, object>>();
            Action<object, Func<Vector2, ushort, Vector2>> wind = members["PublishWind"].CreateDelegate<Action<object, Func<Vector2, ushort, Vector2>>>();
#if CLIENT
            gain = members["EnvironmentGain"].CreateDelegate<GainCall>();
#endif
            try {
                publishSnow.Invoke(owner, SnowSample);
                publishSnowDisturb.Invoke(owner, DisturbSnow);
                support.Invoke(owner, SnowSupportBodies.Marker);
                surface.Invoke(null, new object[] { owner, sampler, (Action<Vector2, Vector4, Vector4, ushort>)LoadSurface });
                vehicleContacts = new SnowVehicleContacts(SnowRuntime.Pack, WeatherNetwork.SnowBurst);
                wind.Invoke(owner, LocalWind);
                SnowRuntime.Pack.ExternalWind = RotorWind;
            } catch (TargetInvocationException problem) {
                Release();
                throw new InvalidOperationException("BOV rejected the publication: " + (problem.InnerException?.Message ?? problem.Message), problem);
            }
        }
        internal static Vector2 SnowStrength(float compaction) => new Vector2(SnowConstitutive.SinteredCohesion * Math.Clamp(compaction, 0, 1), SnowConstitutive.Friction);
        // Without BOV the delegate adds nothing and may be called from any thread; BOV's rotor wash drops expired sources inside the call, so it stays on the calling thread.
        internal static bool RotorWindConcurrent => rotorWind == null;
        internal static Vec2 RotorWind(Vec2 point) {
            Vector2 wind = rotorWind?.Invoke(new Vector2(point.X, point.Y) * WeatherService.UnitsPerMetre, 0) ?? default;
            return new Vec2(wind.X, wind.Y);
        }
        private static Vector2 LocalWind(Vector2 world, ushort excluded) {
            Vec2 point = new Vec2(world.X, world.Y) / WeatherService.UnitsPerMetre;
            float exposure = WeatherService.Grid?.ExposureAt(point.X, point.Y) ?? 1;
            Vec2 wind = WeatherService.Current.Wind * exposure;
            return new Vector2(wind.X, wind.Y) + (rotorWind?.Invoke(world, excluded) ?? default);
        }
        private static bool SampleSurface(Vector2 world, float reach, out Vector4 geometry, out Vector4 strength, out Vector4 state) {
            geometry = strength = state = default;
            if (vehicleContacts == null || !vehicleContacts.Sample(new Vec2(world.X, world.Y) / WeatherService.UnitsPerMetre, reach, out SnowVehicleSurface sample)) return false;
            geometry = new Vector4(sample.Bottom * WeatherService.UnitsPerMetre, sample.Top * WeatherService.UnitsPerMetre, sample.Density, SnowConstitutive.MaximumDensity);
            strength = new Vector4(sample.Crush, 3, sample.Cohesion, sample.Friction);
            state = new Vector4(sample.Modulus, sample.Water, sample.Compaction, SnowConstitutive.Spacing);
            return true;
        }
        private static void LoadSurface(Vector2 world, Vector4 patch, Vector4 motion, ushort predictor) {
            if (vehicleContacts == null) return;
#if CLIENT
            if (!WeatherService.Authority && (predictor == 0 || Barotrauma.Character.Controlled?.ID != predictor)) return;
#endif
            if (vehicleContacts.Load(new Vec2(world.X, world.Y) / WeatherService.UnitsPerMetre, patch.X, patch.Y, patch.Z, patch.W,
                motion.X, motion.Y, motion.Z, WeatherService.Authority ? WeatherService.Flights : null, WeatherService.Current.Temperature, predictor)) WeatherNetwork.SnowChanged();
        }
        private const float WheelReach = 1f;
        private static Vector2 SnowSample(Vector2 world) {
            WeatherService.Underfoot(world, WheelReach, out float depth, out float compaction);
            return new Vector2(depth, compaction);
        }
        internal static void Update(in WeatherState s) => publish?.Invoke(owner, new Vector4(s.Temperature, s.Pressure, s.Humidity, s.Visibility), new Vector2(s.Wind.X, s.Wind.Y));
        private static void DisturbSnow(Vector2 world, float radiusMetres, float depthMetres) {
            if (!WeatherService.Authority || !SnowRuntime.Ready) return;
            SnowTools.PressInto(SnowRuntime.Pack, new Vec2(world.X, world.Y) / WeatherService.UnitsPerMetre, radiusMetres, depthMetres);
            WeatherNetwork.SnowChanged();
        }
        internal static void Release() {
            release?.Invoke(owner); publish = null; release = null; vehicleContacts = null; publishSnow = null; publishSnowDisturb = null; rotorWind = null;
            if (SnowRuntime.Pack != null) SnowRuntime.Pack.ExternalWind = null;
#if CLIENT
            gain = null;
#endif
        }
    }
}
