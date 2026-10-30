using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;
using ScpItdMod.Core;
using ScpItdMod.Net;
using ScpItdMod.Thermal;
using ScpItdMod.Weather;
#if CLIENT
using Microsoft.Xna.Framework.Graphics;
#endif

namespace ScpItdMod.Bath {
    // Адаптер купели к игре: вода считается на авторитете, снимки уходят клиентам.
    // Печь — источник тепловой модели (мощность и воздух), насос берёт воду из корпуса,
    // слив через пробку возвращает её туда же; погружение и температура отдаются тепловой модели.
    public sealed class BathModule : IModule, IRoundEnd {
        public static readonly Identifier Tag = "scpitd_bath".ToIdentifier();
        public static readonly Identifier PlugTag = "scpitd_bath_plug".ToIdentifier();
        private static readonly Identifier PlugId = "scpitd_bath_plug".ToIdentifier();
        private const float FallbackAmbient = 18f;
        private const double SnapshotInterval = 0.2;  // 5 Гц

        private sealed class Basin {
            public BathWater Water;
            public float Snow;
            public bool Heating, Seeded;
            public readonly BathBasin Geometry;
            public Basin(Item item) {
                Geometry = new BathBasin(item);
                Water = new BathWater(0, FallbackAmbient);
            }
        }

        private static int registry = -1;
        private static readonly Dictionary<Item, Basin> basins = new Dictionary<Item, Basin>();
        private static readonly Dictionary<ushort, BathSnapshot> views = new Dictionary<ushort, BathSnapshot>();
        private static readonly List<Item> stale = new List<Item>();
        private static ItemPrefab plugPrefab;
        private static double nextSnapshot;

        // Снимки для отрисовки: на клиенте приходят по сети, на авторитете копируются локально.
        public static IReadOnlyDictionary<ushort, BathSnapshot> Views => views;

        public void Attach(Harmony harmony) {
            registry = Tagged.Watch(Tag);
#if SERVER
            harmony.Patch(AccessTools.Method(typeof(GameServer), nameof(GameServer.Update), new[] { typeof(float) }),
                postfix: ModuleHost.Bind(typeof(BathModule), nameof(ServerTick)));
#else
            harmony.Patch(AccessTools.Method(typeof(GameScreen), nameof(GameScreen.Update), new[] { typeof(double) }),
                postfix: ModuleHost.Bind(typeof(BathModule), nameof(ClientTick)));
            harmony.Patch(AccessTools.Method(typeof(Submarine), nameof(Submarine.DrawFront)),
                postfix: ModuleHost.Bind(typeof(BathModule), nameof(DrawPostfix)));
            ModNet.ReceiveBytes(BathWire.Channel, BathWire.Bytes, OnSnapshot);
#endif
        }
        public void RoundStart() => Reset();
        public void RoundEnd() => Reset();
        public void Release() => Reset();
        private static void Reset() { basins.Clear(); views.Clear(); nextSnapshot = 0; }

#if SERVER
        public static void ServerTick(float __0) => Step(__0);
#else
        public static void ClientTick(double __0) {
            Step((float)__0);
            BathDraw.Tick((float)__0);
        }
        public static void DrawPostfix(SpriteBatch spriteBatch) => BathDraw.Front(spriteBatch);
        private static void OnSnapshot(byte[] payload) {
            if (BathWire.Read(payload, out BathSnapshot snapshot)) { views[snapshot.Item] = snapshot; }
        }
#endif

        private static void Step(float dt) {
            if (registry < 0 || !WeatherService.Authority || dt <= 0) { return; }
            IReadOnlyList<Item> baths = Tagged.Of(registry);
            foreach (Item bath in baths) {
                if (bath.Removed) { continue; }
                if (!basins.TryGetValue(bath, out Basin basin)) { basin = new Basin(bath); basins[bath] = basin; }
                if (!basin.Seeded) { Seed(bath, basin); }
                Advance(bath, basin, dt);
            }
            nextSnapshot -= dt;
            if (nextSnapshot <= 0) {
                nextSnapshot = SnapshotInterval;
                foreach (Item bath in baths) { if (!bath.Removed && basins.TryGetValue(bath, out Basin basin)) { Publish(bath, basin); } }
            }
            if (views.Count > 0 && basins.Count > 0) {
                stale.Clear();
                foreach (Item known in basins.Keys) { if (known.Removed) { stale.Add(known); } }
                foreach (Item gone in stale) { basins.Remove(gone); views.Remove(gone.ID); }
            }
        }

        // Пробка лежит в гнезде с выпуска: спавн один раз на чашу, без пробки купель всегда открыта.
        private static void Seed(Item bath, Basin basin) {
            basin.Seeded = true;
            if (Plugged(bath)) { return; }
            plugPrefab ??= ItemPrefab.Find(null, PlugId);
            if (plugPrefab == null || Entity.Spawner == null) { return; }
            foreach (ItemContainer container in bath.GetComponents<ItemContainer>()) {
                if (container.Inventory == null || container.Inventory.IsFull(true)) { continue; }
                Entity.Spawner.AddItemToSpawnQueue(plugPrefab, container.Inventory);
                return;
            }
        }

