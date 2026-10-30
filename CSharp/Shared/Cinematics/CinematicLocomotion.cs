using System;

namespace ScpItdMod.Cinematics {
    // Ground locomotion of one person toward a point on a line. The speed is a state with bounded acceleration and a stopping profile, never an on/off key:
    // a person eases into the walk, keeps the animation's own walk speed, brakes so as to stop inside the arrival radius, and does not twitch back on the edge of it.
    public struct CinematicLocomotion {
        public float Speed;
        public bool Settled { get; private set; }
        public const float StartAcceleration = 1.1f, StopDeceleration = 1.6f, ResumeRadiusShare = 1.8f, StandstillSpeed = 1e-3f;

        // distance: signed metres to the target along the walking line; cruise: speed cap in m/s (the animation's walk speed); arrival: radius that counts as arrived.
        public float Step(float dt, float distance, float cruise, float arrival) {
            if (!(dt > 0) || !float.IsFinite(distance) || !(cruise > 0) || !(arrival > 0)) { Speed = 0; return 0; }
            float remaining = MathF.Abs(distance), direction = distance < 0 ? -1f : 1f;
            if (Settled) {
                if (remaining <= arrival * ResumeRadiusShare) return 0;
                Settled = false;
            }
            if (remaining <= arrival && MathF.Abs(Speed) <= StandstillSpeed) { Speed = 0; Settled = true; return 0; }
            // Speed that still lets the person stop at the middle of the arrival radius at the braking rate.
            float brake = MathF.Sqrt(2f * StopDeceleration * MathF.Max(0f, remaining - arrival * .5f));
            float wanted = MathF.Min(cruise, brake);
            if (remaining <= arrival) wanted = 0;
            float along = Speed * direction;
            float change = Math.Clamp(wanted - along, -StopDeceleration * dt, StartAcceleration * dt);
            along = MathF.Max(0f, along + change);
            if (Speed * direction < 0) along = 0; // the target moved behind the person: stop first, then turn
            Speed = along * direction;
            return Speed;
        }
    }

    public static class CinematicFormation {
        // The first person out walks to the slot farthest from the door, the last stands nearest to it: nobody has to pass a person who is already in place.
        // slots: the line's positions in any order; returns, for the n-th person out, the slot index to take.
        public static void Assign(ReadOnlySpan<float> slots, float door, Span<int> order) {
            if (order.Length != slots.Length) throw new ArgumentException("One slot per person", nameof(order));
            for (int i = 0; i < order.Length; i++) order[i] = i;
            // Insertion sort by distance from the door, descending: the sets are a handful of people, and nothing is allocated.
            for (int i = 1; i < order.Length; i++) {
                int item = order[i]; float key = MathF.Abs(slots[item] - door); int j = i - 1;
                while (j >= 0 && (MathF.Abs(slots[order[j]] - door) < key || MathF.Abs(slots[order[j]] - door) == key && order[j] > item)) { order[j + 1] = order[j]; j--; }
                order[j + 1] = item;
            }
        }
    }
}
