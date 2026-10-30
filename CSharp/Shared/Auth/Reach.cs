using System;
using ScpItdMod.Core;
using System.Runtime.CompilerServices;

namespace ScpItdMod.Auth {
    public enum ReachPhase : byte { Prepare, Approach, Contact, Release, Return, Done }

    public struct ReachTiming {
        public const float Prepare = 0.3f;
        public const float ApproachBase = 0.18f;
        public const float ApproachPerArm = 0.22f;
        public const float Press = 0.09f;
        public const float Read = 0.32f;
        public const float Lift = 0.09f;
        public const float Return = 0.46f;
        public const float CancelReturn = 0.42f;

        public float Approach;

        public static ReachTiming For(float distance, float arm) =>
            new ReachTiming { Approach = ApproachBase + ApproachPerArm * Math.Clamp(distance / MathF.Max(arm, 0.01f), 0.0f, 1.5f) };

        public float ContactAt => Prepare + Approach + Press;

        public float ReleaseAt => ContactAt + Read;

        public float Total => ReleaseAt + Lift + Return;
    }

    public struct ReachMotion {
        public const int MaxKeys = 7;

        private Vec2 k0, k1, k2, k3, k4, k5, k6;
        private Vec2 v0, v1, v2, v3, v4, v5, v6;
        private float t0, t1, t2, t3, t4, t5, t6;
        private int count;
        private float contactAt;
        private float releaseAt;
        private bool cancelled;

        public float Arm;
        public Vec2 Contact;

        public bool Cancelled => cancelled;

        public float End => count == 0 ? 0.0f : T(count - 1);

        public float ContactAt => contactAt;

        public float ReleaseAt => releaseAt;

        public static ReachMotion Plan(Vec2 start, Vec2 contact, float arm, in ReachTiming timing) {
            var m = new ReachMotion { Arm = arm };
            float reach = arm * 0.95f;
            float d = contact.Length();
            if (d > reach && d > 1e-5f) { contact *= reach / d; }
            if (contact.X < arm * 0.12f) { contact = new Vec2(arm * 0.12f, contact.Y); }
            m.Contact = contact;
            Vec2 toward = contact.Length() > 1e-5f ? Vec2.Normalize(contact) : new Vec2(1, 0);
            Vec2 restPose = Rest(arm);
            var chest = new Vec2(arm * 0.3f, Math.Clamp(restPose.Y + (contact.Y - restPose.Y) * 0.55f, -arm * 0.45f, arm * 0.2f));
            Vec2 approach = contact - toward * (arm * 0.12f);
            Vec2 rest = restPose;
            float t = 0.0f;
            m.Key(t, start, Vec2.Zero);
            t += ReachTiming.Prepare;
            m.Key(t, chest, float.NaN);
            t += timing.Approach;
            m.Key(t, approach, float.NaN);
            t += ReachTiming.Press;
            m.contactAt = t;
            m.Key(t, contact, Vec2.Zero);
            t += ReachTiming.Read;
            m.releaseAt = t;
            m.Key(t, contact, Vec2.Zero);
            t += ReachTiming.Lift;
            m.Key(t, approach, float.NaN);
            t += ReachTiming.Return;
            m.Key(t, rest, Vec2.Zero);
            m.Smooth();
            return m;
        }

        public static Vec2 Rest(float arm) => new Vec2(arm * 0.08f, -arm * 0.92f);

        public void Cancel(float now) {
            if (cancelled || now >= End) { return; }
            Sample(now, out Vec2 pos, out Vec2 vel);
            Vec2 rest = Rest(Arm);
            Vec2 mid = Vec2.Lerp(pos, new Vec2(Arm * 0.3f, -Arm * 0.35f), 0.6f);
            count = 0;
            Key(now, pos, vel);
            Key(now + ReachTiming.CancelReturn * 0.45f, mid, float.NaN);
            Key(now + ReachTiming.CancelReturn, rest, Vec2.Zero);
            Smooth();
            contactAt = float.PositiveInfinity;
            releaseAt = float.PositiveInfinity;
            cancelled = true;
        }

        public void Shift(float dt) {
            for (int i = 0; i < count; i++) { SetT(i, T(i) + dt); }
            contactAt += dt;
            releaseAt += dt;
        }

        public ReachPhase Phase(float t) {
            if (count == 0 || t >= End) { return ReachPhase.Done; }
            if (cancelled) { return ReachPhase.Return; }
            if (t < T(1)) { return ReachPhase.Prepare; }
            if (t < contactAt) { return ReachPhase.Approach; }
            if (t < releaseAt) { return ReachPhase.Contact; }
            if (t < T(5)) { return ReachPhase.Release; }
            return ReachPhase.Return;
        }

        public float Weight(float t) {
            if (count == 0) { return 0.0f; }
            float rise = IkBlend.Ease(t / (ReachTiming.Prepare * 0.6f));
            float from = cancelled ? T(0) : count >= 6 ? T(5) : End;
            float fall = 1.0f - IkBlend.Ease((t - from) / MathF.Max(1e-4f, End - from));
            return MathF.Min(rise, fall);
        }

        public float CardTilt(float t) {
            if (cancelled || count < 7) { return 0.0f; }
            float a = Smooth01((t - T(1)) / MathF.Max(1e-4f, contactAt - T(1)));
            float b = 1.0f - Smooth01((t - releaseAt) / MathF.Max(1e-4f, T(6) - releaseAt));
            return MathF.Min(a, b);
        }