        private static void Advance(Item bath, Basin basin, float dt) {
            BathBasin geometry = basin.Geometry;
            Powered power = bath.GetComponent<Powered>();
            bool powered = power != null && power.HasPower;
            // Насос берёт воду из корпуса, где стоит купель, и возвращает слитую туда же.
            Hull hull = Hull.FindHull(bath.WorldPosition, null, true);
            if (powered && hull != null && hull.WaterVolume > 0 && basin.Water.Litres < geometry.Capacity) {
                float available = hull.WaterVolume / geometry.UnitsPerLitre;
                float pumped = BathMath.Pumped(dt, geometry.PumpRate, available);
                float sourceCelsius = ThermalMap.Field != null ? ThermalMap.Sample(new Vector2(bath.WorldPosition.X, hull.WorldSurface)).Air : FallbackAmbient;
                basin.Water = BathMath.Pour(basin.Water, pumped, sourceCelsius, geometry.Capacity, out float accepted);
                hull.WaterVolume -= accepted * geometry.UnitsPerLitre;
            }
            if (!Plugged(bath) && basin.Water.Litres > 0) {
                float before = basin.Water.Litres;
                basin.Water = BathMath.Drain(basin.Water, dt, geometry.DrainRate);
                if (hull != null) { hull.WaterVolume += (before - basin.Water.Litres) * geometry.UnitsPerLitre; }
            }
            // Печь отдаёт мощность тепловой модели (воздух); её доля идёт в воду: сначала на таяние снега, остаток греет воду до уставки.
            basin.Heating = powered && BathMath.Heating(basin.Water, geometry.Setpoint);
            float energy = powered ? ThermalMap.SourcePower(bath) * geometry.WaterShare * dt : 0;
            float roomKg = MathF.Max(0, (geometry.Capacity - basin.Water.Litres) / BathMath.MeltLitresPerKg);
            float meltedKg = BathMath.MeltKg(energy, MathF.Min(basin.Snow, roomKg));
            if (meltedKg > 0) {
                basin.Snow -= meltedKg;
                basin.Water = BathMath.Pour(basin.Water, meltedKg * BathMath.MeltLitresPerKg, 0, geometry.Capacity, out _);
                energy -= meltedKg * BathMath.FusionHeat;
            }
            float stove = basin.Heating && dt > 0 ? energy / dt : 0;
            float ambient = ThermalMap.Field != null ? ThermalMap.AirAt(bath.WorldPosition) : FallbackAmbient;
            basin.Water = BathMath.Heat(basin.Water, dt, stove, ambient);
        }

        private static bool Plugged(Item bath) {
            foreach (ItemContainer container in bath.GetComponents<ItemContainer>()) {
                if (container.Inventory == null) { continue; }
                for (int i = 0; i < container.Inventory.Capacity; i++) {
                    if (container.Inventory.GetItemAt(i) is Item held && held.HasTag(PlugTag)) { return true; }
                }
            }
            return false;
        }

        private static void Publish(Item bath, Basin basin) {
            var snapshot = new BathSnapshot(bath.ID, basin.Water.Litres, basin.Water.Celsius, basin.Snow, Plugged(bath), basin.Heating);
            views[bath.ID] = snapshot;
#if SERVER
            if (GameMain.Server == null) { return; }
            var writer = new WireWriter(BathWire.Bytes);
            BathWire.Write(writer, snapshot);
            foreach (Client client in GameMain.Server.ConnectedClients) {
                ModNet.SendBytes(BathWire.Channel, client.Connection, writer.Span, DeliveryMethod.Unreliable);
            }
#endif
        }

        // Лопата сбрасывает снег над купелью: точка в метрах погоды (как у SnowShovel), часть загрузки уходит в приёмник.
        // Возвращает true, если хоть что-то принято; остаток остаётся в загрузке лопаты.
        public static bool Accept(Vec2 metres, ref SnowPayload load) {
            if (registry < 0 || !WeatherService.Authority || load.Mass <= 0) { return false; }
            Vector2 world = new Vector2((float)metres.X, (float)metres.Y) * WeatherService.UnitsPerMetre;
            foreach (Item bath in Tagged.Of(registry)) {
                if (!basins.TryGetValue(bath, out Basin basin)) { continue; }
                float s = bath.Scale;
                BathMath.ToLocal(world.X - bath.WorldPosition.X, world.Y - bath.WorldPosition.Y, ItemPose.Angle(bath), out float x, out float y);
                if (!basin.Geometry.InsideHopper(x, y, s)) { continue; }
                double room = Math.Max(0, basin.Geometry.SnowCapacity - basin.Snow);
                double taken = Math.Min(load.Mass, room);
                if (taken <= 0) { return false; }
                double share = taken / load.Mass;
                basin.Snow += (float)taken;
                load.Mass -= taken; load.Volume -= load.Volume * share;
                return true;
            }
            return false;
        }

        // Тепловая модель спрашивает, погружён ли персонаж в купель: доля тела и температура воды.
        // Чаша считается в локальной системе предмета, поэтому поворот учитывается; поверхность — в той же системе.
        public static float Depth(Vector2 world, out float celsius) {
            celsius = 0; float best = 0;
            if (registry < 0) { return 0; }
            foreach (Item bath in Tagged.Of(registry)) {
                if (!basins.TryGetValue(bath, out Basin basin) || basin.Water.Litres <= 0) { continue; }
                float s = bath.Scale;
                BathMath.ToLocal(world.X - bath.WorldPosition.X, world.Y - bath.WorldPosition.Y, ItemPose.Angle(bath), out float x, out float y);
                if (!basin.Geometry.InsideLocal(x, y, s)) { continue; }
                float surface = basin.Geometry.SurfaceLocal(basin.Water.Litres, s);
                float depth = BathMath.Submersion(y, surface);
                if (depth > best) { best = depth; celsius = basin.Water.Celsius; }
            }
            return best;
        }
    }
}
