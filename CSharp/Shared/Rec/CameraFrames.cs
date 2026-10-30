using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ScpItdMod.Rec {
    public sealed class CameraFrames {
        private readonly FigurePose[] live = new FigurePose[Tape.MaxFigures];
        private readonly Tape tape;
        public Tape Archive => tape;
        private bool recording, collecting;
        public int Count { get; private set; }
        public double Time { get; private set; }
        public bool Available { get; private set; }

        public CameraFrames(Tape tape) => this.tape = tape ?? throw new ArgumentNullException(nameof(tape));

        public bool Begin(double time, bool available, bool record) {
            if (!double.IsFinite(time) || time < 0) { throw new ArgumentOutOfRangeException(nameof(time)); }
            if (collecting) { throw new InvalidOperationException("Camera frame is already open"); }
            if (recording && (!available || !record)) { tape.MarkGap(time); }
            Available = available; Time = time; Count = 0; recording = available && record; collecting = available;
            if (recording) { tape.BeginFrame(time); }
            return available;
        }
        public void Figure(in FigurePose pose) {
            if (!collecting) { throw new InvalidOperationException("Camera frame is not open"); }
            if (Count >= live.Length) { return; }
            live[Count++] = pose;
            if (recording) { tape.Figure(pose); }
        }
        public void End() {
            if (!collecting) { return; }
            if (recording) { tape.EndFrame(); }
            collecting = false;
        }
        public ScreenState Read(double requested, TapeReader reader, List<FigurePose> into, out double time) {
            into.Clear(); time = Time;
            if (requested == double.MaxValue) {
                if (!Available) { return ScreenState.NoPower; }
                into.EnsureCapacity(Count); CollectionsMarshal.SetCount(into, Count);
                live.AsSpan(0, Count).CopyTo(CollectionsMarshal.AsSpan(into));
                return ScreenState.Picture;
            }
            if (!double.IsFinite(requested) || tape.Segments == 0) { return ScreenState.Waiting; }
            time = Math.Clamp(requested, tape.Oldest, tape.Newest);
            return reader.Read(tape, time, into) ? ScreenState.Picture : reader.InGap ? ScreenState.Gap : ScreenState.Waiting;
        }
    }
}
