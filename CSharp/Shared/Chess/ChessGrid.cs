using System;
using ScpItdMod.Core;

namespace ScpItdMod.Chess {
    public static class ChessGrid {
        public static int Square(in Plane plane, Vec2 point, bool flipped = false) {
            Vec2 position = plane.Position(point);
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || position.X < 0 || position.X >= 1 || position.Y < 0 || position.Y >= 1) { return -1; }
            int file = (int)(position.X * 8), rank = 7 - (int)(position.Y * 8);
            return flipped ? (7 - rank) * 8 + 7 - file : rank * 8 + file;
        }
        public static Vec2 Cell(in Plane plane, int square, bool flipped = false) {
            if ((uint)square >= 64) { throw new ArgumentOutOfRangeException(nameof(square)); }
            if (flipped) { square = 63 - square; }
            return plane.Point(new Vec2(((square & 7) + 0.5f) / 8, (7 - (square >> 3) + 0.5f) / 8));
        }
    }
}
