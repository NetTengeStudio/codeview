using System;
using Vector2 = ScpItdMod.Core.Vec2;

namespace ScpItdMod.Pool {
    public readonly struct Surface {
        public readonly Vector2 Center;
        public readonly float Scale, Angle;
        public readonly bool IsWorld;
        private readonly float cosine, sine;
        public Surface(Vector2 center, float scale, float angle = 0) : this(center, scale, angle, false) { }
        private Surface(Vector2 center, float scale, float angle, bool world) {
            if (!float.IsFinite(scale) || scale <= 0 || !float.IsFinite(angle)) { throw new ArgumentOutOfRangeException(nameof(scale)); }
            Center = center; Scale = scale; Angle = angle; cosine = MathF.Cos(angle); sine = MathF.Sin(angle); IsWorld = world;
        }
        public static Surface World(Table table, Vector2 anchor, float scale, float angle) {
            Vector2 offset = (table.WorldImage / 2 - table.WorldOrigin) * scale;
            float cosine = MathF.Cos(angle), sine = MathF.Sin(angle);
            return new Surface(anchor + new Vector2(offset.X * cosine - offset.Y * sine, offset.X * sine + offset.Y * cosine), scale, angle, true);
        }
        public Vector2 Pixel(Vector2 point) { point *= Scale; return Center + new Vector2(point.X * cosine - point.Y * sine, point.X * sine + point.Y * cosine); }
        public Vector2 Inverse(Vector2 point) { point -= Center; return new Vector2(point.X * cosine + point.Y * sine, point.Y * cosine - point.X * sine) / Scale; }
        public Vector2 Ball(Table table, Vector2 point) => Pixel(IsWorld ? table.WorldBase + table.WorldX * point.X + table.WorldY * point.Y : table.UiPixel(point) - table.UiImage / 2);
        public Vector2 Position(Table table, Vector2 point) {
            if (!IsWorld) { return table.UiPosition(Inverse(point) + table.UiImage / 2); }
            Vector2 position = table.WorldPlane.Position(Inverse(point) + table.WorldImage / 2 + table.WorldLift);
            return new Vector2(position.X * table.Length, position.Y * table.Width);
        }
        public float PixelsPerMeter(Table table) => (IsWorld ? table.WorldPixelsPerMeter : table.UiScale.X) * Scale;
        public Vector2 Vector(Table table, Vector2 direction) => Direction(IsWorld ? table.WorldX * direction.X + table.WorldY * direction.Y : new Vector2(direction.X * table.UiScale.X, direction.Y * table.UiScale.Y)) * Scale;
        public Vector2 Direction(Vector2 direction) => new Vector2(direction.X * cosine - direction.Y * sine, direction.X * sine + direction.Y * cosine);
    }

    public static class PoolAim {
        public static Vector2 Trace(Table table, ReadOnlySpan<Ball> balls, Vector2 direction) {
            Vector2 from = balls[0].Position; direction = Vector2.Normalize(direction); float time = table.Length * 2, r = table.Radius;
            if (direction.X > 1e-6f) { time = MathF.Min(time, (table.Length - r - from.X) / direction.X); }
            else if (direction.X < -1e-6f) { time = MathF.Min(time, (r - from.X) / direction.X); }
            if (direction.Y > 1e-6f) { time = MathF.Min(time, (table.Width - r - from.Y) / direction.Y); }
            else if (direction.Y < -1e-6f) { time = MathF.Min(time, (r - from.Y) / direction.Y); }
            time = MathF.Max(0, time);
            for (int i = 1; i < 16; i++) { if (!balls[i].Down && Physics.HitCircle(from - balls[i].Position, direction, 2 * r, time, out float hit)) { time = hit; } }
            return from + direction * time;
        }
    }
}
