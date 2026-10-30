using System;
using System.Collections.Generic;
using ScpItdMod.Net;

namespace ScpItdMod.Rec {
    public readonly struct Subject : IEquatable<Subject> {
        public readonly ushort Entity;
        public readonly uint Serial;

        public Subject(ushort entity, uint serial) {
            Entity = entity;
            Serial = serial;
        }

        public bool Equals(Subject o) => Entity == o.Entity && Serial == o.Serial;

        public override bool Equals(object obj) => obj is Subject o && Equals(o);

        public override int GetHashCode() => (int)(Entity | Serial << 16);
    }

    [Flags]
    public enum PoseFlags : byte { None = 0, Left = 1, Dead = 2, Down = 4, Masked = 8, Running = 16, HoldRight = 32, HoldLeft = 64 }

    public enum TapeEvent : byte { Appear, Vanish, Speech, Emote, Shot, Death, Gap, Footstep }

    public struct FigurePose {
        public const int MaxLimbs = 18;
        public const int HeldSlots = 2;
        public const float Unit = 8.0f;
        public const float AngleUnit = (float)(Math.PI * 2.0 / 65536.0);

        public Subject Subject;
        public int Look;
        public PoseFlags Flags;
        public float X;
        public float Y;
        public int Limbs;
        public ushort Missing;
        public LimbArray L;

        public readonly bool ValidLimbs => (uint)Limbs <= MaxLimbs && (Missing >> Math.Max(0, Limbs - HeldSlots)) == 0;
        public readonly bool HasBodyLimb(int index) => (uint)index < (uint)Math.Max(0, Limbs - HeldSlots) && (Missing & (1u << index)) == 0;

        public static short Q(float v) => (short)Math.Clamp((int)MathF.Round(v * Unit), short.MinValue, short.MaxValue);

        public static short QA(float radians) {
            double turns = radians / (Math.PI * 2.0);
            turns -= Math.Floor(turns);
            return unchecked((short)(int)(turns * 65536.0));
        }
    }

    public struct LimbArray {
        [System.Runtime.CompilerServices.InlineArray(FigurePose.MaxLimbs)]
        private struct Values { private short first; }
        private Values x, y, angles;

        public readonly short X(int i) => x[i];
        public readonly short Y(int i) => y[i];
        public readonly short A(int i) => angles[i];

        public void Set(int i, short x, short y, short a) {
            this.x[i] = x;
            this.y[i] = y;
            angles[i] = a;
        }
    }

    public struct TapeNote {
        public double Time;
        public TapeEvent Kind;
        public Subject Subject;
        public uint Value;
        public string Text;
    }

    public interface ILookRefs {
        void Retain(int look);
        void Release(int look);
    }

    public sealed class Segment {
        public double Start;
        public double End;
        public int Frames;
        public byte[] Data = Array.Empty<byte>();
        public int Length;
        public readonly List<TapeNote> Notes = new List<TapeNote>(8);
        public readonly HashSet<int> Looks = new HashSet<int>();
        public bool Thinned;
        public bool Gap;

        public long Bytes => Data.Length + Notes.Count * 40 + Looks.Count * 12 + 96;
    }

    public sealed class Tape {
        public const double SegmentSeconds = 10.0;
        public const int MaxFigures = 32;
        public const int MaxNotes = 48;
        public const int MaxNoteText = 160;

        private readonly List<Segment> segments = new List<Segment>();
        private readonly Dictionary<Subject, int> lastIndex = new Dictionary<Subject, int>();
        private readonly Dictionary<Subject, FigurePose> lastPose = new Dictionary<Subject, FigurePose>();
        private readonly WireWriter w = new WireWriter(4096);
        private readonly List<FigurePose> lastFrame = new List<FigurePose>(MaxFigures);
        private readonly List<FigurePose> carry = new List<FigurePose>(MaxFigures);
        private double carryTime;
        private readonly ILookRefs looks;
        private long budget;
        private readonly double keep;
        private Segment open;
        private int frameCount;
        private int frameAt;
        private double frameTime;
        private long sealedBytes;

        public Tape(long budgetBytes, double keepSeconds, ILookRefs looks = null) {
            budget = budgetBytes;
            keep = keepSeconds;
            this.looks = looks;
        }

        public int Segments => segments.Count + (open != null ? 1 : 0);

        public long Bytes => sealedBytes + (open == null ? 0 : open.Bytes + w.Length);

