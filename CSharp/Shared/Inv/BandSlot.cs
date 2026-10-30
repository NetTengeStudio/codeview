using System;

namespace ScpItdMod.Inv {
    public readonly struct BandSlot {
        public readonly float Inner, Outer, Angle, Half, Gap;

        public BandSlot(float inner, float outer, float angle, float half, float gap) {
            Inner = inner; Outer = outer; Angle = angle; Half = half; Gap = gap;
        }

        public float Mid => 0.5f * (Inner + Outer);

        public float Thickness => Outer - Inner;

        public float CentreX(float ringX, float scale) => ringX + MathF.Cos(Angle) * Mid * scale;

        public float CentreY(float ringY, float scale) => ringY + MathF.Sin(Angle) * Mid * scale;

        public bool Contains(float dx, float dy, float pad) => ContainsPose(dx, dy, Angle, 1f, pad);

        public bool ContainsPose(float dx, float dy, float angle, float scale, float pad) {
            if (!(Outer > Inner) || !(Half > 0) || !(scale > 0) || !float.IsFinite(scale) || !float.IsFinite(pad)) return false;
            float cosine = MathF.Cos(angle), sine = MathF.Sin(angle);
            return SectorDistance((dx * cosine + dy * sine) / scale, (-dx * sine + dy * cosine) / scale, Inner, Outer, Half, Gap) <= pad / scale;
        }

        public static float Wrap(float angle) => angle - MathF.Tau * MathF.Round(angle / MathF.Tau);

        public static float SectorDistance(float x, float y, float inner, float outer, float half, float gap) {
            float r = MathF.Sqrt(x * x + y * y);
            float radial = MathF.Max(inner - r, r - outer);
            float nx = -MathF.Sin(half), upY = MathF.Cos(half), loY = -MathF.Cos(half);
            float g = gap * 0.5f;
            float up = x * nx + y * upY + g;
            float down = x * nx + y * loY + g;
            return MathF.Max(radial, MathF.Max(up, down));
        }

        private const float RimInset = 1.5f, RimHalfWidth = 0.9f;

        // Pixel coverage of the hairline drawn just inside a sector's edge, from the sector distance.
        public static float RimCoverage(float distance) => 0.5f + RimHalfWidth - MathF.Abs(distance + RimInset);
    }
}
