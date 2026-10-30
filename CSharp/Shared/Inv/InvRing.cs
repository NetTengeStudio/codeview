using System;
using System.Runtime.CompilerServices;

namespace ScpItdMod.Inv {
    public readonly struct InvRing {
        public const int Slots = InvSlots.Capacity;
        public const int None = -1;
        public const int Hub = -2;

        private const float RadiusOfHeight = 0.19f;
        private const float CellOfHeight = 0.105f;
        private const float BandOfHeight = 0.075f;
        private const float HubGap = 0.012f;
        public const float GapOfHeight = 0.006f;
        private const float ReachOfBand = 1.6f;
        private const float Sector = MathF.PI * 2.0f / Slots;
        private const float FirstAngle = -MathF.PI * 0.5f;

        public readonly float X;
        public readonly float Y;
        public readonly float Radius;
        public readonly float Cell;
        public readonly float Inner;
        public readonly float Outer;
        public readonly float HubRadius;

        public InvRing(float x, float y, float height) {
            X = x;
            Y = y;
            Radius = height * RadiusOfHeight;
            Cell = height * CellOfHeight;
            Inner = Radius - height * BandOfHeight;
            Outer = Radius + height * BandOfHeight;
            HubRadius = Inner - height * HubGap;
        }

        public static float SectorAngle => Sector;

        public static float InnerOfOuter => (RadiusOfHeight - BandOfHeight) / (RadiusOfHeight + BandOfHeight);

        public static float GapOfOuter => GapOfHeight / (RadiusOfHeight + BandOfHeight);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Angle(int slot) => FirstAngle + slot * Sector;

        public float CellX(int slot) => X + Radius * MathF.Cos(Angle(slot));

        public float CellY(int slot) => Y + Radius * MathF.Sin(Angle(slot));

        public int Pick(float px, float py) {
            float dx = px - X;
            float dy = py - Y;
            float d2 = dx * dx + dy * dy;
            if (d2 < HubRadius * HubRadius) { return Hub; }
            if (d2 < Inner * Inner || d2 > Outer * Outer) { return None; }
            float a = MathF.Atan2(dy, dx) - FirstAngle + Sector * 0.5f;
            int slot = (int)MathF.Floor(a / Sector);
            slot %= Slots;
            if (slot < 0) { slot += Slots; }
            float angle = Angle(slot), cosine = MathF.Cos(angle), sine = MathF.Sin(angle);
            return WedgeDistance((dx * cosine + dy * sine) / Outer, (-dx * sine + dy * cosine) / Outer) <= 0 ? slot : None;
        }

        public static float WedgeDistance(float x, float y) {
            float half = Sector * 0.5f;
            float nx = -MathF.Sin(half);
            float ny = MathF.Cos(half);
            float gap = GapOfOuter;
            float r = MathF.Sqrt(x * x + y * y);
            float radial = MathF.Max(InnerOfOuter - r, r - 1.0f);
            float up = x * nx + y * ny + gap;
            float down = x * nx - y * ny + gap;
            return MathF.Max(radial, MathF.Max(up, down));
        }

        public static float DiscDistance(float x, float y) => MathF.Sqrt(x * x + y * y) - 1.0f;

        public static float PlaceX(float width, float height, float left, float right) {
            float centre = width * 0.5f;
            if (!(left < right)) { return centre; }
            float half = height * (RadiusOfHeight + BandOfHeight * ReachOfBand);
            if (centre + half <= left || centre - half >= right) { return centre; }
            float roomLeft = left;
            float roomRight = width - right;
            float x = roomLeft >= roomRight ? Math.Min(centre, left - half) : Math.Max(centre, right + half);
            return Math.Clamp(x, Math.Min(half, centre), Math.Max(width - half, centre));
        }

        public bool Covers(float px, float py) {
            float dx = px - X;
            float dy = py - Y;
            float reach = Radius + (Outer - Radius) * ReachOfBand;
            return dx * dx + dy * dy <= reach * reach;
        }
    }
}