        public long Budget => budget;
        public Action<double, double> EvictedRange { get; set; }

        public void Resize(long budgetBytes) {
            if (budgetBytes <= 0) { throw new ArgumentOutOfRangeException(nameof(budgetBytes)); }
            budget = budgetBytes;
            Enforce();
        }

        public double Oldest => segments.Count > 0 ? segments[0].Start : open?.Start ?? double.MaxValue;

        public double Newest => open?.End ?? (segments.Count > 0 ? segments[segments.Count - 1].End : double.MinValue);

        public IReadOnlyList<Segment> Sealed => segments;

        public Segment Open => open;

        public int Evicted { get; private set; }

        public int ThinnedCount { get; private set; }

        public void Clear() {
            foreach (Segment s in segments) { ReleaseLooks(s); }
            if (open != null) { ReleaseLooks(open); }
            segments.Clear();
            open = null;
            sealedBytes = 0;
            lastIndex.Clear();
            lastPose.Clear();
            w.Clear();
        }

        public void BeginFrame(double time) {
            bool roll = open != null && !open.Gap && time - open.Start >= SegmentSeconds && time >= open.End && open.Frames > 0;
            if (open != null && (roll || time < open.End || open.Gap && open.Frames == 0 && time > open.Start)) {
                if (open.Gap) { open.End = time; }
                if (roll) {
                    carry.Clear();
                    carry.AddRange(lastFrame);
                    carryTime = open.End;
                }
                Seal();
            }
            if (open == null) {
                open = new Segment { Start = roll ? carryTime : time, End = roll ? carryTime : time };
                w.Clear();
                lastPose.Clear();
                if (roll && carry.Count > 0) {
                    Emit(carryTime);
                    for (int i = 0; i < carry.Count; i++) { Figure(carry[i]); }
                    EndFrame();
                }
            }
            Emit(time);
        }

        private void Emit(double time) {
            lastFrame.Clear();
            frameTime = time;
            frameAt = w.Length;
            w.Var((ulong)Math.Max(0, Math.Round((time - open.Start) * 1000.0)));
            w.U8(0);
            frameCount = 0;
        }

        public void Figure(in FigurePose p) {
            if (open == null || frameCount >= MaxFigures) { return; }
            if (!p.ValidLimbs || !float.IsFinite(p.X) || !float.IsFinite(p.Y)) { throw new ArgumentOutOfRangeException(nameof(p)); }
            frameCount++;
            bool known = lastPose.TryGetValue(p.Subject, out FigurePose prev);
            float dx = MathF.Round((p.X - prev.X) * FigurePose.Unit), dy = MathF.Round((p.Y - prev.Y) * FigurePose.Unit);
            bool first = !known || dx < short.MinValue || dx > short.MaxValue || dy < short.MinValue || dy > short.MaxValue;
            w.Var(p.Subject.Entity);
            w.Var(p.Subject.Serial);
            w.U8((byte)((byte)p.Flags | (first ? 0x80 : 0)));
            w.Var((ulong)Math.Max(0, p.Look));
            int limbs = Math.Clamp(p.Limbs, 0, FigurePose.MaxLimbs);
            w.U8((byte)limbs); w.U16(p.Missing);
            if (first) {
                w.U32((uint)BitConverter.SingleToInt32Bits(p.X));
                w.U32((uint)BitConverter.SingleToInt32Bits(p.Y));
                for (int i = 0; i < limbs; i++) {
                    Zig(p.L.X(i));
                    Zig(p.L.Y(i));
                    Zig(p.L.A(i));
                }
            } else {
                Zig((short)dx);
                Zig((short)dy);
                for (int i = 0; i < limbs; i++) {
                    Zig((short)(p.L.X(i) - prev.L.X(i)));
                    Zig((short)(p.L.Y(i) - prev.L.Y(i)));
                    Zig(unchecked((short)(p.L.A(i) - prev.L.A(i))));
                }
            }
            FigurePose stored = p;
            if (!first) {
                stored.X = prev.X + dx / FigurePose.Unit;
                stored.Y = prev.Y + dy / FigurePose.Unit;
            }
            lastPose[p.Subject] = stored;
            lastFrame.Add(stored);
            if (p.Look > 0 && open.Looks.Add(p.Look)) { looks?.Retain(p.Look); }
        }

