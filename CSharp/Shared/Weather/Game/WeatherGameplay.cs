using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;
using ScpItdMod.Core;

namespace ScpItdMod.Weather {
    internal static class WeatherGameplay {
        private sealed class PowerLease {
            internal Item Item;
            internal RelayComponent Relay;
            internal bool WasOn, Held;
        }
        private static readonly List<PowerLease> power = new List<PowerLease>();
        private static int heatTag = -1, objectsTag = -1, powerTag = -1;
        internal static void Reset() {
            for (int i = 0; i < power.Count; i++) if (power[i].Held && !power[i].Item.Removed) power[i].Relay.SetState(power[i].WasOn, false);
            power.Clear();
            if (heatTag < 0) { heatTag = Tagged.Watch("weather_heat".ToIdentifier()); objectsTag = Tagged.Watch("weather_external".ToIdentifier()); powerTag = Tagged.Watch("weather_power".ToIdentifier()); }
        }
        internal static void SnowSources() {
            Material.SnowClimate snow = SnowRuntime.Climate; if (snow == null) return;
            snow.ClearHeat(); IReadOnlyList<Item> heaters = Tagged.Of(heatTag);
            for (int i = 0; i < heaters.Count; i++) {
                Item item = heaters[i]; if (item.Condition <= 0 || item.GetComponent<Powered>() is Powered powered && powered.Voltage < powered.MinVoltage) continue;
                Vector2 position = item.WorldPosition; snow.Heat(new Vec2(position.X / 100, position.Y / 100), 4, 1);
            }
        }
        internal static void Physics(float dt) {
            if (!WeatherService.Authority) return;
            WeatherState s = WeatherService.Current; WeatherGrid grid = WeatherService.Grid;
            float density = s.Pressure * 100 / (287.05f * (s.Temperature + 273.15f));
            IReadOnlyList<Item> external = Tagged.Of(objectsTag);
            for (int i = 0; i < external.Count; i++) {
                Item item = external[i]; if (item.body == null || item.ParentInventory != null || !item.body.Enabled) continue;
                Vector2 p = item.WorldPosition; float exposure = grid.Exposure(grid.Index(p.X / 100, p.Y / 100));
                float area = item.WorldRect.Width * item.WorldRect.Height * .0001f;
                float relative = s.Wind.X - item.body.LinearVelocity.X;
                item.body.ApplyForce(new Vector2(Math.Clamp(.5f * density * area * relative * MathF.Abs(relative) * exposure, -10000, 10000), 0));
            }
        }
        internal static void Step(float dt) => UpdatePower();
        internal static void Control(Character __instance) {
            if (!WeatherService.Active || WeatherService.Grid == null) return;
            __instance.StackSpeedMultiplier(SnowCharacters.Speed(__instance));
        }
        internal static bool DoorOpening(Door __instance, bool open, bool isNetworkMessage) {
            if (!WeatherService.Active || !WeatherService.Authority || isNetworkMessage || !open || __instance.LinkedGap?.IsRoomToRoom != false) return true;
            Rectangle r = __instance.Item.WorldRect; float reach = r.Width * .5f + 40, sill = r.Y - r.Height, middle = r.X + r.Width * .5f, drift = 0;
            for (int side = -1; side <= 1; side++) drift = MathF.Max(drift, WeatherService.Footing(new Vector2(middle + side * reach, sill)).Depth);
            return drift < WeatherService.Config.DoorDepth;
        }
        private static void UpdatePower() {
            IReadOnlyList<Item> items = Tagged.Of(powerTag);
            for (int i = 0; i < items.Count; i++) {
                Item item = items[i]; bool found = false; for (int j = 0; j < power.Count; j++) if (power[j].Item == item) { found = true; break; }
                if (!found && item.GetComponent<RelayComponent>() is RelayComponent relay) power.Add(new PowerLease { Item = item, Relay = relay });
            }
            double time = WeatherService.Clock.Seconds; WeatherState state = WeatherService.Current;
            bool outage = state.Regime == WeatherRegime.Storm && state.WindSpeed > WeatherService.Config.StormWind && WeatherNoise.Value(WeatherService.Director.Seed ^ 0x36b72941u, time / 45) > .35f;
            for (int i = power.Count - 1; i >= 0; i--) {
                PowerLease p = power[i]; if (p.Item.Removed) { power.RemoveAt(i); continue; }
                if (outage && !p.Held) { p.WasOn = p.Relay.IsOn; p.Held = true; p.Relay.SetState(false, false); }
                else if (!outage && p.Held) { p.Relay.SetState(p.WasOn, false); p.Held = false; }
            }
        }
    }
}
