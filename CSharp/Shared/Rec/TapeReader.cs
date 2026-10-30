using System;
using System.Collections.Generic;
using ScpItdMod.Net;

namespace ScpItdMod.Rec {
    public sealed class TapeReader {
        private readonly Dictionary<Subject, FigurePose> prev = new Dictionary<Subject, FigurePose>();
        private readonly List<FigurePose> before = new List<FigurePose>(Tape.MaxFigures);
        private readonly List<FigurePose> after = new List<FigurePose>(Tape.MaxFigures);
        private readonly List<FigurePose> scratch = new List<FigurePose>(Tape.MaxFigures);
        private Segment segment;
        private int position;
        private double beforeTime = double.NaN;
        private double afterTime = double.NaN;

        public int Decoded { get; private set; }

        public int Seeks { get; private set; }

        public bool InGap { get; private set; }

        public void Reset() {
            segment = null;
            prev.Clear();
            before.Clear();
            after.Clear();
            position = 0;
            beforeTime = afterTime = double.NaN;
        }

        public bool Read(Tape tape, double time, List<FigurePose> output) {
            output.Clear();
            Segment s = tape.At(time);
            InGap = s == null || s.Gap;
            if (s == null) { return false; }
            if (!ReferenceEquals(s, segment) || time < beforeTime) { Seek(s); }
            ReadOnlySpan<byte> data = tape.DataOf(s);
            while (!(afterTime > time) && position < data.Length) {
                if (!Next(data, s.Start)) { break; }
            }
            if (double.IsNaN(afterTime)) { return false; }
            if (!(afterTime > time) || double.IsNaN(beforeTime)) {
                output.AddRange(after);
                return true;
            }
            float k = (float)((time - beforeTime) / Math.Max(1e-6, afterTime - beforeTime));
            for (int i = 0; i < before.Count; i++) {
                FigurePose a = before[i];
                FigurePose b = a;
                bool found = false;
                for (int j = 0; j < after.Count; j++) {
                    if (after[j].Subject.Equals(a.Subject)) {
                        b = after[j];
                        found = true;
                        break;
                    }
                }
                output.Add(found ? Lerp(a, b, k) : a);
            }
            return true;
        }

        private void Seek(Segment s) {
            Seeks++;
            segment = s;
            prev.Clear();
            before.Clear();
            after.Clear();
            position = 0;
            beforeTime = afterTime = double.NaN;
        }

        private bool Next(ReadOnlySpan<byte> data, double start) {
            scratch.Clear();
            ReadOnlySpan<byte> frame = data.Slice(position);
            var fr = new WireReader(frame);
            double t = start + fr.Var() / 1000.0;
            int count = fr.U8();
            if (fr.Failed || count > Tape.MaxFigures) { return false; }
            for (int k = 0; k < count; k++) {
                if (!Tape.ReadFigure(ref fr, prev, out FigurePose pose)) { return false; }
                scratch.Add(pose);
            }
            if (fr.Failed) { return false; }
            position += frame.Length - fr.Remaining;
            Decoded++;
            before.Clear();
            before.AddRange(after);
            beforeTime = afterTime;
            after.Clear();
            after.AddRange(scratch);
            afterTime = t;
            if (double.IsNaN(beforeTime)) {
                before.AddRange(after);
                beforeTime = t;
            }
            return true;
        }

        public static FigurePose Lerp(in FigurePose a, in FigurePose b, float k) {
            if (MathF.Abs(b.X - a.X) > short.MaxValue / FigurePose.Unit || MathF.Abs(b.Y - a.Y) > short.MaxValue / FigurePose.Unit) { return k < 1 ? a : b; }
            FigurePose p = k < 0.5f ? a : b;
            p.X = a.X + (b.X - a.X) * k;
            p.Y = a.Y + (b.Y - a.Y) * k;
            if ((a.Flags & PoseFlags.Left) != (b.Flags & PoseFlags.Left) || a.Limbs != b.Limbs) { return p; }
            for (int i = 0; i < a.Limbs; i++) {
                if (i < a.Limbs - FigurePose.HeldSlots && (!a.HasBodyLimb(i) || !b.HasBodyLimb(i))) { continue; }
                short ax = a.L.X(i), ay = a.L.Y(i), aa = a.L.A(i);
                short da = unchecked((short)(b.L.A(i) - aa));
                p.L.Set(i, (short)(ax + (b.L.X(i) - ax) * k), (short)(ay + (b.L.Y(i) - ay) * k), unchecked((short)(aa + (int)(da * k))));
            }
            return p;
        }

    }
}
