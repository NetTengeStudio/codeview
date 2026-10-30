using System;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;
using NVector2 = ScpItdMod.Core.Vec2;

namespace ScpItdMod.Pool {
    public static class PoolDraw {
        private static readonly Rectangle[] sources = Sources();
        private static Texture2D balls, cue;
        private static Rectangle[] Sources() { var result = new Rectangle[16]; for (int i = 0; i < result.Length; i++) { result[i] = new Rectangle(i % 4 * 132 + 2, i / 4 * 132 + 2, 128, 128); } return result; }
        public static void Clear() { balls = cue = null; }
        public static Vector2 Xna(NVector2 p) => new Vector2(p.X, p.Y);
        public static void Balls(SpriteBatch batch, PoolReplica view, in Surface surface, float depth, Texture2D sphereAtlas = null) {
            Table table = view.Table; float diameter = 2 * table.Radius * surface.PixelsPerMeter(table);
            float verticalDiameter = surface.IsWorld ? diameter : 2 * table.Radius * table.UiScale.Y * surface.Scale;
            balls ??= TextureStore.File("Content/Items/Pool/balls.png"); if (balls == null) { return; }
            Span<Ball> sampled = stackalloc Ball[16]; Span<int> order = stackalloc int[16]; int count = 0;
            for (int i = 0; i < 16; i++) {
                Ball b = sampled[i] = view.Sample(i, Timing.TotalTime + Timing.Alpha * Timing.Step); if (b.Down) { continue; }
                int slot = count;
                if (surface.IsWorld) { while (slot > 0 && sampled[order[slot - 1]].Position.Y > b.Position.Y) { order[slot] = order[slot - 1]; slot--; } }
                order[slot] = i; count++;
            }
            for (int slot = 0; slot < count; slot++) {
                int i = order[slot]; Ball b = sampled[i];
                float layer = surface.IsWorld ? MathF.Max(0, depth - b.Position.Y / table.Width * 0.00001f) : depth;
                batch.Draw(sphereAtlas ?? balls, Xna(surface.Ball(table, b.Position)), sphereAtlas == null ? sources[i] : PoolSphereRenderer.Source(i), Color.White, surface.Angle, new Vector2(64, 64), new Vector2(diameter, verticalDiameter) / 128 * (sphereAtlas == null ? 1 : PoolSphereRenderer.ProjectionExtent / 2), SpriteEffects.None, layer);
            }
        }
        public static void Table(SpriteBatch batch, in Surface surface) {
            Texture2D table = TextureStore.File(surface.IsWorld ? PoolModule.Layout.WorldArt : PoolModule.Layout.UiArt); if (table == null) { return; }
            batch.Draw(table, Xna(surface.Center), null, Color.White, surface.Angle, new Vector2(table.Width / 2f, table.Height / 2f), surface.Scale, SpriteEffects.None, 0);
        }
        public static void Aim(SpriteBatch batch, PoolReplica view, in Surface surface, NVector2 direction, float power, float depth = 0) {
            if (view.Current[0].Down || direction.LengthSquared() < 0.5f) { return; }
            Table table = view.Table; NVector2 from = surface.Ball(table, view.Current[0].Position);
            if (!surface.IsWorld) {
                NVector2 end = surface.Ball(table, PoolAim.Trace(table, view.Current, direction));
                GUI.DrawLine(batch, Xna(from), Xna(end), Color.White * 0.45f, depth: depth, width: 1);
            }
            cue ??= TextureStore.File("Content/Items/Pool/cue.png"); if (cue == null) { return; }
            NVector2 projected = surface.Vector(table, -direction), original = table.CueButt - table.CueTip;
            float pixels = projected.Length(); if (pixels < 1e-6f) { return; }
            NVector2 axis = projected / pixels;
            float angle = MathF.Atan2(axis.Y, axis.X) - MathF.Atan2(original.Y, original.X);
            NVector2 tip = from + axis * ((table.Radius + 0.02f + power * 0.12f) * pixels);
            float sourceLength = original.Length();
            Vector2 scale = new Vector2(table.CueLength * pixels / sourceLength, table.CueLength * surface.PixelsPerMeter(table) / sourceLength);
            batch.Draw(cue, Xna(tip), null, Color.White, angle, Xna(table.CueTip), scale, SpriteEffects.None, depth);
        }
        public static void Spin(SpriteBatch batch, Rectangle rect, NVector2 tip) {
            balls ??= TextureStore.File("Content/Items/Pool/balls.png"); if (balls == null) { return; }
            int size = Math.Min(rect.Width, rect.Height); var ball = new Rectangle(rect.Center.X - size / 2, rect.Center.Y - size / 2, size, size);
            batch.Draw(balls, ball, sources[0], Color.White);
            Vector2 point = new Vector2(rect.Center.X + tip.X * size / 2, rect.Center.Y - tip.Y * size / 2);
            GUI.DrawLine(batch, point - Vector2.UnitX * 4, point + Vector2.UnitX * 4, Color.Red, width: 2);
            GUI.DrawLine(batch, point - Vector2.UnitY * 4, point + Vector2.UnitY * 4, Color.Red, width: 2);
        }
    }
}