        public Vec2 Sample(float t) {
            Sample(t, out Vec2 p, out _);
            return p;
        }

        public void Sample(float t, out Vec2 pos, out Vec2 vel) {
            if (count == 0) {
                pos = Rest(Arm);
                vel = Vec2.Zero;
                return;
            }
            if (t <= T(0)) {
                pos = K(0);
                vel = Vec2.Zero;
                return;
            }
            int last = count - 1;
            if (t >= T(last)) {
                pos = K(last);
                vel = Vec2.Zero;
                return;
            }
            int i = 0;
            while (i < last - 1 && t >= T(i + 1)) { i++; }
            float a = T(i);
            float h = T(i + 1) - a;
            float u = (t - a) / h;
            float u2 = u * u;
            float u3 = u2 * u;
            float h00 = 2 * u3 - 3 * u2 + 1;
            float h10 = u3 - 2 * u2 + u;
            float h01 = -2 * u3 + 3 * u2;
            float h11 = u3 - u2;
            Vec2 p0 = K(i), p1 = K(i + 1), m0 = V(i) * h, m1 = V(i + 1) * h;
            pos = h00 * p0 + h10 * m0 + h01 * p1 + h11 * m1;
            float d00 = 6 * u2 - 6 * u;
            float d10 = 3 * u2 - 4 * u + 1;
            float d01 = -6 * u2 + 6 * u;
            float d11 = 3 * u2 - 2 * u;
            vel = (d00 * p0 + d10 * m0 + d01 * p1 + d11 * m1) / h;
        }

        private void Key(float t, Vec2 p, Vec2 v) {
            SetT(count, t);
            SetK(count, p);
            SetV(count, v);
            count++;
        }

        private void Key(float t, Vec2 p, float auto) => Key(t, p, new Vec2(auto, auto));

        private void Smooth() {
            for (int i = 0; i < count; i++) {
                Vec2 v = V(i);
                if (!float.IsNaN(v.X)) { continue; }
                if (i == 0 || i == count - 1) {
                    SetV(i, Vec2.Zero);
                    continue;
                }
                Vec2 slope = (K(i + 1) - K(i - 1)) / (T(i + 1) - T(i - 1));
                Vec2 before = (K(i) - K(i - 1)) / (T(i) - T(i - 1));
                Vec2 after = (K(i + 1) - K(i)) / (T(i + 1) - T(i));
                SetV(i, new Vec2(Limit(slope.X, before.X, after.X), Limit(slope.Y, before.Y, after.Y)));
            }
        }

        private static float Limit(float slope, float before, float after) {
            if (before * after <= 0.0f) { return 0.0f; }
            float cap = 3.0f * MathF.Min(MathF.Abs(before), MathF.Abs(after));
            return Math.Clamp(slope, -cap, cap);
        }

        private static float Smooth01(float x) => x <= 0.0f ? 0.0f : x >= 1.0f ? 1.0f : x * x * (3.0f - 2.0f * x);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float T(int i) => i switch { 0 => t0, 1 => t1, 2 => t2, 3 => t3, 4 => t4, 5 => t5, _ => t6 };

        private Vec2 K(int i) => i switch { 0 => k0, 1 => k1, 2 => k2, 3 => k3, 4 => k4, 5 => k5, _ => k6 };

        private Vec2 V(int i) => i switch { 0 => v0, 1 => v1, 2 => v2, 3 => v3, 4 => v4, 5 => v5, _ => v6 };

        private void SetT(int i, float x) {
            switch (i) { case 0: t0 = x; break; case 1: t1 = x; break; case 2: t2 = x; break; case 3: t3 = x; break; case 4: t4 = x; break; case 5: t5 = x; break; default: t6 = x; break; }
        }

        private void SetK(int i, Vec2 x) {
            switch (i) { case 0: k0 = x; break; case 1: k1 = x; break; case 2: k2 = x; break; case 3: k3 = x; break; case 4: k4 = x; break; case 5: k5 = x; break; default: k6 = x; break; }
        }

        private void SetV(int i, Vec2 x) {
            switch (i) { case 0: v0 = x; break; case 1: v1 = x; break; case 2: v2 = x; break; case 3: v3 = x; break; case 4: v4 = x; break; case 5: v5 = x; break; default: v6 = x; break; }
        }
    }

    public static class TwoBone {
        public static bool Solve(Vec2 target, float upper, float lower, int bend, out Vec2 elbow, out Vec2 hand) {
            float d = target.Length();
            float max = upper + lower - 1e-4f;
            float min = MathF.Abs(upper - lower) + 1e-4f;
            bool reached = d <= max && d >= min;
            float dc = Math.Clamp(d, min, max);
            Vec2 dir = d > 1e-6f ? target / d : new Vec2(1, 0);
            hand = dir * dc;
            float a = (upper * upper - lower * lower + dc * dc) / (2 * dc);
            float h = MathF.Sqrt(MathF.Max(0.0f, upper * upper - a * a));
            var normal = new Vec2(-dir.Y, dir.X) * (bend >= 0 ? 1.0f : -1.0f);
            elbow = dir * a + normal * h;
            return reached;
        }
    }
}
