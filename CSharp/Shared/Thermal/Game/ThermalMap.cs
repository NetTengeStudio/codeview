using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;
using ScpItdMod.Core;
using ScpItdMod.Weather;

namespace ScpItdMod.Thermal {
    internal static class ThermalMap {
        private sealed class Source {
            internal readonly Item Item;
            internal readonly ThermalSource Profile;
            internal readonly LightComponent Light;
            internal readonly Reactor Reactor;
            internal readonly Powered Powered;
            internal readonly Deconstructor Deconstructor;
            internal Vector2 Origin;
            internal float Demand = 1;
            internal double Interception;
            internal float Footprint => MathF.Max(Profile.Size, Field?.Grid.Cell * .7071068f ?? Profile.Size);
            internal float Reach => MathF.Max(Profile.Radius, Footprint);
            internal Source(Item item, ThermalSource profile) {
                Item = item; Profile = profile; Origin = item.WorldPosition;
                Light = item.GetComponent<LightComponent>(); Reactor = item.GetComponent<Reactor>(); Deconstructor = item.GetComponent<Deconstructor>();
                foreach (ItemComponent component in item.Components) if (component is Powered candidate) { Powered = candidate; break; }
            }
            internal bool ComponentOn => Profile.Mode == "light" ? Light?.IsOn == true : Profile.Mode == "reactor" ? Reactor?.IsActive == true : Profile.Mode == "deconstructor" ? Deconstructor?.IsActive == true : Powered?.IsActive == true;
            internal float ElectricalLoad => Powered != null ? Powered.CurrPowerConsumption : float.NaN;
            internal float Voltage => Light != null ? Light.Voltage : Powered != null ? Powered.Voltage : float.NaN;
            internal float MinimumVoltage => Light != null ? Light.MinVoltage : Powered != null ? Powered.MinVoltage : float.NaN;
            internal float Power {
                get {
                    if (Item.Removed || Item.Condition <= 0 || Item.InWater || Item.ParentInventory != null) return 0;
                    if (Profile.Mode == "light") return Light != null && Light.IsOn && (Light.PowerConsumption <= 0 || Light.Voltage >= Light.MinVoltage) ? Profile.Watts * Demand : 0;
                    if (Profile.Mode == "reactor") return Reactor == null ? 0 : Profile.Watts * Math.Clamp(Reactor.CurrPowerConsumption / -MathF.Max(1, Reactor.MaxPowerOutput), 0, 1) * Demand;
                    if (Profile.Mode == "deconstructor") return Deconstructor?.IsActive == true ? Profile.Watts * Demand : 0;
                    return Powered != null && Powered.IsActive && Powered.Voltage >= Powered.MinVoltage
                        ? MathF.Min(Profile.Watts, MathF.Max(0, Powered.CurrPowerConsumption)) * Demand : 0;
                }
            }
            internal void Update(ThermalField field) {
                Vec2 point = Metres(Item.WorldPosition);
                float extent = MathF.Sqrt(Item.WorldRect.Width * (float)Item.WorldRect.Width + Item.WorldRect.Height * (float)Item.WorldRect.Height) / 200;
                point = field.Emitter(point, MathF.Max(Profile.Size, extent)); Origin = new Vector2(point.X, point.Y) * WeatherService.UnitsPerMetre;
                Demand = Profile.Regulated ? Math.Clamp((Profile.Target - field.Sample(point).Air) / 2, 0, 1) : 1;
            }
        }
        private struct Flame { internal Vector2 World; internal float Watts, Size, Radius; internal double Interception; }
        private const float FlameRadiantWatts = 1200, FlameRadius = 4, FlameSize = .5f;
        private static readonly List<Source> sources = new List<Source>();
        private static readonly List<Flame> flames = new List<Flame>();
        private static readonly List<Hull> hulls = new List<Hull>();
        private static readonly HashSet<Item> knownSources = new HashSet<Item>();
        private static uint geometryRevision;
        private static Vector2 anchor;
        internal static ThermalField Field { get; private set; }
        internal static uint Revision { get; private set; }
        internal static double UpdatedAt { get; private set; } = double.NegativeInfinity;
        internal static int SourceCount => sources.Count;
        private static Vec2 Metres(Vector2 world) => new Vec2(world.X, world.Y) / WeatherService.UnitsPerMetre;
        internal static void Attach() { Tagged.ItemChanged += Observe; }
        internal static void Release() { Tagged.ItemChanged -= Observe; Reset(); }
        internal static void Observe(Item item) {
            if (item == null || item.Removed || !WeatherService.Active || ThermalRuntime.Catalog == null || !ThermalRuntime.Catalog.Sources.TryGetValue(item.Prefab.Identifier.Value, out ThermalSource profile) || !knownSources.Add(item)) return;
            sources.Add(new Source(item, profile));
        }
        internal static void Start() { foreach (Item item in Item.ItemList) Observe(item); }
        // Мощность, которую источник отдаёт в поле сейчас (Вт); тот же расчёт, что и для воздуха.
        internal static float SourcePower(Item item) {
            for (int i = 0; i < sources.Count; i++) if (sources[i].Item == item) return sources[i].Power;
            return 0;
        }
        internal static void Reset() {
            hulls.Clear(); sources.Clear(); flames.Clear(); knownSources.Clear(); Field = null;
            geometryRevision = 0; Revision = 0; anchor = default;
            UpdatedAt = double.NegativeInfinity;
        }
        private static void Rebuild() {
            Vector2 nextAnchor = Submarine.MainSub?.WorldPosition ?? Vector2.Zero;
            Field = new ThermalField(WeatherService.Grid, ThermalRuntime.Catalog, WeatherService.Current.Temperature, Field, Metres(nextAnchor - anchor));
            anchor = nextAnchor; hulls.Clear();
            foreach (Hull hull in Hull.HullList) if (!hull.Removed) hulls.Add(hull);
            geometryRevision = WeatherMap.Revision;
            if (++Revision == 0) Revision = 1;
        }
        internal static void Step(float dt) {
            if (Field == null || geometryRevision != WeatherMap.Revision || !ReferenceEquals(Field.Grid, WeatherService.Grid) || hulls.Count != Hull.HullList.Count) Rebuild();
            WeatherState weather = WeatherService.Current;
            Field.Begin(weather.Temperature); flames.Clear();
            for (int i = 0; i < hulls.Count; i++) {
                Hull hull = hulls[i]; if (hull.Removed) continue;
                if (hull.WaterVolume > 0) Field.Flood(WeatherMap.Rect(hull.WorldRect), hull.WorldSurface / WeatherService.UnitsPerMetre);
                var fires = hull.FireSources;
                for (int j = 0; j < fires.Count; j++) {
                    float width = Math.Clamp(fires[j].Size.X / WeatherService.UnitsPerMetre, 0, 10);
                    Vector2 world = new Vector2(fires[j].WorldPosition.X + fires[j].Size.X * .5f, fires[j].WorldPosition.Y);
                    Vec2 origin = Field.Emitter(Metres(world), MathF.Max(FlameSize, width * .5f));
                    float size = MathF.Max(FlameSize, width * .5f), radius = MathF.Max(FlameRadius, size);
                    Field.Emit(origin, size, width * 4000, FlameRadiantWatts / 4000, radius);
                    flames.Add(new Flame { World = new Vector2(origin.X, origin.Y) * WeatherService.UnitsPerMetre, Watts = width * FlameRadiantWatts, Size = size, Radius = radius });
                }
            }
            for (int i = sources.Count - 1; i >= 0; i--) {
                Source source = sources[i];
                if (source.Item.Removed) { knownSources.Remove(source.Item); sources.RemoveAt(i); continue; }
                source.Update(Field);
                if (source.Power > 0) Field.Emit(Metres(source.Origin), source.Footprint, source.Power, source.Profile.RadiantFraction, source.Reach);
            }
            Field.Step(weather.WindSpeed, dt);
            UpdatedAt = Timing.TotalTime;
        }
        internal static bool RoomAt(Vector2 world, out int index) {
            index = -1; if (Field == null || !Field.Sample(Metres(world)).Inside) return false;
            double smallest = double.PositiveInfinity;
            for (int i = 0; i < hulls.Count; i++) {
                Rectangle r = hulls[i].WorldRect;
                if (world.X < r.X || world.X > r.Right || world.Y > r.Y || world.Y < r.Y - r.Height) continue;
                double area = (double)r.Width * r.Height;
                if (area < smallest) { index = i; smallest = area; }
            }
            return index >= 0;
        }
        internal static ThermalSample Sample(Vector2 world) {
            ThermalSample sample = Field != null ? Field.Sample(Metres(world)) : default;
            if (sample.Inside) return sample;
            float air = WeatherService.Current.Temperature, wind = WeatherService.Current.WindSpeed * WeatherService.SkyAt(world);
            return new ThermalSample(air, air, wind, 0, default, false);
        }
        internal static float AirAt(Vector2 world) => Sample(world).Air;
        internal static float RadiantAt(Vector2 world) => Sample(world).Surface;
        internal static ThermalRoom Measure(int room) => Field.Measure(WeatherMap.Rect(hulls[room].WorldRect));
        internal static void Tile(int tileX, int tileY, Span<ThermalPixel> pixels) {
            if (Field == null || pixels.Length != ThermalFieldWire.Pixels) throw new ArgumentException("Expected a live thermal field and one tile");
            WeatherGrid grid = Field.Grid;
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) {
                Vec2 point = grid.Origin + new Vec2((tileX * 8 + x + .5f) * grid.Cell, (tileY * 8 + y + .5f) * grid.Cell);
                int at = grid.Index(point.X, point.Y); ThermalSample sample = Field.Sample(point);
                ThermalPixelFlags flags = sample.Inside ? ThermalPixelFlags.Inside : 0;
                if (grid.IsSolid(at)) flags |= ThermalPixelFlags.Solid;
                if (grid.Blocked(at)) flags |= ThermalPixelFlags.ClosedGate;
                if (sample.Water > .5f) flags |= ThermalPixelFlags.Water;
                pixels[y * 8 + x] = ThermalPixel.From(sample, flags);
            }
            for (int i = 0; i < sources.Count; i++) if (sources[i].Power > 0) MarkSource(tileX, tileY, sources[i].Origin, pixels);
            for (int i = 0; i < flames.Count; i++) MarkSource(tileX, tileY, flames[i].World, pixels);
        }
        private static void MarkSource(int tileX, int tileY, Vector2 world, Span<ThermalPixel> pixels) {
            WeatherGrid grid = Field.Grid; Vec2 point = Metres(world);
            int x = (int)MathF.Floor((point.X - grid.Origin.X) / grid.Cell) - tileX * 8;
            int y = (int)MathF.Floor((point.Y - grid.Origin.Y) / grid.Cell) - tileY * 8;
            if ((uint)x < 8 && (uint)y < 8) pixels[y * 8 + x] = pixels[y * 8 + x].With(ThermalPixelFlags.Source);
        }
        internal static void BodyHeatAt(Vector2 world, double watts) { Field?.AddHeat(Metres(world), watts); }
        internal static void BeginReceivers() {
            for (int i = 0; i < sources.Count; i++) sources[i].Interception = 0;
            for (int i = 0; i < flames.Count; i++) { Flame flame = flames[i]; flame.Interception = 0; flames[i] = flame; }
        }
        private static double Interception(ReadOnlySpan<Vector2> points, Vector2 origin, float size, float radius, float emission) {
            double peak = 0;
            for (int i = 0; i < points.Length; i++) {
                float distance = Vector2.DistanceSquared(points[i], origin) / 10000;
                if (distance < radius * radius && Visible(points[i], origin)) peak = Math.Max(peak, 1 / (emission * Math.PI * (distance + size * size)));
            }
            return peak * ThermalBody.Area * ThermalBody.Absorbed;
        }
        internal static void RegisterReceiver(ReadOnlySpan<Vector2> points) {
            for (int i = 0; i < sources.Count; i++) {
                Source s = sources[i]; if (s.Power <= 0 || s.Profile.RadiantFraction <= 0) continue;
                s.Interception += Interception(points, s.Origin, s.Profile.Size, s.Profile.Radius, s.Profile.Emission);
            }
            for (int i = 0; i < flames.Count; i++) { Flame flame = flames[i]; flame.Interception += Interception(points, flame.World, flame.Size, flame.Radius, 4); flames[i] = flame; }
        }
        private static double Incident(Source s, ReadOnlySpan<Vector2> points) => s.Power * s.Profile.RadiantFraction * Interception(points, s.Origin, s.Profile.Size, s.Profile.Radius, s.Profile.Emission) / Math.Max(1, s.Interception);
        private static double Incident(in Flame f, ReadOnlySpan<Vector2> points) => f.Watts * Interception(points, f.World, f.Size, f.Radius, 4) / Math.Max(1, f.Interception);
        internal static void CaptureRadiation(ReadOnlySpan<Vector2> points, double watts) {
            if (watts <= 0 || Field == null) return;
            double total = 0;
            for (int i = 0; i < sources.Count; i++) total += Incident(sources[i], points);
            for (int i = 0; i < flames.Count; i++) total += Incident(flames[i], points);
            if (total <= 0) throw new InvalidOperationException("Absorbed source radiation has no registered emitter");
            for (int i = 0; i < sources.Count; i++) {
                Source s = sources[i]; double share = Incident(s, points) / total;
                if (share > 0) Field.CaptureRadiation(Metres(s.Origin), s.Footprint, s.Reach, watts * share);
            }
            for (int i = 0; i < flames.Count; i++) {
                Flame f = flames[i]; double share = Incident(f, points) / total;
                if (share > 0) Field.CaptureRadiation(Metres(f.World), f.Size, f.Radius, watts * share);
            }
        }
        internal static bool AnySource(Vector2 world) {
            for (int i = 0; i < sources.Count; i++) if (Vector2.DistanceSquared(world, sources[i].Origin) / 10000 < sources[i].Profile.Radius * sources[i].Profile.Radius && sources[i].Power > 0) return true;
            return flames.Count > 0;
        }
        internal static float RadiationAt(Vector2 world) {
            float total = 0;
            for (int i = 0; i < sources.Count; i++) {
                Source s = sources[i]; float watts = s.Power;
                if (watts <= 0 || s.Profile.RadiantFraction <= 0) continue;
                float distance = Vector2.DistanceSquared(world, s.Origin) / 10000;
                if (distance >= s.Profile.Radius * s.Profile.Radius || !Visible(world, s.Origin)) continue;
                total += (float)(watts * s.Profile.RadiantFraction / (s.Profile.Emission * Math.PI * (distance + s.Profile.Size * s.Profile.Size)) / Math.Max(1, s.Interception));
            }
            for (int i = 0; i < flames.Count; i++) {
                float distance = Vector2.DistanceSquared(world, flames[i].World) / 10000;
                if (distance >= flames[i].Radius * flames[i].Radius || !Visible(world, flames[i].World)) continue;
                total += (float)(flames[i].Watts / (4 * Math.PI * (distance + flames[i].Size * flames[i].Size)) / Math.Max(1, flames[i].Interception));
            }
            return total;
        }
        private static bool Visible(Vector2 a, Vector2 b) => WeatherService.Grid.SegmentClear(Metres(a), Metres(b));
        internal static void Capture(ThermalTraceCapture capture, double time) {
            if (capture == null || !capture.Active || Field == null) return;
            for (int i = 0; i < hulls.Count; i++) {
                Rectangle r = hulls[i].WorldRect; ThermalRoom room = Measure(i);
                capture.Room(time, i, (r.X + r.Width * .5f) / WeatherService.UnitsPerMetre, (r.Y - r.Height * .5f) / WeatherService.UnitsPerMetre, room, room.Capacity > 0);
            }
            for (int i = 0; i < sources.Count; i++) {
                Source source = sources[i]; Item item = source.Item;
                bool inRoom = RoomAt(source.Origin, out int room); Vector2 position = item.WorldPosition / WeatherService.UnitsPerMetre;
                capture.Source(time, item.ID, item.Prefab.Identifier.Value, source.Profile.Mode, source.ComponentOn, source.ElectricalLoad, source.Voltage,
                    source.MinimumVoltage, source.Profile.Watts, source.Power, source.Profile.Radius, source.Profile.Target, source.Profile.RadiantFraction,
                    inRoom ? room : -1, position.X, position.Y, Math.Clamp(item.Condition / MathF.Max(1, item.MaxCondition), 0, 1), item.InWater, item.ParentInventory != null);
            }
        }
        internal static string Diagnostics() {
            var text = new StringBuilder(512); int active = 0;
            for (int i = 0; i < sources.Count; i++) if (sources[i].Power > 0) active++;
            text.AppendFormat(CultureInfo.InvariantCulture, "Thermal field: cells={0}, cell={1:0.##}m, revision={2}, hulls={3}, configuredSources={4}, effectiveEmitters={5}.\n",
                Field?.Count ?? 0, Field?.Grid.Cell ?? 0, Revision, hulls.Count, sources.Count, active);
            for (int i = 0; i < sources.Count; i++) {
                Source source = sources[i]; ThermalSample local = Sample(source.Origin);
                text.AppendFormat(CultureInfo.InvariantCulture, "  source[{0}] item={1} {2}: mode={3}, on={4}, load={5:0.##}W, voltage={6:0.##}/{7:0.##}V, heat={8:0}W/{9:0}W, air={10:0.0}C, surface={11:0.0}C, indoor={12}, regulated={13}.\n",
                    i, source.Item.ID, source.Item.Prefab.Identifier.Value, source.Profile.Mode, source.ComponentOn, source.ElectricalLoad, source.Voltage, source.MinimumVoltage,
                    source.Power, source.Profile.Watts, local.Air, local.Surface, local.Inside, source.Profile.Regulated);
            }
            return text.ToString();
        }
    }
}
