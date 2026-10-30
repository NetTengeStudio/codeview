using System;
using System.Collections.Generic;
using System.Reflection;
using Barotrauma;
using HarmonyLib;
using ScpItdMod.Core;
using ScpItdMod.Weather;
#if CLIENT
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endif

namespace ScpItdMod.Thermal {
    public sealed class ThermalModule : IModule, IRoundEnd {
        private Harmony harmony;
        private readonly HashSet<MethodBase> compatibility = new HashSet<MethodBase>();
        public void Attach(Harmony owner) {
            harmony = owner;
            WeatherService.Updated += ThermalRuntime.Step;
            ThermalMap.Attach(); ThermalNetwork.Attach();
            harmony.Patch(AccessTools.Method(typeof(Item), nameof(Item.ApplyTreatment)), prefix: ModuleHost.Bind(typeof(ThermalModule), nameof(Treatment)));
            harmony.Patch(AccessTools.Method(typeof(Gap), nameof(Gap.Remove), Type.EmptyTypes), postfix: ModuleHost.Bind(typeof(WeatherMap), nameof(WeatherMap.Dirty)));
#if CLIENT
            harmony.Patch(AccessTools.Method(typeof(CharacterHealth), nameof(CharacterHealth.AddToGUIUpdateList), Type.EmptyTypes), prefix: ModuleHost.Bind(typeof(ThermalModule), nameof(PrepareFelt)));
            harmony.Patch(AccessTools.PropertySetter(typeof(CharacterHealth), nameof(CharacterHealth.OpenHealthWindow)), postfix: ModuleHost.Bind(typeof(ThermalModule), nameof(HealthWindowChanged)));
            harmony.Patch(AccessTools.Method(typeof(GameScreen), nameof(GameScreen.AddToGUIUpdateList), Type.EmptyTypes), postfix: ModuleHost.Bind(typeof(ThermalModule), nameof(PollFelt)));
#endif
            ModCommands.Register("cold", "status|on|off|toggle|map air|map surface|map off|trace start|trace stop: thermal diagnostics and cold control", Read);
            ModCommands.Register("scpitd_cold", "[on|off|toggle]: enable/disable cold; no arguments: toggle", Control);
        }
        public void RoundStart() {
            ThermalRuntime.Reset();
            if (!WeatherService.Active) {
                string map = Submarine.MainSub?.Info?.Name ?? "(no main submarine)";
                if (WeatherService.Config?.Applies(map) == true) {
                    ModLog.Error("Thermal expected an active weather round for " + map + ": " + ThermalRuntime.Diagnostics());
                }
                return;
            }
            ThermalRuntime.Load();
            ThermalMap.Start();
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()) {
                Type type = assembly.GetType("PrimaryPackage.Cold.ColdPlugin", false);
                if (type == null) continue;
                foreach (string method in new[] { "SessionUpdatePostfix", "ApplyTreatmentPrefix" }) {
                    MethodInfo target = AccessTools.DeclaredMethod(type, method);
                    if (target == null) throw new InvalidOperationException("Unsupported legacy cold entry point: " + method);
                    if (compatibility.Add(target)) harmony.Patch(target, prefix: ModuleHost.Bind(typeof(ThermalModule), nameof(Legacy)));
                }
                ModLog.Message("Thermal: legacy Primary package cold paused on this weather map");
            }
            ThermalRuntime.Begin();
            ModLog.Message("Thermal ready for " + (Submarine.MainSub?.Info?.Name ?? "(unknown submarine)") +
                "; observed sources=" + ThermalMap.SourceCount + ". Use /cold status for component and room diagnostics.");
        }
#if CLIENT
        public static void PollFelt() { ThermalHealthPanel.Poll(); ThermalFieldView.Prepare(); }
        public static void HealthWindowChanged(CharacterHealth value) => ThermalHealthPanel.Changed(value);
        public static void PrepareFelt(CharacterHealth __instance, GUIProgressBar ___healthWindowHealthBar) => ThermalHealthPanel.Prepare(__instance, ___healthWindowHealthBar);
#endif
        private static bool Legacy() => !WeatherService.Active || !ThermalRuntime.Ready;
        private static void Treatment(Item __instance, Character character) {
            if (WeatherService.Active && __instance.Prefab.Identifier == "pp_towel".ToIdentifier()) ThermalRuntime.Dry(character);
        }
        private static void Control(string[] args) {
            if (args.Length > 2) { ModLog.Message("cold status|on|off|toggle|trace start|trace stop"); return; }
            var command = new string[Math.Max(2, args.Length + 1)]; command[0] = "cold";
            for (int i = 0; i < args.Length; i++) command[i + 1] = args[i];
            if (args.Length == 0) command[1] = "toggle";
            WeatherCommands.Request(command);
        }
        private static void Read(string[] args) {
            if (args.Length > 0) {
#if CLIENT
                if (args[0] == "map") { ThermalFieldView.Command(args); return; }
#endif
                if (args.Length == 1 && string.Equals(args[0], "status", StringComparison.OrdinalIgnoreCase)) {
#if CLIENT
                    if (!WeatherService.Authority) { WeatherCommands.Request(new[] { "cold", "status" }); return; }
#endif
                    ModLog.Message(ThermalRuntime.Diagnostics());
                    return;
                }
                Control(args);
                return;
            }
#if CLIENT
            if (ThermalNetwork.TryLocal(out ThermalReading r)) { ModLog.Message(ThermalClient.Describe(r)); return; }
            if (!WeatherService.Authority) { WeatherCommands.Request(new[] { "cold", "status" }); return; }
#endif
            ModLog.Message(ThermalRuntime.Diagnostics());
        }
        public void RoundEnd() => ThermalRuntime.Reset();
        public void Release() { WeatherService.Updated -= ThermalRuntime.Step; ThermalRuntime.Reset(); ThermalMap.Release(); compatibility.Clear(); harmony = null; }
    }
}
