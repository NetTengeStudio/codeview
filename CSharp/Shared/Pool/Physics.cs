using System;
using Vector2 = ScpItdMod.Core.Vec2;

namespace ScpItdMod.Pool {
    public struct Ball {
        public Vector2 Position, Velocity;
        public AngularVelocity Spin;
        public ScpItdMod.Motion.Rotation3 Orientation;
        public byte Pocket;
        public bool Down => Pocket != 0;
    }

    public sealed class Shot {
        public ushort First, Rails, Potted, Off;
        public bool RailAfter, CueSide, BeyondHead;
        public int CueFoot;
        public double FirstAt;
        public readonly byte[] Pockets = new byte[16];
        public void Clear() { First = Rails = Potted = Off = 0; RailAfter = CueSide = BeyondHead = false; CueFoot = 0; FirstAt = double.PositiveInfinity; Array.Clear(Pockets); }
    }

    public sealed class Physics {
        public const float Step = 1f / 240f, MaxSpeed = 10f;
        private const float Gravity = 9.80665f, ContactSlop = 0.000002f;
        public readonly Table Table;
        private readonly Ball[] balls = new Ball[16];
        public readonly Shot Shot = new Shot();
        public Span<Ball> Balls => balls;
        public bool Moving { get; private set; }
        public bool Fault { get; private set; }
        public uint Tick { get; private set; }
        public int Contacts { get; private set; }

        public Physics(Table table) { Table = table ?? throw new ArgumentNullException(nameof(table)); Rack(1); }

        public void Rack(uint seed) {
            Array.Clear(balls);
            for (int i = 0; i < balls.Length; i++) { balls[i].Orientation = ScpItdMod.Motion.Rotation3.Identity; }
            Fault = Moving = false; Tick = 0;
            balls[0].Position = new Vector2(Table.Length / 4, Table.Width / 2);
            Span<byte> order = stackalloc byte[15];
            for (int i = 0; i < 15; i++) { order[i] = (byte)(i + 1); }
            for (int i = 14; i > 0; i--) { seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5; int j = (int)(seed % (uint)(i + 1)); byte t = order[i]; order[i] = order[j]; order[j] = t; }
            Swap(order, 4, 8);
            int solid = 0; while (order[solid] > 7 || solid == 4) { solid++; }
            byte a = order[10]; order[10] = order[solid]; order[solid] = a;
            int stripe = 0; while (order[stripe] < 9 || stripe == 10) { stripe++; }
            a = order[14]; order[14] = order[stripe]; order[stripe] = a;
            int k = 0; float diameter = 2 * Table.Radius + ContactSlop;
            for (int row = 0; row < 5; row++) {
                for (int col = 0; col <= row; col++) {
                    balls[order[k++]].Position = new Vector2(Table.Length * 0.75f + row * diameter * 0.8660254038f, Table.Width / 2 + (col - row * 0.5f) * diameter);
                }
            }
            Shot.Clear();
        }

        private static void Swap(Span<byte> order, int index, byte ball) {
            for (int i = 0; i < order.Length; i++) {
                if (order[i] != ball) { continue; }
                byte v = order[index]; order[index] = ball; order[i] = v; return;
            }
        }

        public bool CanPlace(int index, Vector2 position, bool kitchen = false) {
            float r = Table.Radius;
            if ((uint)index >= 16 || !Finite(position) || position.X < r || position.Y < r || position.X > Table.Length - r || position.Y > Table.Width - r || kitchen && position.X >= Table.Length / 4) { return false; }
            for (int i = 0; i < 16; i++) {
                if (i != index && !balls[i].Down && Vector2.DistanceSquared(position, balls[i].Position) < 4 * r * r + ContactSlop * r) { return false; }
            }
            for (int i = 0; i < 6; i++) {
                if (Vector2.DistanceSquared(position, Table.Pockets[i]) < Table.PocketRadius * Table.PocketRadius) { return false; }
            }
            return true;
        }

