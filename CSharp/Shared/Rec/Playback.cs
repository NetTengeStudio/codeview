using System;
using System.Collections.Generic;

namespace ScpItdMod.Rec {
    public enum ViewState : byte { Idle, Loading, Playing, Paused, Gap, End }

    public sealed class Playback {
        public const double FirstWindow = 10.0;
        public const double Window = 30.0;
        public const double Lead = 4.0;
        public const double Behind = 1.0;
        public const int MaxKnown = 64;
        public static readonly float[] Speeds = { 0.25f, 0.5f, 1.0f, 2.0f, 4.0f, 8.0f };

        private readonly List<(double From, double To)> known = new List<(double, double)>(16);

        public double Oldest { get; private set; } = double.NaN;
        public double Newest { get; private set; } = double.NaN;
        public double Position { get; private set; }
        public bool Playing { get; private set; }
        public int SpeedIndex { get; private set; } = 2;
        public uint Generation { get; private set; }
        public bool Pending { get; private set; }
        public double RequestFrom { get; private set; }
        public double RequestTo { get; private set; }
        public double RequestedAt { get; private set; }
        public bool Wanted { get; private set; }
        public bool Live { get; private set; }

        public float Speed => Speeds[SpeedIndex];

        public bool HasRange => !double.IsNaN(Oldest) && !double.IsNaN(Newest) && Newest >= Oldest;

        public IReadOnlyList<(double From, double To)> Known => known;

        public void Reset() {
            known.Clear();
            Oldest = Newest = double.NaN;
            Position = 0;
            Playing = false;
            Live = false;
            SpeedIndex = 2;
            Pending = false;
            Wanted = false;
            Generation++;
        }

        public void SetRange(double oldest, double newest) {
            if (!double.IsFinite(oldest) || !double.IsFinite(newest) || newest < oldest) {
                Oldest = Newest = double.NaN;
                return;
            }
            bool first = !HasRange;
            Oldest = oldest;
            Newest = newest;
            if (first) {
                Position = Math.Max(oldest, newest - 30.0);
                Want();
            }
            if (Live) {
                Position = Newest;
                if (!Covered(Math.Max(Oldest, Newest - 0.5), Newest)) { Want(); }
            }
            Position = Math.Clamp(Position, Oldest, Newest);
            for (int i = known.Count - 1; i >= 0; i--) {
                if (known[i].To < Oldest) { known.RemoveAt(i); }
            }
        }

        public void GoLive() {
            if (!HasRange) { Playing = false; Live = true; return; }
            Seek(Newest);
            Playing = false;
            Live = true;
        }

        public void Seek(double t) {
            if (!HasRange) { return; }
            Live = false;
            Position = Math.Clamp(t, Oldest, Newest);
            if (!Covered(Position, Math.Min(Newest, Position + 0.5))) {
                Pending = false;
                Want();
            }
        }

        public void Step(double seconds) => Seek(Position + seconds);

        public void Toggle() => SetPlaying(!Playing);

        public void SetPlaying(bool on) {
            Playing = on && HasRange;
            if (Playing && Position >= Newest - 0.05) { Seek(Math.Max(Oldest, Newest - 30.0)); }
        }

        public void Faster() => SpeedIndex = Math.Min(Speeds.Length - 1, SpeedIndex + 1);

        public void Slower() => SpeedIndex = Math.Max(0, SpeedIndex - 1);

        public void Advance(double dt) {
            if (!HasRange) { return; }
            if (Playing && !Loading) {
                Position += dt * Speed;
                if (Position >= Newest) {
                    Position = Newest;
                    Playing = false;
                }
            }
            if (!Pending && !Covered(Position, Math.Min(Newest, Position + Lead))) { Want(); }
        }

        public bool Loading => Pending && !Covered(Position, Math.Min(Newest, Position + 0.25));

        public ViewState State(bool gapAtPosition) {
            if (!HasRange) { return ViewState.Idle; }
            if (Loading) { return ViewState.Loading; }
            if (gapAtPosition) { return ViewState.Gap; }
            if (Playing) { return ViewState.Playing; }
            return Position >= Newest - 0.05 ? ViewState.End : ViewState.Paused;
        }

        private void Want() => Wanted = true;

        public bool TakeRequest(double now, out uint generation, out double from, out double to) {
            generation = 0;
            from = to = 0;
            if (!Wanted || !HasRange) { return false; }
            Wanted = false;
            Generation++;
            double start = Math.Max(Oldest, Position - Behind);
            bool here = Covered(Position, Math.Min(Newest, Position + 0.25));
            for (int i = 0; i < known.Count; i++) {
                if (known[i].From <= start + 1e-6 && known[i].To > start) { start = known[i].To; }
            }
            from = start;
            to = Math.Min(Newest, start + (here ? Window : FirstWindow));
            if (to - from < 0.05) { return false; }
            RequestFrom = from;
            RequestTo = to;
            RequestedAt = now;
            Pending = true;
            generation = Generation;
            return true;
        }

        public bool Accept(uint generation) => Pending && generation == Generation;

        public void Done(uint generation, bool complete = true) {
            if (generation != Generation) { return; }
            Pending = false;
            if (complete) { Mark(RequestFrom, RequestTo); }
        }

        public void Denied(uint generation) {
            if (generation != Generation) { return; }
            Pending = false;
            Playing = false;
        }

        public void Forget() {
            known.Clear();
            Pending = false;
            Generation++;
            Want();
        }

        public void ForgetRange(double from, double to) {
            for (int i = known.Count - 1; i >= 0; i--) {
                var range = known[i];
                if (to <= range.From || from >= range.To) { continue; }
                if (from <= range.From && to >= range.To) { known.RemoveAt(i); }
                else if (from <= range.From) { known[i] = (to, range.To); }
                else if (to >= range.To) { known[i] = (range.From, from); }
                else { known[i] = (range.From, from); known.Insert(i + 1, (to, range.To)); }
            }
            while (known.Count > MaxKnown) { known.RemoveAt(0); }
        }

        public void Mark(double from, double to) {
            if (to <= from) { return; }
            int i = 0;
            while (i < known.Count && known[i].To < from - 0.25) { i++; }
            double a = from;
            double b = to;
            while (i < known.Count && known[i].From <= b + 0.25) {
                a = Math.Min(a, known[i].From);
                b = Math.Max(b, known[i].To);
                known.RemoveAt(i);
            }
            known.Insert(i, (a, b));
            while (known.Count > MaxKnown) { known.RemoveAt(0); }
        }

        public bool Covered(double from, double to) {
            for (int i = 0; i < known.Count; i++) {
                if (known[i].From <= from + 1e-6 && known[i].To >= to - 1e-6) { return true; }
            }
            return false;
        }
    }
}