        public void EndFrame() {
            if (open == null) { return; }
            w.Buffer[FrameCountOffset()] = (byte)frameCount;
            open.Frames++;
            open.End = frameTime;
        }

        private int FrameCountOffset() {
            int i = frameAt;
            while ((w.Buffer[i] & 0x80) != 0) { i++; }
            return i + 1;
        }

        public void Note(in TapeNote note) {
            if (open == null) { BeginFrame(note.Time); EndFrame(); }
            if (open.Notes.Count >= MaxNotes) { return; }
            TapeNote n = note;
            if (n.Text != null && n.Text.Length > MaxNoteText) { n.Text = n.Text.Substring(0, MaxNoteText); }
            open.Notes.Add(n);
        }

        public void MarkGap(double time) {
            if (open != null) { Seal(); }
            open = new Segment { Start = time, End = time, Gap = true };
            w.Clear();
            lastPose.Clear();
        }

        public void Seal() {
            if (open == null) { return; }
            if (open.Data.Length != w.Length) { open.Data = w.Length == 0 ? Array.Empty<byte>() : new byte[w.Length]; }
            Buffer.BlockCopy(w.Buffer, 0, open.Data, 0, w.Length);
            open.Length = w.Length;
            segments.Add(open);
            sealedBytes += open.Bytes;
            open = null;
            w.Clear();
            lastPose.Clear();
            Enforce();
        }

        public void Enforce() {
            double newest = Newest;
            while (segments.Count > 0 && (newest - segments[0].End > keep)) { Evict(0); }
            if (sealedBytes <= budget) { return; }
            for (int i = 0; i < segments.Count && sealedBytes > budget; i++) {
                if (segments[i].Thinned || i >= segments.Count / 2) { continue; }
                Thin(segments[i]);
            }
            while (segments.Count > 1 && sealedBytes > budget) { Evict(0); }
        }

        private void Evict(int index) {
            Segment s = segments[index];
            sealedBytes -= s.Bytes;
            ReleaseLooks(s);
            segments.RemoveAt(index);
            Evicted++;
            EvictedRange?.Invoke(s.Start, s.End);
        }

        private void ReleaseLooks(Segment s) {
            if (looks == null) { return; }
            foreach (int l in s.Looks) { looks.Release(l); }
        }

        private void Thin(Segment s) {
            var tmp = new Tape(long.MaxValue, double.MaxValue);
            tmp.open = new Segment { Start = s.Start, End = s.Start };
            // Decode once, emitting even frames immediately. Keep only one odd frame
            // to preserve the original segment's final frame when its index is odd.
            FigurePose[] lastOdd = new FigurePose[MaxFigures];
            double lastOddTime = 0;
            int lastOddCount = 0, frames = 0;
            Decode(s, double.MaxValue, (time, poses) => {
                if ((frames++ & 1) == 0) {
                    AppendThinFrame(tmp, s.Start, time, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(poses));
                } else {
                    lastOddTime = time;
                    lastOddCount = poses.Count;
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(poses).CopyTo(lastOdd);
                }
            });
            if ((frames & 1) == 0 && frames > 0) {
                AppendThinFrame(tmp, s.Start, lastOddTime, lastOdd.AsSpan(0, lastOddCount));
            }
            sealedBytes -= s.Bytes;
            s.Data = tmp.w.Span.ToArray();
            s.Length = s.Data.Length;
            s.Frames = tmp.open.Frames;
            s.Thinned = true;
            sealedBytes += s.Bytes;
            ThinnedCount++;
        }

        private static void AppendThinFrame(Tape tape, double start, double time, ReadOnlySpan<FigurePose> poses) {
            tape.frameTime = time;
            tape.frameAt = tape.w.Length;
            tape.w.Var((ulong)Math.Max(0, Math.Round((time - start) * 1000.0)));
            tape.w.U8(0);
            tape.frameCount = 0;
            for (int i = 0; i < poses.Length; i++) { tape.Figure(poses[i]); }
            tape.EndFrame();
        }

