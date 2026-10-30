using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Barotrauma;
using FarseerPhysics;
using FarseerPhysics.Collision.Shapes;
using Microsoft.Xna.Framework;
using ScpItdMod.Core;
using ScpItdMod.Weather;

namespace ScpItdMod.Diagnostics {
    internal static class GeometryDiagnostics {
        internal static void Write(string folder) {
            var structures = new List<object>();
            foreach (Structure wall in Structure.WallList) {
                var fixtures = new List<object>(); Vector2 offset = wall.WorldPosition - wall.Position;
                if (wall.Bodies != null) foreach (var body in wall.Bodies) foreach (var fixture in body.FixtureList) {
                    var points = new List<Vector2>();
                    if (fixture.Shape is PolygonShape polygon) foreach (Vector2 vertex in polygon.Vertices) points.Add((ConvertUnits.ToDisplayUnits(body.GetWorldPoint(vertex)) + offset) / WeatherService.UnitsPerMetre);
                    else if (fixture.Shape is ChainShape chain) foreach (Vector2 vertex in chain.Vertices) points.Add((ConvertUnits.ToDisplayUnits(body.GetWorldPoint(vertex)) + offset) / WeatherService.UnitsPerMetre);
                    fixtures.Add(new { shape = fixture.Shape.ShapeType.ToString(), sensor = fixture.IsSensor, body.Enabled, category = fixture.CollisionCategories.ToString(), collidesWith = fixture.CollidesWith.ToString(), points });
                }
                structures.Add(new { id = wall.ID, prefab = wall.Prefab.Identifier.ToString(), rect = wall.WorldRect, depth = wall.SpriteDepth, fixtures });
            }
            var hulls = new List<object>(); foreach (Hull hull in Hull.HullList) hulls.Add(new { id = hull.ID, rect = hull.WorldRect, ambient = hull.AmbientLight });
            var gaps = new List<object>(); foreach (Gap gap in Gap.GapList) gaps.Add(new { id = gap.ID, rect = gap.WorldRect, gap.Open, gap.IsRoomToRoom, door = gap.ConnectedDoor?.Item.ID });
            var support = new List<Vec2>(); var runs = new List<int>();
            ScpItdMod.Weather.Material.SnowPack snow = SnowRuntime.Pack;
            if (snow != null) ScpItdMod.Weather.Material.SnowSkin.Build(snow, snow.Grid.Bounds.Left, snow.Grid.Bounds.Right, support, runs);
            File.WriteAllText(Path.Combine(folder, "geometry.json"), JsonSerializer.Serialize(new { units = "fixture and snow points: world metres, Y-up; rectangles: native display units", structures, hulls, gaps, snowSupport = new { points = support, starts = runs, revision = snow?.Revision }, limits = "Read-only native fixture snapshot; snow support is the continuous physical support profile, not a game screenshot" }, new JsonSerializerOptions { IncludeFields = true, WriteIndented = true }));
        }
    }
}