        public bool Place(int index, Vector2 position, bool kitchen = false) {
            if (Moving || !CanPlace(index, position, kitchen)) { return false; }
            balls[index] = new Ball { Position = position, Orientation = ScpItdMod.Motion.Rotation3.Identity }; return true;
        }

        public bool Spot(int index) {
            if (Moving || (uint)index >= 16) { return false; }
            float foot = Table.Length * 0.75f, diameter = 2 * Table.Radius + ContactSlop * 2;
            for (int sign = 1; sign >= -1; sign -= 2) {
                float x = foot;
                for (int pass = 0; pass < 16 && x >= Table.Radius && x <= Table.Length - Table.Radius; pass++) {
                    float next = x;
                    for (int i = 0; i < 16; i++) {
                        if (i == index || balls[i].Down) { continue; }
                        float dy = balls[i].Position.Y - Table.Width / 2;
                        if (MathF.Abs(dy) >= diameter) { continue; }
                        float half = MathF.Sqrt(diameter * diameter - dy * dy), left = balls[i].Position.X - half, right = balls[i].Position.X + half;
                        if (x > left && x < right) { next = sign > 0 ? MathF.Max(next, right) : MathF.Min(next, left); }
                    }
                    if (next == x) { if (Place(index, new Vector2(x, Table.Width / 2))) { return true; } break; }
                    x = next;
                }
            }
            return false;
        }

        public bool Strike(Vector2 direction, float speed, Vector2 tip) {
            float lengthSquared = direction.LengthSquared();
            if (Moving || Fault || balls[0].Down || !Finite(direction) || !Finite(tip) || !float.IsFinite(lengthSquared) || !float.IsFinite(speed) || speed < 0.05f || speed > MaxSpeed || tip.LengthSquared() > 0.64f || lengthSquared < 0.5f) { return false; }
            direction /= MathF.Sqrt(lengthSquared); Shot.Clear(); Contacts = 0;
            ref Ball cue = ref balls[0]; cue.Velocity = direction * speed;
            float k = 2.5f * speed / Table.Radius;
            cue.Spin = new AngularVelocity(-direction.Y * tip.Y * k, direction.X * tip.Y * k, -tip.X * k);
            Moving = true; return true;
        }

