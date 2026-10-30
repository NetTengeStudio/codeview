using System;
using System.Collections.Generic;
using ScpItdMod.Core;
using ScpItdMod.Weather;

namespace ScpItdMod.Thermal {
    public readonly struct ThermalSample {
        public readonly float Air, Surface, Draft, Water;
        public readonly Vec2 HeatFlow;
        public readonly bool Inside;
        public ThermalSample(float air, float surface, float draft, float water, Vec2 flow, bool inside) {
            Air = air; Surface = surface; Draft = draft; Water = water; HeatFlow = flow; Inside = inside;
        }
    }
    public sealed class ThermalField {
        private const double AirCapacity = 1.2 * 1005, WaterCapacity = 1000 * 4182;
        private readonly WeatherGrid grid;
        private readonly ThermalCatalog config;
        private readonly int[] nodes, cells;
        private readonly float[] exposed, water, pendingWater, draft;
        private readonly BoundaryFace[] boundaries;
        private readonly struct BoundaryFace {
            internal readonly int Node, Neighbour;
            internal BoundaryFace(int node, int neighbour) { Node = node; Neighbour = neighbour; }
        }
        private readonly double[] pendingHeat, pendingSurfaceHeat;
        private readonly Vec2[] flow;
        private readonly bool[] partition;
        private readonly ThermalRooms model;
        private readonly double volume, face;
        private float outside;
        public int Count => cells.Length;
        public WeatherGrid Grid => grid;
        public double Energy => model.Energy;
        public double SourceJoules => model.SourceJoules;
        public double BoundaryJoules => model.BoundaryJoules;
        public double FluidExchangeJoules { get; private set; }
        public ThermalField(WeatherGrid grid, ThermalCatalog config, float outside, ThermalField previous = null, Vec2 translation = default) {
            this.grid = grid ?? throw new ArgumentNullException(nameof(grid));
            this.config = config ?? throw new ArgumentNullException(nameof(config));
            if (!float.IsFinite(outside)) throw new ArgumentOutOfRangeException(nameof(outside));
            this.outside = outside;
            volume = grid.Cell * grid.Cell * config.Depth; face = grid.Cell * config.Depth;
            nodes = new int[grid.Cells]; Array.Fill(nodes, -1);
            var active = new List<int>();
            for (int i = 0; i < nodes.Length; i++) if (grid.IsInterior(i) && !grid.IsSolid(i)) { nodes[i] = active.Count; active.Add(i); }
            cells = active.ToArray();
            exposed = new float[Count]; water = new float[Count]; pendingWater = new float[Count];
            draft = new float[Count]; pendingHeat = new double[Count]; pendingSurfaceHeat = new double[Count]; flow = new Vec2[Count];
            var links = new List<ThermalLink>(); var kinds = new List<bool>();
            var openings = new List<BoundaryFace>();
            var surfaces = new double[Count];
            for (int i = 0; i < Count; i++) {
                surfaces[i] = 2 * grid.Cell * grid.Cell;
                exposed[i] = (float)surfaces[i];
                int cell = cells[i], x = cell % grid.Width, y = cell / grid.Width;
                for (int direction = 0; direction < 4; direction++) {
                    int dx = direction == 0 ? -1 : direction == 1 ? 1 : 0, dy = direction == 2 ? -1 : direction == 3 ? 1 : 0;
                    int other = At(x + dx, y + dy), neighbour = other >= 0 ? nodes[other] : -1;
                    if (neighbour >= 0) {
                        if (i < neighbour) { links.Add(new ThermalLink { A = i, B = neighbour }); kinds.Add(false); }
                        continue;
                    }
                    if (other < 0 || !grid.IsSolid(other)) { openings.Add(new BoundaryFace(i, other)); continue; }
                    surfaces[i] += face;
                    int nx = x + dx, ny = y + dy;
                    while (other >= 0 && grid.IsSolid(other)) { nx += dx; ny += dy; other = At(nx, ny); }
                    neighbour = other >= 0 ? nodes[other] : -1;
                    if (neighbour < 0) exposed[i] += (float)face;
                    else if (i < neighbour) { links.Add(new ThermalLink { A = i, B = neighbour, Fabric = true, Conductance = face * config.WallU }); kinds.Add(true); }
                }
            }
            model = new ThermalRooms(Count, links.Count); partition = kinds.ToArray();
            boundaries = openings.ToArray();
            links.CopyTo(model.Links);
            for (int i = 0; i < Count; i++) {
                Vec2 point = grid.CellCenter(cells[i]);
                ThermalSample old = previous != null ? previous.Sample(point - translation) : default;
                model.Rooms[i] = new ThermalRoom { Capacity = volume * AirCapacity,
                    Temperature = old.Inside ? old.Air : config.InitialIndoor, SurfaceTemperature = old.Inside ? old.Surface : config.InitialIndoor,
                    FabricCapacity = surfaces[i] * config.FabricCapacity, FabricConductance = surfaces[i] * 3,
                    FabricOutsideConductance = exposed[i] / (1 / config.WallU - 1.0 / 3) };
                if (old.Inside) { pendingWater[i] = old.Water; ChangeWater(i, old.Water, old.Air); }
            }
            if (previous != null) for (int i = 0; i < previous.Count; i++) {
                int target = Node(previous.Position(i) + translation);
                if (target >= 0) { pendingHeat[target] += previous.pendingHeat[i]; pendingSurfaceHeat[target] += previous.pendingSurfaceHeat[i]; }
            }
        }
        private int At(int x, int y) => (uint)x < grid.Width && (uint)y < grid.Height ? y * grid.Width + x : -1;
        private int Node(Vec2 point) { int cell = grid.Index(point.X, point.Y); return cell >= 0 ? nodes[cell] : -1; }
        public Vec2 Position(int node) => grid.CellCenter(cells[node]);
        public Vec2 Emitter(Vec2 point, float radius) {
            int cell = grid.Index(point.X, point.Y);
            if (cell < 0 || !grid.IsSolid(cell)) return point;
            Bounds(point, radius + grid.Cell, out int x0, out int y0, out int x1, out int y1);
            float nearest = float.PositiveInfinity; Vec2 result = point;
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) {
                int i = nodes[y * grid.Width + x]; if (i < 0) continue;
                Vec2 candidate = Position(i); float distance = Vec2.DistanceSquared(point, candidate);
                if (distance < nearest) { nearest = distance; result = candidate; }
            }
            return result;
        }
        public ThermalSample Sample(Vec2 point) {
            int i = Node(point);
            if (i < 0) return new ThermalSample(outside, outside, 0, 0, default, false);
            ref ThermalRoom node = ref model.Rooms[i];
            return new ThermalSample((float)node.Temperature, (float)node.SurfaceTemperature, draft[i], water[i], flow[i], true);
        }
        private void Bounds(Vec2 point, float radius, out int x0, out int y0, out int x1, out int y1) {
            x0 = Math.Max(0, (int)MathF.Floor((point.X - radius - grid.Origin.X) / grid.Cell));
            y0 = Math.Max(0, (int)MathF.Floor((point.Y - radius - grid.Origin.Y) / grid.Cell));
            x1 = Math.Min(grid.Width - 1, (int)MathF.Floor((point.X + radius - grid.Origin.X) / grid.Cell));
            y1 = Math.Min(grid.Height - 1, (int)MathF.Floor((point.Y + radius - grid.Origin.Y) / grid.Cell));
        }
        public bool AddHeat(Vec2 point, double watts) {
            if (!double.IsFinite(watts)) throw new ArgumentOutOfRangeException(nameof(watts));
            int i = Node(point); if (i < 0) return false;
            pendingHeat[i] += watts; return true;
        }
        public bool Emit(Vec2 point, float radius, double watts, float radiantFraction, float radiantRadius = 0) {
            if (!float.IsFinite(radius) || radius <= 0 || !double.IsFinite(watts) || watts < 0 || !float.IsFinite(radiantFraction) || radiantFraction < 0 || radiantFraction > 1) throw new ArgumentOutOfRangeException(nameof(watts));
            if (!float.IsFinite(radiantRadius) || radiantRadius < 0) throw new ArgumentOutOfRangeException(nameof(radiantRadius));
            if (Node(point) < 0) return false;
            double radiant = watts * radiantFraction;
            if (radiantRadius >= radius && radiant > 0 && DepositRadiation(point, radius, radiantRadius, radiant)) {
                watts -= radiant; radiantFraction = 0;
            }
            Bounds(point, radius, out int x0, out int y0, out int x1, out int y1);
            int count = 0;
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) {
                int i = nodes[y * grid.Width + x];
                if (i >= 0 && Vec2.DistanceSquared(Position(i), point) <= radius * radius && grid.SegmentClear(point, Position(i))) count++;
            }
            if (count == 0) {
                int i = Node(point); if (i < 0) return false;
                model.Rooms[i].Heat += watts * (1 - radiantFraction); model.Rooms[i].SurfaceHeat += watts * radiantFraction;
                return true;
            }
            double each = watts / count;
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) {
                int i = nodes[y * grid.Width + x];
                if (i < 0 || Vec2.DistanceSquared(Position(i), point) > radius * radius || !grid.SegmentClear(point, Position(i))) continue;
                model.Rooms[i].Heat += each * (1 - radiantFraction); model.Rooms[i].SurfaceHeat += each * radiantFraction;
            }
            return true;
        }
        public bool CaptureRadiation(Vec2 point, float size, float radius, double watts) {
            if (!float.IsFinite(size) || size <= 0 || !float.IsFinite(radius) || radius < size || !double.IsFinite(watts) || watts < 0) throw new ArgumentOutOfRangeException(nameof(watts));
            return Node(point) >= 0 && DepositRadiation(point, size, radius, -watts, true);
        }
        private bool DepositRadiation(Vec2 point, float size, float radius, double watts, bool queued = false) {
            Bounds(point, radius, out int x0, out int y0, out int x1, out int y1);
            double weights = 0;
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) {
                int i = nodes[y * grid.Width + x]; if (i < 0) continue;
                float distance = Vec2.DistanceSquared(Position(i), point);
                if (distance <= radius * radius && grid.SegmentClear(point, Position(i))) weights += model.Rooms[i].FabricConductance / (distance + size * size);
            }
            if (weights <= 0) return false;
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) {
                int i = nodes[y * grid.Width + x]; if (i < 0) continue;
                float distance = Vec2.DistanceSquared(Position(i), point);
                if (distance <= radius * radius && grid.SegmentClear(point, Position(i))) {
                    double heat = watts / weights * model.Rooms[i].FabricConductance / (distance + size * size);
                    if (queued) pendingSurfaceHeat[i] += heat; else model.Rooms[i].SurfaceHeat += heat;
                }
            }
            return true;
        }
        public void Begin(float ambient) {
            if (!float.IsFinite(ambient)) throw new ArgumentOutOfRangeException(nameof(ambient));
            outside = ambient; FluidExchangeJoules = 0;
            Array.Clear(pendingWater);
            for (int i = 0; i < Count; i++) { model.Rooms[i].Heat = pendingHeat[i]; model.Rooms[i].SurfaceHeat = pendingSurfaceHeat[i]; pendingHeat[i] = pendingSurfaceHeat[i] = 0; }
        }
        public void Flood(in WeatherRect area, float surface) {
            if (!float.IsFinite(surface)) throw new ArgumentOutOfRangeException(nameof(surface));
            Vec2 centre = new Vec2((area.Left + area.Right) * .5f, (area.Bottom + area.Top) * .5f);
            Bounds(centre, MathF.Max(area.Right - area.Left, area.Top - area.Bottom) * .5f, out int x0, out int y0, out int x1, out int y1);
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) {
                int i = nodes[y * grid.Width + x]; if (i < 0 || !area.Contains(Position(i).X, Position(i).Y)) continue;
                float bottom = grid.Origin.Y + y * grid.Cell;
                pendingWater[i] = MathF.Max(pendingWater[i], Math.Clamp((surface - bottom) / grid.Cell, 0, 1));
            }
        }
        private void ChangeWater(int i, float fraction, float inlet) {
            ref ThermalRoom cell = ref model.Rooms[i];
            double oldCapacity = cell.Capacity, oldEnergy = oldCapacity * cell.Temperature;
            double nextCapacity = volume * (AirCapacity * (1 - fraction) + WaterCapacity * fraction);
            double removed = volume * (fraction > water[i] ? AirCapacity : WaterCapacity) * Math.Abs(fraction - water[i]);
            double added = volume * (fraction > water[i] ? WaterCapacity : AirCapacity) * Math.Abs(fraction - water[i]);
            double nextEnergy = oldEnergy - removed * cell.Temperature + added * inlet;
            cell.Capacity = nextCapacity; cell.Temperature = nextEnergy / nextCapacity; water[i] = fraction;
            FluidExchangeJoules += nextEnergy - oldEnergy;
        }
        public void Step(float wind, float dt) {
            if (!float.IsFinite(wind) || wind < 0 || wind > 200 || !float.IsFinite(dt) || dt <= 0 || dt > 1) throw new ArgumentOutOfRangeException(nameof(wind));
            for (int i = 0; i < Count; i++) {
                if (pendingWater[i] != water[i]) ChangeWater(i, pendingWater[i], pendingWater[i] > water[i] ? 0 : ReplacementAir(i));
                ref ThermalRoom r = ref model.Rooms[i];
                float localWind = wind * grid.Exposure(cells[i]);
                draft[i] = localWind * (1 - water[i]); flow[i] = default;
                r.OutsideConductance = volume * AirCapacity * config.Leakage / 3600 * (1 - water[i]);
                r.FabricOutsideConductance = exposed[i] / (1 / config.WallU - 1.0 / 3);
            }
            for (int i = 0; i < boundaries.Length; i++) {
                BoundaryFace opening = boundaries[i]; int node = opening.Node;
                ref ThermalRoom r = ref model.Rooms[node];
                if (grid.Blocked(cells[node]) || grid.Blocked(opening.Neighbour)) r.FabricOutsideConductance += face / (1 / config.WallU - 1.0 / 3);
                else r.OutsideConductance += ThermalRooms.Ventilation((float)face, 1, wind * grid.Exposure(cells[node]), (float)(r.Temperature - outside), grid.Cell) * (1 - water[node]);
            }
            for (int i = 0; i < model.Links.Length; i++) {
                if (partition[i]) continue;
                ref ThermalLink link = ref model.Links[i];
                int a = link.A, b = link.B;
                bool closed = grid.Blocked(cells[a]) || grid.Blocked(cells[b]);
                link.Fabric = closed;
                if (closed) { link.Conductance = face * config.WallU; continue; }
                double delta = model.Rooms[a].Temperature - model.Rooms[b].Temperature;
                bool vertical = cells[b] - cells[a] == grid.Width;
                float wet = MathF.Min(water[a], water[b]);
                if (MathF.Abs(water[a] - water[b]) > .5f) { link.Conductance = face * 10; continue; }
                double densityCapacity = AirCapacity * (1 - wet) + WaterCapacity * wet;
                double mixing = config.Mixing * (1 - wet) + config.WaterMixing * wet;
                double speed = wind * (grid.Exposure(cells[a]) + grid.Exposure(cells[b])) * .5 * config.WindMixing;
                if (vertical && delta > 0) speed += config.Buoyancy * Math.Sqrt(9.81 * grid.Cell * delta / Math.Max(180, model.Rooms[b].Temperature + 273.15));
                link.Conductance = densityCapacity * face * (mixing / grid.Cell + speed);
                float motion = (float)speed * (1 - wet);
                draft[a] = MathF.Max(draft[a], motion); draft[b] = MathF.Max(draft[b], motion);
            }
            model.Step(outside, dt);
            for (int i = 0; i < model.Links.Length; i++) {
                ThermalLink link = model.Links[i]; if (link.Fabric) continue;
                Vec2 direction = Position(link.B) - Position(link.A);
                float flux = (float)(link.Conductance * (model.Rooms[link.A].Temperature - model.Rooms[link.B].Temperature) / face);
                Vec2 vector = direction * (flux / grid.Cell);
                flow[link.A] += vector; flow[link.B] += vector;
            }
        }
        private float ReplacementAir(int node) {
            int cell = cells[node], x = cell % grid.Width, y = cell / grid.Width;
            double total = 0; int count = 0;
            for (int side = 0; side < 4; side++) {
                int at = At(x + (side == 0 ? -1 : side == 1 ? 1 : 0), y + (side == 2 ? -1 : side == 3 ? 1 : 0));
                int i = at >= 0 ? nodes[at] : -1;
                if (i < 0 || water[i] >= .5f || grid.Blocked(at)) continue;
                total += model.Rooms[i].Temperature; count++;
            }
            return count > 0 ? (float)(total / count) : (float)model.Rooms[node].Temperature;
        }
        public ThermalRoom Measure(in WeatherRect area) {
            double energy = 0, surfaceEnergy = 0; ThermalRoom total = default;
            for (int i = 0; i < Count; i++) {
                Vec2 at = Position(i); if (!area.Contains(at.X, at.Y)) continue;
                ThermalRoom r = model.Rooms[i];
                energy += r.Capacity * r.Temperature; surfaceEnergy += r.FabricCapacity * r.SurfaceTemperature;
                total.Capacity += r.Capacity; total.FabricCapacity += r.FabricCapacity;
                total.Heat += r.Heat + r.SurfaceHeat;
            }
            total.Temperature = total.Capacity > 0 ? energy / total.Capacity : outside;
            total.SurfaceTemperature = total.FabricCapacity > 0 ? surfaceEnergy / total.FabricCapacity : outside;
            return total;
        }
    }
}
