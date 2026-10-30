using System;

namespace ScpItdMod.Inv {
    // Where one ring wedge is this frame. Pure geometry: the renderer draws from it and the slot's hit rectangle
    // is taken from the same value, so what the player sees and what they can click cannot drift apart.
    public readonly struct RingWedge {
        public const float HoverGrow = 0.1f, HaloGrow = 0.025f, MinimumSpread = 0.9f, SpreadRange = 0.1f;

        // Wedges open clockwise from the top and close back in exactly the reverse order, one interval apart.
        // The closing interval is shorter so a retract does not take longer than the opening.
        public const double OpenInterval = 0.035, CloseInterval = 0.02;

        public static double OpenDelay(int slot) => slot * OpenInterval;

        public static double CloseDelay(int slot) => (InvRing.Slots - 1 - slot) * CloseInterval;

        // Radial spread of the ring around its centre: 1 is resting, the bloom starts a little inside.
        public readonly float Spread;
        // Wedge contents grow with hover and shrink while pressed.
        public readonly float Size;
        public readonly float CentreX, CentreY;
        // Bloom in 0..1 (the wedge fades in with it).
        public readonly float Grown;
        // Halo for the dark backing: the same spread with a hover lift.
        public readonly float Halo;
        public readonly float Hover;
        // Integer pixel rectangle of the slot, the value handed to VisualSlot.
        public readonly int X, Y, Side;

        private RingWedge(float spread, float size, float cx, float cy, float grown, float hover) {
            Spread = spread; Size = size; CentreX = cx; CentreY = cy; Grown = grown; Hover = hover;
            Halo = spread * (1f + HaloGrow * hover);
            Side = Math.Max(1, (int)size);
            X = (int)(cx - Side * 0.5f); Y = (int)(cy - Side * 0.5f);
        }

        public bool Visible => Grown > 0f;

        // Places wedge `slot` for the current animation values. Inputs are clamped to their ranges, so a stray value
        // from the motion engine cannot push a wedge off the ring.
        public static RingWedge Place(in InvRing ring, int slot, float spread, float bloom, float hover, float press) {
            float grown = Scalar01(bloom), lift = Scalar01(hover), pressed = float.IsNaN(press) ? 1f : Math.Clamp(press, 0f, 1f);
            float wedgeSpread = spread * (MinimumSpread + SpreadRange * grown);
            float scale = wedgeSpread * (1f + HoverGrow * lift) * pressed;
            float size = (int)ring.Cell * scale;
            float cx = ring.X + (ring.CellX(slot) - ring.X) * wedgeSpread;
            float cy = ring.Y + (ring.CellY(slot) - ring.Y) * wedgeSpread;
            return new RingWedge(wedgeSpread, size, cx, cy, grown, lift);
        }

        private static float Scalar01(float value) => float.IsNaN(value) ? 0f : Math.Clamp(value, 0f, 1f);
    }
}