        public void Advance() {
            if (!Moving || Fault) { return; }
            Tick++;
            Friction(Step / 2);
            float remaining = Step, elapsed = 0;
            int events = 0;
            while (remaining > 0) {
                float time = remaining; int one = -1, two = -1, rail = -1, pocket = -1; Vector2 normal = default;
                for (int i = 0; i < 16; i++) {
                    ref Ball a = ref balls[i]; if (a.Down) { continue; }
                    for (int j = i + 1; j < 16; j++) {
                        if (balls[j].Down) { continue; }
                        if (a.Velocity == Vector2.Zero && balls[j].Velocity == Vector2.Zero) { continue; }
                        Vector2 d = a.Position - balls[j].Position, v = a.Velocity - balls[j].Velocity;
                        if (HitCircle(d, v, Table.Radius * 2, time, out float hit)) { time = hit; one = i; two = j; rail = pocket = -1; }
                    }
                    if (a.Velocity.LengthSquared() < 1e-12f) { continue; }
                    for (int p = 0; p < 6; p++) {
                        if (HitCircle(a.Position - Table.Pockets[p], a.Velocity, Table.PocketRadius, time, out float hit, true)) { time = hit; one = i; two = rail = -1; pocket = p; }
                    }
                    for (int r = 0; r < Table.Rails.Length; r++) {
                        if (HitRail(a, Table.Rails[r], time, out float hit, out Vector2 n)) { time = hit; one = i; two = pocket = -1; rail = r; normal = n; }
                    }
                }
                Move(time); elapsed += time; remaining -= time;
                if (one < 0) { break; }
                double stamp = (Tick - 1) * (double)Step + elapsed;
                if (pocket >= 0) {
                    ref Ball a = ref balls[one]; a.Pocket = (byte)(pocket + 1); a.Velocity = default; a.Spin = default;
                    Shot.Potted |= (ushort)(1 << one); Shot.Pockets[one] = (byte)(pocket + 1);
                } else if (two >= 0) {
                    Collide(one, two);
                    if (one == 0) {
                        if (double.IsPositiveInfinity(Shot.FirstAt)) { Shot.FirstAt = stamp; }
                        if (stamp - Shot.FirstAt < 0.000001) { Shot.First |= (ushort)(1 << two); }
                    }
                } else {
                    Bounce(ref balls[one], normal);
                    Shot.Rails |= (ushort)(1 << one); if (Shot.First != 0) { Shot.RailAfter = true; }
                    if (one == 0) {
                        Vector2 inward = Table.Rails[rail].Normal;
                        if (inward.X < 0) { Shot.CueFoot++; }
                        else if (inward.Y != 0) { Shot.CueSide = true; }
                    }
                }
                Contacts++;
                if (++events > 4096) { Fault = true; Moving = false; return; }
            }
            Friction(Step / 2); Moving = false;
            for (int i = 0; i < 16; i++) {
                ref Ball b = ref balls[i]; if (b.Down) { continue; }
                if (b.Position.X < -Table.Radius * 4 || b.Position.X > Table.Length + Table.Radius * 4 || b.Position.Y < -Table.Radius * 4 || b.Position.Y > Table.Width + Table.Radius * 4) {
                    b.Pocket = 7; b.Velocity = default; b.Spin = default; Shot.Off |= (ushort)(1 << i); continue;
                }
                if (b.Velocity.LengthSquared() > 1e-10f || b.Spin.LengthSquared() > 1e-6f) { Moving = true; }
            }
        }

        private void Friction(float dt) {
            float r = Table.Radius;
            for (int i = 0; i < 16; i++) {
                ref Ball b = ref balls[i]; if (b.Down || b.Velocity == Vector2.Zero && b.Spin.IsZero) { continue; }
                Vector2 slip = b.Velocity + new Vector2(-r * b.Spin.Y, r * b.Spin.X);
                float speed = slip.Length(), rollingTime = dt;
                if (speed > 0.00001f) {
                    float slidingTime = MathF.Min(dt, speed / (3.5f * Table.Slide * Gravity));
                    Vector2 dv = slip * (-Table.Slide * Gravity * slidingTime / speed);
                    b.Velocity += dv; b.Spin.X += 2.5f * dv.Y / r; b.Spin.Y -= 2.5f * dv.X / r;
                    rollingTime -= slidingTime;
                }
                if (rollingTime > 0) {
                    float v = b.Velocity.Length();
                    b.Velocity *= v > 0 ? MathF.Max(0, 1 - Table.Roll * Gravity * rollingTime / v) : 0;
                    b.Spin.X = -b.Velocity.Y / r; b.Spin.Y = b.Velocity.X / r;
                }
                float z = MathF.Abs(b.Spin.Z), loss = 2.5f * Table.Spin * Gravity * dt / r;
                b.Spin.Z = MathF.CopySign(MathF.Max(0, z - loss), b.Spin.Z);
            }
        }

        private void Move(float time) {
            if (time <= 0) { return; }
            for (int i = 0; i < 16; i++) {
                ref Ball b = ref balls[i]; if (b.Down || b.Velocity == Vector2.Zero && b.Spin.IsZero) { continue; }
                b.Position += b.Velocity * time;
                if (i == 0 && Shot.First == 0 && b.Position.X > Table.Length / 4) { Shot.BeyondHead = true; }
                if (!b.Spin.IsZero) { b.Orientation = BallRotation.Advance(b.Orientation, b.Spin, time); }
            }
        }

