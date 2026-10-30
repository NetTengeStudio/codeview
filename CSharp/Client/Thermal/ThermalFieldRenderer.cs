using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ScpItdMod.Thermal {
    internal static class ThermalFieldRenderer {
        internal static Color Temperature(float temperature) {
            if (temperature < -20) return Color.Lerp(new Color(36, 60, 124), new Color(65, 135, 182), Math.Clamp((temperature + 40) / 20, 0, 1));
            if (temperature < 0) return Color.Lerp(new Color(65, 135, 182), new Color(158, 200, 200), (temperature + 20) / 20);
            if (temperature < 20) return Color.Lerp(new Color(158, 200, 200), new Color(207, 195, 139), temperature / 20);
            return Color.Lerp(new Color(207, 195, 139), new Color(208, 83, 53), Math.Clamp((temperature - 20) / 20, 0, 1));
        }
        internal static int Draw(SpriteBatch batch, Texture2D pixel, ThermalFieldReplica replica, double time, Vector2 origin, Vector2 axisX, Vector2 axisY, Rectangle viewport, float opacity, bool surfaces) {
            float unit = axisX.Length(); if (unit <= 0 || opacity <= 0) return 0;
            float rotation = MathF.Atan2(axisX.Y, axisX.X);
            int tiles = 0;
            for (int slot = 0; slot < ThermalFieldReplica.Slots; slot++) {
                if (!replica.TryTile(slot, time, out ThermalTileHeader header, out ReadOnlySpan<ThermalPixel> pixels)) continue;
                tiles++;
                float size = header.Cell * unit;
                for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) {
                    ThermalPixel cell = pixels[y * 8 + x];
                    Vector2 world = new Vector2(header.Origin.X + (header.X * 8 + x + .5f) * header.Cell, header.Origin.Y + (header.Y * 8 + y + .5f) * header.Cell);
                    Vector2 centre = origin + axisX * world.X + axisY * world.Y;
                    if (centre.X < viewport.Left - size || centre.X > viewport.Right + size || centre.Y < viewport.Top - size || centre.Y > viewport.Bottom + size) continue;
                    bool solid = (cell.Flags & (ThermalPixelFlags.Solid | ThermalPixelFlags.ClosedGate)) != 0;
                    Color colour = solid ? new Color(38, 45, 47) : Temperature(surfaces ? cell.SurfaceTemperature : cell.AirTemperature);
                    batch.Draw(pixel, centre, null, colour * opacity, rotation, new Vector2(.5f), new Vector2(size), SpriteEffects.None, .99f);
                    if ((cell.Flags & ThermalPixelFlags.Source) != 0) {
                        float marker = MathF.Max(3, MathF.Min(7, size * .25f));
                        batch.Draw(pixel, centre, null, Color.Black * opacity, rotation, new Vector2(.5f), new Vector2(marker + 2), SpriteEffects.None, .99f);
                        batch.Draw(pixel, centre, null, new Color(248, 223, 145) * opacity, rotation, new Vector2(.5f), new Vector2(marker), SpriteEffects.None, .99f);
                    }
                    if (surfaces || solid || cell.Strength < 4 || (x & 1) != 0 || (y & 1) != 0) continue;
                    float angle = cell.Angle * (2 * MathF.PI / 256);
                    Vector2 direction = axisX * MathF.Cos(angle) + axisY * MathF.Sin(angle);
                    direction /= unit;
                    float reach = MathF.Max(2, size * .65f) * MathF.Min(1, cell.Strength / 64f);
                    Vector2 start = centre - direction * reach * .5f, end = centre + direction * reach * .5f;
                    Vector2 side = new Vector2(-direction.Y, direction.X) * MathF.Min(3, reach * .4f);
                    Color ink = new Color(232, 235, 221) * MathF.Min(1, opacity + .2f);
                    Line(batch, pixel, start, end, ink);
                    Line(batch, pixel, end, end - direction * MathF.Min(4, reach * .5f) + side, ink);
                    Line(batch, pixel, end, end - direction * MathF.Min(4, reach * .5f) - side, ink);
                }
            }
            return tiles;
        }
        private static void Line(SpriteBatch batch, Texture2D pixel, Vector2 from, Vector2 to, Color colour) {
            Vector2 delta = to - from; float length = delta.Length(); if (length <= 0) return;
            batch.Draw(pixel, from, null, colour, MathF.Atan2(delta.Y, delta.X), new Vector2(0, .5f), new Vector2(length, 1), SpriteEffects.None, .99f);
        }
    }
}
