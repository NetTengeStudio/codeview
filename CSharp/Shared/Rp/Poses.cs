using System;
using ScpItdMod.Core;

namespace ScpItdMod.Rp {
    public sealed class HandTrack {
        public const int Capacity = 40;

        private readonly float[] t = new float[Capacity];
        private readonly Vec2[] p = new Vec2[Capacity];
        private readonly Vec2[] v = new Vec2[Capacity];
        private readonly bool[] auto = new bool[Capacity];
        private int n;

        public int Count => n;

        public float End => n == 0 ? 0.0f : t[n - 1];

        public float Start => n == 0 ? 0.0f : t[0];

        public bool Active => n > 0;

        public void Clear() => n = 0;

        public void Key(float time, Vec2 pos) => Add(time, pos, Vec2.Zero, true);

        public void Hold(float time, Vec2 pos) => Add(time, pos, Vec2.Zero, false);

        public void Moving(float time, Vec2 pos, Vec2 vel) => Add(time, pos, vel, false);

        private void Add(float time, Vec2 pos, Vec2 vel, bool isAuto) {
            if (n >= Capacity) { return; }
            if (n > 0 && time <= t[n - 1] + 1e-4f) { time = t[n - 1] + 1e-4f; }
            t[n] = time;
            p[n] = pos;
            v[n] = vel;
            auto[n] = isAuto;
            n++;
        }

        public void Smooth() {
            for (int i = 0; i < n; i++) {
                if (!auto[i]) { continue; }
                if (i == 0 || i == n - 1) {
                    v[i] = Vec2.Zero;
                    continue;
                }
                Vec2 before = (p[i] - p[i - 1]) / (t[i] - t[i - 1]);
                Vec2 after = (p[i + 1] - p[i]) / (t[i + 1] - t[i]);
                Vec2 slope = (p[i + 1] - p[i - 1]) / (t[i + 1] - t[i - 1]);
                v[i] = new Vec2(Limit(slope.X, before.X, after.X), Limit(slope.Y, before.Y, after.Y));
            }
        }

        public void Sample(float time, out Vec2 pos, out Vec2 vel) {
            if (n == 0) {
                pos = Vec2.Zero;
                vel = Vec2.Zero;
                return;
            }
            if (time <= t[0]) {
                pos = p[0];
                vel = Vec2.Zero;
                return;
            }
            if (time >= t[n - 1]) {
                pos = p[n - 1];
                vel = Vec2.Zero;
                return;
            }
            int lo = 0;
            int hi = n - 1;
            while (hi - lo > 1) {
                int mid = (lo + hi) >> 1;
                if (t[mid] <= time) { lo = mid; } else { hi = mid; }
            }
            float h = t[hi] - t[lo];
            float u = (time - t[lo]) / h;
            float u2 = u * u;
            float u3 = u2 * u;
            Vec2 m0 = v[lo] * h;
            Vec2 m1 = v[hi] * h;
            pos = (2 * u3 - 3 * u2 + 1) * p[lo] + (u3 - 2 * u2 + u) * m0 + (-2 * u3 + 3 * u2) * p[hi] + (u3 - u2) * m1;
            vel = ((6 * u2 - 6 * u) * p[lo] + (3 * u2 - 4 * u + 1) * m0 + (-6 * u2 + 6 * u) * p[hi] + (3 * u2 - 2 * u) * m1) / h;
        }

        private static float Limit(float slope, float before, float after) {
            if (before * after <= 0.0f) { return 0.0f; }
            float cap = 3.0f * MathF.Min(MathF.Abs(before), MathF.Abs(after));
            return Math.Clamp(slope, -cap, cap);
        }
    }

    public struct PoseFrame {
        public Vec2 Head;
        public Vec2 RightStart;
        public Vec2 LeftStart;
        public float Arm;
    }

    public static class Poses {
        public const float Lead = 0.38f;
        public const float Return = 0.45f;
        public const float MinHold = 0.7f;
        public const float FadeIn = Lead * 0.7f;
        public const float StanceRate = 5.0f;

        public static float SoundDelay(EmotePose pose) => pose == EmotePose.None ? 0.0f : Lead;

        public static float Total(EmotePose pose, float soundSeconds) =>
            pose == EmotePose.None ? soundSeconds : Lead + MathF.Max(soundSeconds, MinHold) + Return;

        public static float FadeAt(EmotePose pose, float soundSeconds) =>
            pose == EmotePose.None ? soundSeconds : Lead + MathF.Max(soundSeconds, MinHold);

        public static float Weight(float t, float fadeAt) => IkBlend.Weight(t, FadeIn, fadeAt, Return);

        public static void HandsUp(float arm, out Vec2 right, out Vec2 left) {
            right = new Vec2(arm * 0.24f, arm * 0.8f);
            left = new Vec2(arm * 0.1f, arm * 0.82f);
        }

