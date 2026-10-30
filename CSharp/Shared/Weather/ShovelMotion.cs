using System;

namespace ScpItdMod.Weather {
    public enum ShovelStroke : byte { None, Dig, Pour, Throw }

    // Where a held shovel is, as an offset from its resting hold: forward and up in item units, and the blade angle in degrees. A stroke is a path of key
    // poses (wind-up, strike, bite, heave, settle) joined by cubic Hermite curves; the blade does not sit on that path but is pulled along it by a
    // critically-tuned second-order system per channel (the arms and the tool are a mass on a spring): it lags, overshoots a fast swing, follows through
    // and settles. The load makes the system heavier, so a full blade is slower and sags. A dig that meets snow stronger than the stroke can cut stops
    // short with a recoil; Contact is the moment the world is changed.
    internal static class ShovelMotion {
        private readonly struct Key {
            internal readonly float T, X, Y, Angle;
            internal Key(float t, float x, float y, float angle) { T = t; X = x; Y = y; Angle = angle; }
        }
        private static readonly Key[] dig = {
            new Key(0, 0, 0, 0), new Key(.24f, -9, 13, -30), new Key(.40f, 22, -28, 60), new Key(.50f, 23, -29, 62), new Key(.74f, 10, 9, 20), new Key(.9f, -2, -2, -5), new Key(1, 0, 0, 0) };
        private static readonly Key[] pour = {
            new Key(0, 0, 0, 0), new Key(.25f, 8, 7, -14), new Key(.55f, 15, -3, 74), new Key(.82f, 15, -4, 78), new Key(1, 0, 0, 0) };
        private static readonly Key[] throwing = {
            new Key(0, 0, 0, 0), new Key(.32f, -16, 12, 42), new Key(.52f, 24, -5, -64), new Key(.7f, 28, -13, -76), new Key(1, 0, 0, 0) };
        private static Key[] Keys(ShovelStroke stroke) => stroke switch { ShovelStroke.Dig => dig, ShovelStroke.Pour => pour, ShovelStroke.Throw => throwing, _ => Array.Empty<Key>() };

        // Seconds a stroke takes; load is the share of the shovel's capacity that it carries, 0 to 1.
        internal static float Duration(ShovelStroke stroke, float load) => stroke switch {
            ShovelStroke.Dig => .62f + .3f * Math.Clamp(load, 0, 1), ShovelStroke.Pour => .9f + .3f * Math.Clamp(load, 0, 1), ShovelStroke.Throw => .62f + .25f * Math.Clamp(load, 0, 1), _ => 0 };
        // Share of the stroke at which the blade touches the snow, the load starts to leave the blade or the throw lets go.
        internal static float Contact(ShovelStroke stroke) => stroke switch { ShovelStroke.Dig => .40f, ShovelStroke.Pour => .4f, ShovelStroke.Throw => .52f, _ => 1 };
        // Natural frequency (Hz) and damping ratio of the hands and tool: a quick, slightly underdamped reach that a load slows like a heavier pendulum.
        internal const float Frequency = 3.2f, Damping = .62f, Lead = 1.6f;
        internal static float LoadedFrequency(float load) => Frequency / MathF.Sqrt(1 + 2.5f * Math.Clamp(load, 0, 1));

