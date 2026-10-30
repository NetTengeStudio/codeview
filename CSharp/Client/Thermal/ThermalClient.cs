using System;
using System.Collections.Generic;
using System.Globalization;
using Barotrauma;
using Barotrauma.Particles;
using Microsoft.Xna.Framework;
using FarseerPhysics;
using ScpItdMod.Core;
using ScpItdMod.Weather;

namespace ScpItdMod.Thermal {
    internal static class ThermalClient {
        private static readonly Dictionary<Character, double> breath = new Dictionary<Character, double>();
        private static readonly List<Character> removed = new List<Character>();
        private static ParticlePrefab vapour;
        internal static void Reset() {
            breath.Clear(); removed.Clear(); vapour = null;
            ThermalHealthPanel.Reset();
            ThermalFieldView.Reset();
        }
        internal static string Describe(in ThermalReading r) => string.Format(CultureInfo.InvariantCulture,
            "Тело {0:0.00} °C; холодный участок кожи {1:0.0} °C; воздух {2:0.0} °C; ветер {3:0.0} м/с; влага {4:0}%; резерв дрожи {5:0}%; повреждение тканей {6:0.0}%; кисти {7:0}%; ходьба {8:0}%; боль {9:0}%; холодовой шок {10:0}%; мокрый холод {11:0}%.", r.Core, r.Skin, r.Air, r.Wind, r.Wetness * 100, r.Reserve * 100, r.Frostbite * 100, r.Dexterity * 100, r.Gait * 100, r.Pain * 100, r.ColdShock * 100, r.WetCold * 100);
        internal static void Step(float dt) {
            Camera camera = GameMain.GameScreen?.Cam;
            if (camera == null || GameMain.ParticleManager == null) return;
            if (vapour == null && !ParticlePrefab.Prefabs.TryGet("scpitd_breath".ToIdentifier(), out vapour)) throw new InvalidOperationException("Thermal breath particle missing");
            IReadOnlyList<Character> people = Humans.All;
            double time = WeatherService.Clock.Seconds; Rectangle view = camera.WorldView;
            for (int i = 0; i < people.Count; i++) {
                Character c = people[i];
                if (c.Removed || c.IsDead || c.AnimController == null || c.AnimController.HeadInWater) continue;
                Limb head = c.AnimController.GetLimb(LimbType.Head); if (head == null) continue;
                Vector2? mouth = c.AnimController.GetMouthPosition();
                if (!mouth.HasValue || c.InvisibleTimer > 0) continue;
                Vector2 at = ConvertUnits.ToDisplayUnits(mouth.Value) + (c.Submarine?.Position ?? Vector2.Zero);
                if (at.X < view.X - 100 || at.X > view.Right + 100 || at.Y > view.Y + 100 || at.Y < view.Y - view.Height - 100) continue;
                float air = WeatherService.IndoorAt(at) ? WeatherService.Config.IndoorTemperature : WeatherService.Current.Temperature;
                if (c == Character.Controlled && ThermalNetwork.TryLocal(out ThermalReading reading)) air = reading.Air;
                float condensation = ThermalBreath.Condensation(air, WeatherService.Current.Humidity);
                if (condensation <= 0) continue;
                Item mask = c.Inventory?.GetItemInLimbSlot(InvSlotType.Head);
                Item suit = c.Inventory?.GetItemInLimbSlot(InvSlotType.OuterClothes);
                if (mask != null && mask.Prefab.Identifier.Value.IndexOf("gasmask", StringComparison.OrdinalIgnoreCase) >= 0 || suit != null && suit.HasTag("divingsuit".ToIdentifier())) continue;
                if (!breath.TryGetValue(c, out double next)) { breath[c] = time + (c.ID % 31) / 10.0; continue; }
                if (time < next) continue;
                float activity = MathF.Min(2, c.AnimController.Collider?.LinearVelocity.Length() ?? 0);
                breath[c] = time + 3.6 / (1 + activity * .6);
                float direction = c.AnimController.Dir, exposure = WeatherService.SkyAt(at);
                Vector2 outward = new Vector2(direction * MathF.Cos(head.Rotation), direction * MathF.Sin(head.Rotation));
                Vector2 velocity = (c.AnimController.Collider?.LinearVelocity ?? Vector2.Zero) * 100 + outward * 35 + new Vector2(WeatherService.Current.Wind.X * exposure * 18, 5);
                int count = 2 + (int)(condensation * 5);
                for (int puff = 0; puff < count; puff++) {
                    Particle particle = GameMain.ParticleManager.CreateParticle(vapour, at + outward * (puff * 1.5f), velocity + outward * (puff * 4), hullGuess: c.CurrentHull);
                    if (particle != null) particle.Size *= .6f + condensation * .8f;
                }
            }
            removed.Clear(); foreach (var pair in breath) if (pair.Key.Removed || pair.Key.IsDead) removed.Add(pair.Key);
            for (int i = 0; i < removed.Count; i++) breath.Remove(removed[i]);
        }
    }
}