        public static bool HitCircle(Vector2 delta, Vector2 velocity, float radius, float max, out float time, bool capture = false) {
            float c = delta.LengthSquared() - radius * radius, b = Vector2.Dot(delta, velocity), a = velocity.LengthSquared();
            time = 0;
            if (capture && c <= 0) { return true; }
            if (a < 1e-14f || b >= -1e-9f) { return false; }
            if (c <= 0) { return true; }
            double discriminant = (double)b * b - (double)a * c;
            if (discriminant < 0) { return false; }
            time = (float)(c / (-b + Math.Sqrt(discriminant)));
            return time <= max;
        }

        private bool HitRail(in Ball ball, in Rail rail, float max, out float time, out Vector2 normal) {
            time = max; normal = default; bool hit = false;
            float length = rail.Length; Vector2 axis = rail.Axis;
            float vn = Vector2.Dot(ball.Velocity, rail.Normal);
            if (vn < -1e-8f) {
                float t = MathF.Max(0, (Table.Radius - Vector2.Dot(ball.Position - rail.A, rail.Normal)) / vn);
                float along = Vector2.Dot(ball.Position + ball.Velocity * t - rail.A, axis);
                if (t <= max && along >= 0 && along <= length) { time = t; normal = rail.Normal; hit = true; }
            }
            for (int i = 0; i < 2; i++) {
                Vector2 end = i == 0 ? rail.A : rail.B;
                if (!HitCircle(ball.Position - end, ball.Velocity, Table.Radius, time, out float t)) { continue; }
                Vector2 n = ball.Position + ball.Velocity * t - end;
                float d = n.Length(); if (d < 1e-9f) { continue; }
                time = t; normal = n / d; hit = true;
            }
            return hit;
        }

        private void Collide(int i, int j) {
            ref Ball a = ref balls[i]; ref Ball b = ref balls[j];
            Vector2 delta = b.Position - a.Position; float distance = delta.Length();
            Vector2 n = distance > 1e-8f ? delta / distance : Vector2.UnitX;
            float overlap = Table.Radius * 2 + ContactSlop - distance;
            if (overlap > 0) { a.Position -= n * (overlap / 2); b.Position += n * (overlap / 2); }
            float closing = Vector2.Dot(b.Velocity - a.Velocity, n); if (closing >= 0) { return; }
            float impulse = -(1 + Table.Restitution) * closing / 2;
            Vector2 t = new Vector2(-n.Y, n.X);
            float slip = Vector2.Dot(b.Velocity - a.Velocity, t) - Table.Radius * (a.Spin.Z + b.Spin.Z);
            float tangent = Math.Clamp(-slip / 7, -Table.BallFriction * impulse, Table.BallFriction * impulse);
            Vector2 change = n * impulse + t * tangent;
            a.Velocity -= change; b.Velocity += change;
            float spin = -2.5f * tangent / Table.Radius; a.Spin.Z += spin; b.Spin.Z += spin;
        }

        private void Bounce(ref Ball ball, Vector2 n) {
            float closing = Vector2.Dot(ball.Velocity, n); if (closing >= 0) { return; }
            ball.Position += n * ContactSlop;
            float impulse = -(1 + Table.Cushion) * closing;
            Vector2 t = new Vector2(-n.Y, n.X);
            float slip = Vector2.Dot(ball.Velocity, t) - Table.Radius * ball.Spin.Z;
            float tangent = Math.Clamp(-slip / 3.5f, -Table.RailFriction * impulse, Table.RailFriction * impulse);
            ball.Velocity += n * impulse + t * tangent; ball.Spin.Z -= 2.5f * tangent / Table.Radius;
        }

        public double Energy() {
            double energy = 0; float inertia = 0.4f * Table.Mass * Table.Radius * Table.Radius;
            for (int i = 0; i < 16; i++) { if (!balls[i].Down) { energy += 0.5 * Table.Mass * balls[i].Velocity.LengthSquared() + 0.5 * inertia * balls[i].Spin.LengthSquared(); } }
            return energy;
        }
        private static bool Finite(Vector2 p) => float.IsFinite(p.X) && float.IsFinite(p.Y);
    }
}
