using System;
using Barotrauma;
using Barotrauma.Items.Components;
using FarseerPhysics.Dynamics;
using HarmonyLib;
using ScpItdMod.Core;

namespace ScpItdMod.Weather {
    public sealed class WeatherModule : IModule, IRoundEnd {
        public void Attach(Harmony harmony) {
            WeatherService.Load();
            harmony.Patch(AccessTools.Method(typeof(Ragdoll), "ApplyImpact"), prefix: ModuleHost.Bind(typeof(SnowImpact), nameof(SnowImpact.Apply)));
            harmony.Patch(AccessTools.Method(typeof(Submarine), nameof(Submarine.CheckVisibility)), prefix: ModuleHost.Bind(typeof(SnowVisibility), nameof(SnowVisibility.Filter)));
            harmony.Patch(AccessTools.Method(typeof(World), nameof(World.Step), new[] { typeof(float) }),
                prefix: new HarmonyMethod(typeof(WeatherModule), nameof(Step)) { priority = Priority.First + 100 });
            harmony.Patch(AccessTools.PropertySetter(typeof(Gap), nameof(Gap.Open)), prefix: ModuleHost.Bind(typeof(WeatherModule), nameof(GapBefore)), postfix: ModuleHost.Bind(typeof(WeatherModule), nameof(GapAfter)));
            harmony.Patch(AccessTools.Method(typeof(Structure), nameof(Structure.SetDamage)), prefix: ModuleHost.Bind(typeof(WeatherModule), nameof(WallBefore)), postfix: ModuleHost.Bind(typeof(WeatherModule), nameof(WallChanged)));
            harmony.Patch(AccessTools.Method(typeof(Hull), nameof(Hull.Remove), Type.EmptyTypes), postfix: ModuleHost.Bind(typeof(WeatherMap), nameof(WeatherMap.Dirty)));
            harmony.Patch(AccessTools.Method(typeof(Structure), nameof(Structure.Remove), Type.EmptyTypes), postfix: ModuleHost.Bind(typeof(WeatherMap), nameof(WeatherMap.Dirty)));
            harmony.Patch(AccessTools.Method(typeof(Door), "SetState"), prefix: ModuleHost.Bind(typeof(WeatherGameplay), nameof(WeatherGameplay.DoorOpening)));
            harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.Control), new[] { typeof(float), typeof(Camera) }), prefix: ModuleHost.Bind(typeof(WeatherGameplay), nameof(WeatherGameplay.Control)));
            ModCommands.Register("weather", "set|wind|temp|time|season|timescale|snowdepth|freeze|forecast|dump|quality|dig", WeatherCommands.Request);
            WeatherNetwork.Attach(harmony);
#if CLIENT
            WeatherClient.Attach(harmony);
#endif
        }
        private static void Step(World __instance, float __0) { if (ReferenceEquals(__instance, GameMain.World)) WeatherService.Step(__0); }
        private static void GapBefore(Gap __instance, out bool __state) => __state = WeatherMap.GateOpen(__instance);
        private static void GapAfter(Gap __instance, bool __state) { if (WeatherService.Active && __instance.ConnectedDoor != null && __state != WeatherMap.GateOpen(__instance)) WeatherMap.GateChanged(__instance); }
        private static void WallBefore(Structure __instance, out Body __state) => __state = __instance.Bodies?.Count > 0 ? __instance.Bodies[0] : null;
        private static void WallChanged(Structure __instance, Body __state) {
            Body current = __instance.Bodies?.Count > 0 ? __instance.Bodies[0] : null;
            if (WeatherService.Active && __instance.HasBody && !ReferenceEquals(current, __state)) WeatherMap.Dirty();
        }
        public void RoundStart() => WeatherService.StartRound();
        public void RoundEnd() => WeatherService.Stop();
        public void Release() { WeatherService.Stop(); WeatherNetwork.Release(); }
    }
}