        // The target pose at share t (0 to 1) of a stroke. bite is the share of the cut the blade achieved (1 for a full cut): a short bite stops the strike higher.
        internal static void Target(ShovelStroke stroke, float t, float load, float bite, out float x, out float y, out float angle) {
            Key[] keys = Keys(stroke); x = y = angle = 0;
            if (keys.Length == 0) return;
            t = Math.Clamp(t, 0, 1); int i = 0; while (i + 2 < keys.Length && t > keys[i + 1].T) i++;
            Key a = keys[i], b = keys[i + 1]; float span = b.T - a.T, s = (t - a.T) / span;
            x = Hermite(keys, i, s, span, 0); y = Hermite(keys, i, s, span, 1); angle = Hermite(keys, i, s, span, 2);
            load = Math.Clamp(load, 0, 1); bite = Math.Clamp(bite, 0, 1);
            if (stroke == ShovelStroke.Dig) {
                float buried = MathF.Sin(MathF.PI * Math.Clamp((t - .3f) / .4f, 0, 1));
                y += (1 - bite) * 18 * buried; x -= (1 - bite) * 7 * buried;
                y -= load * 7 * MathF.Sin(MathF.PI * Math.Clamp((t - .5f) / .45f, 0, 1));
            }
            if (stroke == ShovelStroke.Throw) y -= load * 5 * MathF.Sin(MathF.PI * Math.Clamp(t / .4f, 0, 1));
            // A poured load shakes off the blade: a decaying tremor of the tilt once it is over the snow.
            if (stroke == ShovelStroke.Pour && t > .55f) angle += 3.5f * (1 - t) / .45f * MathF.Exp(-8 * (t - .55f)) * MathF.Sin(2 * MathF.PI * 6 * (t - .55f));
        }
        // One frame of a stroke at share t: the target pose, and the hands driven hard through the strike and the swing of a throw (they lead the tool and
        // it whips through) and carried through the rest. The component and the tests step a stroke only through this.
        internal static void Drive(ShovelRig rig, ShovelStroke stroke, float t, float load, float bite, float dt) {
            Target(stroke, t, load, bite, out float x, out float y, out float angle);
            bool fast = stroke == ShovelStroke.Throw && t > .3f && t < .6f || stroke == ShovelStroke.Dig && t > .24f && t < .42f;
            rig.Step(dt, x, y, angle, LoadedFrequency(load) * (fast ? 1.8f : 1), Damping, fast ? Lead : 0);
        }
        // The pose of the shovel at rest in the hands: it hangs by the weight it carries and breathes with the holder.
        internal static void Rest(float load, double seconds, float speed, out float x, out float y, out float angle) {
            load = Math.Clamp(load, 0, 1); float breath = MathF.Sin((float)(seconds * 2 * Math.PI * .27));
            x = -Math.Clamp(speed * .9f, -6, 6);
            y = -4 * load + .8f * breath;
            angle = -8 * load + (1 + 2 * load) * MathF.Sin((float)(seconds * 2 * Math.PI * .5) + .7f) * .6f;
        }
        // Tangents are central differences over the neighbouring keys, zero at both ends, which is what makes a stroke start and finish at rest.
        private static float Hermite(Key[] keys, int i, float s, float span, int channel) {
            float p0 = Value(keys[i], channel), p1 = Value(keys[i + 1], channel);
            float m0 = i == 0 ? 0 : (p1 - Value(keys[i - 1], channel)) / (keys[i + 1].T - keys[i - 1].T) * span;
            float m1 = i + 2 >= keys.Length ? 0 : (Value(keys[i + 2], channel) - p0) / (keys[i + 2].T - keys[i].T) * span;
            float s2 = s * s, s3 = s2 * s;
            return (2 * s3 - 3 * s2 + 1) * p0 + (s3 - 2 * s2 + s) * m0 + (-2 * s3 + 3 * s2) * p1 + (s3 - s2) * m1;
        }
        private static float Value(in Key k, int channel) => channel == 0 ? k.X : channel == 1 ? k.Y : k.Angle;
    }

    // The tool in the hands as a second-order system per channel, x'' = (target + k3 target' - x - k1 x') / k2: frequency f, damping zeta and lead r
    // (r > 1 lets a fast swing overshoot its pose, which is the whip of a throw). Stable for any frame time: k2 is raised to the smallest value the
    // semi-implicit step tolerates. The same input on every peer gives the same motion.
    internal sealed class ShovelRig {
        private struct Channel { internal float Value, Velocity, Target; }
        private Channel x, y, angle;
        internal float X => x.Value;
        internal float Y => y.Value;
        internal float Angle => angle.Value;
        internal float AngularVelocity => angle.Velocity;
        internal float SpeedX => x.Velocity;
        internal float SpeedY => y.Velocity;
        internal void Reset() { x = y = angle = default; }
        // Moves toward the target pose; the first call after a reset starts from rest at the resting hold.
        internal void Step(float dt, float tx, float ty, float tangle, float frequency, float damping, float lead) {
            if (!(dt > 0) || !float.IsFinite(dt)) return;
            float w = 2 * MathF.PI * frequency, k1 = damping / (MathF.PI * frequency), k2 = 1 / (w * w), k3 = lead * damping / w;
            k2 = MathF.Max(k2, MathF.Max(dt * dt * .5f + dt * k1 * .5f, dt * k1));
            Advance(ref x, tx, dt, k1, k2, k3); Advance(ref y, ty, dt, k1, k2, k3); Advance(ref angle, tangle, dt, k1, k2, k3);
        }
        private static void Advance(ref Channel c, float target, float dt, float k1, float k2, float k3) {
            float targetVelocity = (target - c.Target) / dt; c.Target = target;
            c.Value += dt * c.Velocity;
            c.Velocity += dt * (target + k3 * targetVelocity - c.Value - k1 * c.Velocity) / k2;
        }
        // A blade that hits snow it cannot cut is thrown back: a velocity impulse up and away from the stroke.
        internal void Recoil(float strength) { y.Velocity += 160 * strength; x.Velocity -= 70 * strength; angle.Velocity -= 260 * strength; }
        internal bool Settled(float tolerance) => MathF.Abs(x.Value - x.Target) + MathF.Abs(y.Value - y.Target) + MathF.Abs(angle.Value - angle.Target) * .2f < tolerance &&
            MathF.Abs(x.Velocity) + MathF.Abs(y.Velocity) + MathF.Abs(angle.Velocity) * .2f < tolerance * 10;
    }
}
