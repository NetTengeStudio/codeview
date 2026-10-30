using System;
using System.Collections.Generic;

namespace ScpItdMod.Rec {
    [Flags]
    public enum Witness : byte { None = 0, Seen = 1, Heard = 2 }

    public struct WitnessSpan {
        public Subject Subject;
        public Witness Kind;
        public double From;
        public double To;
        public float X;
        public float Y;
    }

    public sealed class RecallIndex {
        public const int MaxSpans = 1024;
        public const double MergeGap = 2.0;

        private readonly List<WitnessSpan> spans = new List<WitnessSpan>(256);
        private readonly Dictionary<(Subject, Witness), int> open = new Dictionary<(Subject, Witness), int>();

        public int Count => spans.Count;

        public IReadOnlyList<WitnessSpan> Spans => spans;

        public void Clear() {
            spans.Clear();
            open.Clear();
        }

        public void Mark(Subject s, Witness kind, double time, float x = 0.0f, float y = 0.0f) {
            if (kind == Witness.None) { return; }
            if ((kind & Witness.Seen) != 0) { Extend(s, Witness.Seen, time, x, y); }
            if ((kind & Witness.Heard) != 0) { Extend(s, Witness.Heard, time, x, y); }
        }

        private void Extend(Subject s, Witness kind, double time, float x, float y) {
            var key = (s, kind);
            if (open.TryGetValue(key, out int i) && i < spans.Count && spans[i].Subject.Equals(s) && spans[i].Kind == kind && time - spans[i].To <= MergeGap && time >= spans[i].To) {
                WitnessSpan w = spans[i];
                w.To = time;
                spans[i] = w;
                return;
            }
            if (spans.Count >= MaxSpans) { DropOldest(); }
            spans.Add(new WitnessSpan { Subject = s, Kind = kind, From = time, To = time, X = x, Y = y });
            open[key] = spans.Count - 1;
        }

        public void Trim(double before) {
            int n = 0;
            while (n < spans.Count && spans[n].To < before) { n++; }
            if (n == 0) { return; }
            spans.RemoveRange(0, n);
            Reindex();
        }

        private void DropOldest() {
            spans.RemoveRange(0, Math.Max(1, spans.Count / 8));
            Reindex();
        }

        private void Reindex() {
            open.Clear();
            for (int i = 0; i < spans.Count; i++) { open[(spans[i].Subject, spans[i].Kind)] = i; }
        }

        public bool HeardDuring(Subject s, double from, double to) {
            for (int i = spans.Count - 1; i >= 0; i--) {
                WitnessSpan w = spans[i];
                if (w.Kind == Witness.Heard && w.Subject.Equals(s) && w.To >= from && w.From <= to) { return true; }
            }
            return false;
        }
    }

    public struct Episode {
        public Subject Subject;
        public double From;
        public double To;
        public bool Voice;
        public double Duration => To - From;
    }

    public sealed class Recall {
        public const float Threshold = 20.0f;
        public const double MinAge = 30.0;
        public const double MinSeconds = 2.5;
        public const double MaxSeconds = 12.0;

        private readonly List<int> candidates = new List<int>(64);

        public int Played { get; private set; }

        public double Next { get; private set; } = double.NaN;

        public static int Cap(float intensity) => intensity < Threshold ? 0 : Math.Min(12, 2 + (int)((intensity - Threshold) / 8.0f));

        public static double Gap(float intensity, double u) => Math.Max(10.0, 75.0 - intensity * 0.6) * (0.6 + 0.8 * Math.Clamp(u, 0.0, 1.0));

        public void Reset() {
            Played = 0;
            Next = double.NaN;
        }

        public bool Due(double now, float intensity, double u) {
            if (intensity < Threshold || Played >= Cap(intensity)) {
                Next = double.NaN;
                return false;
            }
            if (double.IsNaN(Next)) {
                Next = now + Gap(intensity, u);
                return false;
            }
            return now >= Next;
        }

        public bool Plan(RecallIndex index, double now, float intensity, double u, double v, Func<WitnessSpan, bool> usable, out Episode e) {
            e = default;
            candidates.Clear();
            IReadOnlyList<WitnessSpan> spans = index.Spans;
            for (int i = 0; i < spans.Count; i++) {
                WitnessSpan w = spans[i];
                if (w.Kind != Witness.Seen || w.To > now - MinAge || w.To - w.From < MinSeconds) { continue; }
                if (usable != null && !usable(w)) { continue; }
                candidates.Add(i);
            }
            Next = now + Gap(intensity, v);
            if (candidates.Count == 0) { return false; }
            int pick = candidates[Math.Min(candidates.Count - 1, (int)(Math.Clamp(u, 0.0, 0.999999) * candidates.Count))];
            WitnessSpan s = spans[pick];
            double length = Math.Min(MaxSeconds, s.To - s.From);
            double slack = s.To - s.From - length;
            double from = s.From + slack * Math.Clamp(v, 0.0, 1.0);
            e = new Episode { Subject = s.Subject, From = from, To = from + length };
            e.Voice = index.HeardDuring(s.Subject, e.From, e.To);
            Played++;
            return true;
        }
    }
}