        public bool Insert(Segment s) {
            if (s == null) { return false; }
            foreach (int id in s.Looks) { looks?.Retain(id); }
            int at = 0;
            while (at < segments.Count && segments[at].Start < s.Start) { at++; }
            if (at < segments.Count && Math.Abs(segments[at].Start - s.Start) < 1e-6) {
                sealedBytes -= segments[at].Bytes;
                ReleaseLooks(segments[at]);
                segments[at] = s;
            } else {
                segments.Insert(at, s);
            }
            sealedBytes += s.Bytes;
            while (segments.Count > 1 && sealedBytes > budget) {
                int victim = at >= segments.Count / 2 ? 0 : segments.Count - 1;
                if (ReferenceEquals(segments[victim], s)) { break; }
                Evict(victim);
                if (victim < at) { at--; }
            }
            return true;
        }

        public bool Covers(double t0, double t1) {
            double at = t0;
            for (int i = 0; i < segments.Count && at < t1; i++) {
                if (segments[i].Start > at + 0.25) { return false; }
                if (segments[i].End > at) { at = segments[i].End; }
            }
            return at >= t1 - 0.25;
        }

        public int Find(double time) {
            int lo = 0;
            int hi = segments.Count - 1;
            while (lo <= hi) {
                int mid = (lo + hi) >> 1;
                if (segments[mid].End < time) { lo = mid + 1; } else if (segments[mid].Start > time) { hi = mid - 1; } else { return mid; }
            }
            return -1;
        }

        public Segment At(double time) {
            if (open != null && time >= open.Start) { return time <= open.End + 0.5 ? open : null; }
            int i = Find(time);
            return i < 0 ? null : segments[i];
        }

        public ReadOnlySpan<byte> DataOf(Segment s) => ReferenceEquals(s, open) ? w.Span : new ReadOnlySpan<byte>(s.Data, 0, s.Length);

        public void Decode(Segment s, double until, Action<double, List<FigurePose>> onFrame) {
            var poses = new List<FigurePose>(MaxFigures);
            var prev = new Dictionary<Subject, FigurePose>();
            Decode(DataOf(s), s.Start, until, prev, poses, onFrame);
        }

        public static void Decode(ReadOnlySpan<byte> data, double start, double until, Dictionary<Subject, FigurePose> prev, List<FigurePose> poses, Action<double, List<FigurePose>> onFrame) {
            var r = new WireReader(data);
            while (!r.End && !r.Failed) {
                double time = start + r.Var() / 1000.0;
                int count = r.U8();
                if (r.Failed || count > MaxFigures || time > until) { break; }
                poses.Clear();
                for (int k = 0; k < count && !r.Failed; k++) {
                    if (!ReadFigure(ref r, prev, out FigurePose p)) { return; }
                    poses.Add(p);
                }
                if (!r.Failed) { onFrame(time, poses); }
            }
        }

        internal static bool ReadFigure(ref WireReader r, Dictionary<Subject, FigurePose> prev, out FigurePose p) {
            var subject = new Subject((ushort)r.Count(ushort.MaxValue), (uint)r.Var());
            byte flags = r.U8();
            p = new FigurePose { Subject = subject, Flags = (PoseFlags)(flags & 0x7F), Look = r.Count(CamWire.MaxLooks), Limbs = r.U8(), Missing = r.U16() };
            if (r.Failed || !p.ValidLimbs) { return false; }
            if ((flags & 0x80) != 0) {
                p.X = BitConverter.Int32BitsToSingle((int)r.U32());
                p.Y = BitConverter.Int32BitsToSingle((int)r.U32());
                for (int i = 0; i < p.Limbs; i++) { p.L.Set(i, Unzig(ref r), Unzig(ref r), Unzig(ref r)); }
            } else {
                if (!prev.TryGetValue(subject, out FigurePose b)) { return false; }
                p.X = b.X + Unzig(ref r) / FigurePose.Unit;
                p.Y = b.Y + Unzig(ref r) / FigurePose.Unit;
                for (int i = 0; i < p.Limbs; i++) {
                    short dx = Unzig(ref r), dy = Unzig(ref r), da = Unzig(ref r);
                    p.L.Set(i, (short)(b.L.X(i) + dx), (short)(b.L.Y(i) + dy), unchecked((short)(b.L.A(i) + da)));
                }
            }
            if (r.Failed || !float.IsFinite(p.X) || !float.IsFinite(p.Y)) { return false; }
            prev[subject] = p;
            return true;
        }

        private void Zig(short v) => w.Var((ulong)(uint)((v << 1) ^ (v >> 31)));

        private static short Unzig(ref WireReader r) {
            uint z = (uint)r.Var();
            return unchecked((short)(int)((z >> 1) ^ (uint)-(int)(z & 1)));
        }
    }
}
