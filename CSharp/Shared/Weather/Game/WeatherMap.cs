using System;
using Barotrauma;
using Voronoi2;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using FarseerPhysics.Collision.Shapes;
using ScpItdMod.Core;

namespace ScpItdMod.Weather {
    internal static class WeatherMap {
        internal static bool IsDirty { get; private set; }
        internal static bool RebuildDue => IsDirty;
        internal static uint Revision { get; private set; }
        internal static ulong Fingerprint { get; private set; }
        private static Microsoft.Xna.Framework.Vector2 anchor;
        internal static void CheckMoved() {
            if (Submarine.MainSub != null && Microsoft.Xna.Framework.Vector2.DistanceSquared(Submarine.MainSub.WorldPosition, anchor) > 100 * 100) IsDirty = true;
        }
        internal static void Dirty() { if (WeatherService.Active) IsDirty = true; }
        private static readonly System.Collections.Generic.List<Gap> gates = new System.Collections.Generic.List<Gap>();
        internal static void Reset() { IsDirty = true; Revision = 0; Fingerprint = 0; gates.Clear(); }
        internal static bool GateOpen(Gap gap) => gap.Open > .1f;
        internal static WeatherRect Rect(Microsoft.Xna.Framework.Rectangle r) => new WeatherRect(r.X / 100f, (r.Y - r.Height) / 100f, (r.X + r.Width) / 100f, r.Y / 100f);
        private static Vec2[] outline = new Vec2[32];
        private static bool Physical(Body body) => body.Enabled && (body.CollisionCategories & (Physics.CollisionWall | Physics.CollisionPlatform | Physics.CollisionStairs)) != 0;
        private static void WallGeometry(Structure wall, WeatherGrid grid, ref float left, ref float right, ref float bottom, ref float top) {
            if (wall.Bodies == null) return;
            Microsoft.Xna.Framework.Vector2 offset = wall.WorldPosition - wall.Position;
            for (int b = 0; b < wall.Bodies.Count; b++) {
                Body body = wall.Bodies[b]; if (!Physical(body)) continue;
                for (int f = 0; f < body.FixtureList.Count; f++) {
                    Fixture fixture = body.FixtureList[f];
                    if (fixture.IsSensor || fixture.Shape is not PolygonShape polygon || polygon.Vertices.Count < 3) continue;
                    int count = polygon.Vertices.Count; if (outline.Length < count) outline = new Vec2[count * 2];
                    float minX = float.PositiveInfinity, maxX = float.NegativeInfinity, minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
                    for (int i = 0; i < count; i++) {
                        Microsoft.Xna.Framework.Vector2 p = (ConvertUnits.ToDisplayUnits(body.GetWorldPoint(polygon.Vertices[i])) + offset) / WeatherService.UnitsPerMetre;
                        outline[i] = new Vec2(p.X, p.Y);
                        minX = MathF.Min(minX, p.X); maxX = MathF.Max(maxX, p.X); minY = MathF.Min(minY, p.Y); maxY = MathF.Max(maxY, p.Y);
                    }
                    left = MathF.Min(left, minX); right = MathF.Max(right, maxX); bottom = MathF.Min(bottom, minY); top = MathF.Max(top, maxY);
                    if (grid != null && new WeatherRect(minX, minY, maxX, maxY).Overlaps(grid.Bounds)) {
                        grid.PaintConvex(outline.AsSpan(0, count), wall.CastShadow, true);
                    }
                }
            }
        }
        private static void PaintTerrain(WeatherGrid grid) {
            if (Level.Loaded == null) return;
            WeatherRect zone = grid.Bounds;
            foreach (VoronoiCell cell in Level.Loaded.GetAllCells()) {
                if (cell.CellType != CellType.Solid || cell.BodyVertices.Count < 3) continue;
                int n = cell.BodyVertices.Count; if (outline.Length < n) outline = new Vec2[n * 2];
                float left = float.PositiveInfinity, right = float.NegativeInfinity, bottom = float.PositiveInfinity, top = float.NegativeInfinity;
                for (int i = 0; i < n; i++) {
                    Microsoft.Xna.Framework.Vector2 v = (cell.BodyVertices[i] + cell.Translation) / WeatherService.UnitsPerMetre;
                    outline[i] = new Vec2(v.X, v.Y);
                    left = MathF.Min(left, v.X); right = MathF.Max(right, v.X); bottom = MathF.Min(bottom, v.Y); top = MathF.Max(top, v.Y);
                }
                if (right < zone.Left || left > zone.Right || top < zone.Bottom || bottom > zone.Top) continue;
                grid.PaintConvex(new ReadOnlySpan<Vec2>(outline, 0, n));
            }
        }
        internal static void Rebuild() {
            if (!WeatherService.Active || Submarine.MainSub == null) return;
            WeatherConfig config = WeatherService.Config; anchor = Submarine.MainSub.WorldPosition;
            float left = float.PositiveInfinity, right = float.NegativeInfinity, bottom = float.PositiveInfinity, top = float.NegativeInfinity;
            foreach (Structure wall in Structure.WallList) {
                if (wall.Removed || !wall.HasBody || wall.Submarine != Submarine.MainSub) continue;
                WallGeometry(wall, null, ref left, ref right, ref bottom, ref top);
            }
            foreach (Hull hull in Hull.HullList) {
                if (hull.Removed || hull.Submarine != Submarine.MainSub) continue;
                WeatherRect r = Rect(hull.WorldRect); left = MathF.Min(left, r.Left); right = MathF.Max(right, r.Right); bottom = MathF.Min(bottom, r.Bottom); top = MathF.Max(top, r.Top);
            }
            if (!float.IsFinite(left)) throw new InvalidOperationException("Vostok has no station geometry");
            float margin = config.MarginMetres;
            var grid = new WeatherGrid(new WeatherRect(left - margin, bottom - margin, right + margin, top + margin), WeatherGrid.FitCell(new WeatherRect(left - margin, bottom - margin, right + margin, top + margin), config.CellMetres, config.MaximumCells), config.MaximumCells);
            foreach (Hull hull in Hull.HullList) if (!hull.Removed && hull.Submarine == Submarine.MainSub) grid.Paint(Rect(hull.WorldRect), false, true);
            foreach (Structure wall in Structure.WallList) {
                if (wall.Removed || !wall.HasBody) continue;
                WallGeometry(wall, grid, ref left, ref right, ref bottom, ref top);
            }
            PaintTerrain(grid);
            gates.Clear();
            foreach (Gap gap in Gap.GapList) {
                if (gap.Removed || gap.Submarine != Submarine.MainSub || gap.ConnectedDoor == null) continue;
                grid.AddGate(Rect(gap.WorldRect)); gates.Add(gap);
            }
            grid.Seal();
            for (int i = 0; i < gates.Count; i++) grid.SetGate(i, GateOpen(gates[i]));
            grid.RefreshGates(); grid.SetPenetration(WeatherGrid.PenetrationLength(WeatherService.Current.WindSpeed));
            ulong hash = 14695981039346656037;
            for (int i = 0; i < grid.Cells; i++) hash = (hash ^ (ulong)(grid.IsSolid(i) ? 1 : grid.IsInterior(i) ? 2 : 0)) * 1099511628211;
            hash = (hash ^ BitConverter.SingleToUInt32Bits(grid.Origin.X)) * 1099511628211;
            hash = (hash ^ BitConverter.SingleToUInt32Bits(grid.Origin.Y)) * 1099511628211;
            for (int i = 0; i < grid.SurfaceCount; i++) {
                hash = (hash ^ unchecked((uint)(int)MathF.Round(grid.SurfaceY(i) * 1000))) * 1099511628211;
                hash = (hash ^ (float.IsFinite(grid.Ceiling(i)) ? unchecked((uint)(int)MathF.Round(grid.Ceiling(i) * 1000)) : uint.MaxValue)) * 1099511628211;
            }
            Fingerprint = hash; WeatherService.Grid = grid; SnowRuntime.Bind(config, grid); Revision++; IsDirty = false;
            WeatherNetwork.SnowChanged();
        }
        internal static void GateChanged(Gap gap) {
            WeatherGrid grid = WeatherService.Grid; if (grid == null) return;
            int gate = gates.IndexOf(gap); if (gate < 0) return;
            bool was = grid.GateOpened(gate); grid.SetGate(gate, GateOpen(gap));
            // The snow that leaned on the door has lost its wall: it is woken, so whatever its cohesion cannot hold flows into the doorway under its own weight
            // instead of standing until something else touches it.
            if (was == GateOpen(gap) || SnowRuntime.Pack == null) return;
            WeatherRect r = Rect(gap.WorldRect);
            SnowRuntime.Pack.Material.Disturb(new Vec2((r.Left + r.Right) * .5f, (r.Bottom + r.Top) * .5f), MathF.Max(r.Right - r.Left, r.Top - r.Bottom) * .5f + 1.5f, 6);
        }
        internal static void Refresh(in WeatherState weather) {
            WeatherGrid grid = WeatherService.Grid; if (grid == null) return;
            grid.SetPenetration(WeatherGrid.PenetrationLength(weather.WindSpeed));
            grid.RefreshGates();
        }
    }
}