        public static void Build(EmotePose pose, float seconds, float[] beats, in PoseFrame f, HandTrack right, HandTrack left) {
            right.Clear();
            left.Clear();
            if (pose == EmotePose.None) { return; }
            float a = f.Arm;
            float hold = MathF.Max(seconds, MinHold);
            float end = Lead + hold;
            Vec2 chest = new Vec2(a * 0.42f, -a * 0.12f);
            switch (pose) {
                case EmotePose.Salute:
                    Stay(right, f.RightStart, f.Head + new Vec2(a * 0.14f, a * 0.04f), end, a);
                    break;
                case EmotePose.Point:
                    Stay(right, f.RightStart, new Vec2(a * 0.93f, a * 0.08f), end, a);
                    break;
                case EmotePose.Mouth:
                    Stay(right, f.RightStart, f.Head + new Vec2(a * 0.16f, -a * 0.12f), end, a);
                    break;
                case EmotePose.Face:
                    Stay(right, f.RightStart, f.Head + new Vec2(a * 0.16f, -a * 0.02f), end, a);
                    Stay(left, f.LeftStart, f.Head + new Vec2(a * 0.12f, -a * 0.06f), end, a);
                    break;
                case EmotePose.Wave: {
                    Vec2 up = f.Head + new Vec2(a * 0.25f, a * 0.4f);
                    right.Hold(0.0f, f.RightStart);
                    right.Key(Lead, up);
                    int swings = Math.Max(2, (int)(hold / 0.36f));
                    for (int i = 1; i <= swings; i++) {
                        float x = (i & 1) == 1 ? a * 0.14f : -a * 0.06f;
                        right.Key(Lead + hold * i / (swings + 1), up + new Vec2(x, -MathF.Abs(x) * 0.25f));
                    }
                    right.Hold(end, up);
                    right.Smooth();
                    break;
                }
                case EmotePose.Snap: {
                    Vec2 raised = new Vec2(a * 0.38f, a * 0.08f);
                    right.Hold(0.0f, f.RightStart);
                    right.Key(Lead * 0.9f, raised);
                    float beat = Lead + First(beats);
                    right.Moving(beat, raised + new Vec2(a * 0.03f, -a * 0.02f), new Vec2(0.0f, -a * 1.2f));
                    right.Key(beat + 0.12f, raised + new Vec2(0.0f, -a * 0.05f));
                    right.Hold(end, raised + new Vec2(-a * 0.02f, -a * 0.08f));
                    right.Smooth();
                    break;
                }
                case EmotePose.Thump: {
                    Vec2 cocked = new Vec2(a * 0.45f, -a * 0.02f);
                    Vec2 hit = new Vec2(a * 0.16f, -a * 0.1f);
                    right.Hold(0.0f, f.RightStart);
                    float beat = Lead + First(beats);
                    right.Key(MathF.Max(Lead * 0.6f, beat - 0.14f), cocked);
                    right.Hold(beat, hit);
                    right.Key(beat + 0.18f, hit + new Vec2(a * 0.08f, 0.0f));
                    right.Hold(end, hit + new Vec2(a * 0.1f, -a * 0.05f));
                    right.Smooth();
                    break;
                }
                case EmotePose.Knock: {
                    Vec2 door = new Vec2(a * 0.72f, a * 0.12f);
                    Vec2 back = door - new Vec2(a * 0.12f, 0.0f);
                    right.Hold(0.0f, f.RightStart);
                    right.Key(Lead * 0.85f, back);
                    Beats(right, beats, door, back);
                    right.Hold(end, back);
                    right.Smooth();
                    break;
                }
                case EmotePose.Clap: {
                    Vec2 meet = chest;
                    Vec2 openR = meet + new Vec2(a * 0.12f, a * 0.03f);
                    Vec2 openL = meet - new Vec2(a * 0.14f, -a * 0.02f);
                    right.Hold(0.0f, f.RightStart);
                    left.Hold(0.0f, f.LeftStart);
                    right.Key(Lead * 0.8f, openR);
                    left.Key(Lead * 0.8f, openL);
                    for (int i = 0; i < beats.Length; i++) {
                        float bt = Lead + beats[i];
                        if (bt <= Lead * 0.8f + 0.02f) { bt = Lead * 0.8f + 0.05f; }
                        right.Hold(bt, meet + new Vec2(a * 0.015f, 0.0f));
                        left.Hold(bt, meet - new Vec2(a * 0.015f, 0.0f));
                        float next = i + 1 < beats.Length ? Lead + beats[i + 1] : bt + 0.3f;
                        float mid = (bt + next) * 0.5f;
                        right.Key(mid, openR);
                        left.Key(mid, openL);
                    }
                    right.Hold(end, openR);
                    left.Hold(end, openL);
                    right.Smooth();
                    left.Smooth();
                    break;
                }
            }
        }

        private static void Stay(HandTrack h, Vec2 start, Vec2 at, float end, float arm) {
            Vec2 path = at - start;
            float len = path.Length();
            Vec2 over = len > 1e-4f ? path * (arm * 0.045f / len) : Vec2.Zero;
            h.Hold(0.0f, start);
            h.Key(Lead * 0.82f, at + over);
            h.Hold(Lead * 1.3f, at);
            h.Hold(end, at);
            h.Smooth();
        }

        private static void Beats(HandTrack h, float[] beats, Vec2 hit, Vec2 back) {
            for (int i = 0; i < beats.Length; i++) {
                float bt = Lead + beats[i];
                h.Hold(bt, hit);
                float next = i + 1 < beats.Length ? Lead + beats[i + 1] : bt + 0.2f;
                h.Key((bt + next) * 0.5f, back);
            }
        }

        private static float First(float[] beats) => beats.Length > 0 ? beats[0] : 0.0f;
    }
}
