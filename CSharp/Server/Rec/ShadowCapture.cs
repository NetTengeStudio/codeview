using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using FarseerPhysics;
using FarseerPhysics.Collision.Shapes;
using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;

namespace ScpItdMod.Rec {
    public static class ShadowCapture {
        private static readonly List<ShadowEdge> edges = new List<ShadowEdge>(64);
        public static SceneShadow Collect(MapEntity entity, SceneShadow previous) {
            edges.Clear();
            Vector2 offset = entity.WorldPosition - entity.Position;
            if (entity is Structure wall && wall.CastShadow && !wall.IsPlatform && wall.StairDirection == Direction.None && wall.Bodies != null) {
                foreach (Body body in wall.Bodies) { Add(body,offset,null,entity); }
            } else if (entity is Item item && item.GetComponent<Door>() is Door door) {
                Add(door.Body?.FarseerBody,offset,door,item);
            }
            if (edges.Count==0) { return null; }
            return previous != null && previous.Matches(edges) ? previous : new SceneShadow(edges);
        }
        private static void Add(Body body, Vector2 offset, Door door, MapEntity entity) {
            if (body == null || !body.Enabled) { return; }
            foreach (Fixture fixture in body.FixtureList) {
                if (fixture.IsSensor || fixture.Shape is not PolygonShape polygon) { continue; }
                var vertices=polygon.Vertices;
                for (int i=0; i<vertices.Count; i++) {
                    Vector2 a=ConvertUnits.ToDisplayUnits(body.GetWorldPoint(vertices[i]))+offset;
                    Vector2 b=ConvertUnits.ToDisplayUnits(body.GetWorldPoint(vertices[(i+1)%vertices.Count]))+offset;
                    if (door != null && door.HasWindow) {
                        Rectangle r=entity.WorldRect;
                        float low, high;
                        if (door.IsHorizontal) {
                            low=r.X+door.Window.X*entity.Scale; high=low+door.Window.Width*entity.Scale;
                            if (entity.FlippedX) { float old=low; low=2*(r.X+r.Width*0.5f)-high; high=2*(r.X+r.Width*0.5f)-old; }
                            Split(a,b,a.X,b.X,low,high);
                        } else {
                            high=r.Y+door.Window.Y*entity.Scale; low=high-door.Window.Height*entity.Scale;
                            if (entity.FlippedY) { float old=low; low=2*(r.Y-r.Height*0.5f)-high; high=2*(r.Y-r.Height*0.5f)-old; }
                            Split(a,b,a.Y,b.Y,low,high);
                        }
                    } else { Edge(a,b); }
                }
            }
        }
        private static void Split(Vector2 a, Vector2 b, float av, float bv, float low, float high) {
            float delta=bv-av;
            if (delta==0) { if (av<low || av>high) { Edge(a,b); } return; }
            float t0=(low-av)/delta, t1=(high-av)/delta;
            if (t0>t1) { (t0,t1)=(t1,t0); }
            float enter=Math.Clamp(t0,0,1), exit=Math.Clamp(t1,0,1);
            if (enter>0) { Edge(a,Vector2.Lerp(a,b,enter)); }
            if (exit<1) { Edge(Vector2.Lerp(a,b,exit),b); }
        }
        private static void Edge(Vector2 a, Vector2 b) {
            if (a!=b) { edges.Add(new ShadowEdge(a.X,a.Y,b.X,b.Y)); }
        }
    }
}
