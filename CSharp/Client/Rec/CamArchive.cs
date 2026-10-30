using System;
using System.Collections.Generic;
using ScpItdMod.Net;

namespace ScpItdMod.Rec {
    public sealed class CamArchive {
        public const long TapeBudget = 6L << 20;
        public const long VoiceBudget = 3L << 20;
        public const int MaxLooks = CamWire.MaxLooks;

        private readonly WireWriter assembling = new WireWriter(64 * 1024);
        private int expectPart;
        private int expectedParts;
        private readonly HashSet<int> pendingLooks = new HashSet<int>();
        private uint pendingGeneration;

        public readonly LookTable Looks = new LookTable(MaxLooks);
        public readonly Tape Tape;
        public readonly VoiceStore Voice = new VoiceStore(VoiceBudget, double.MaxValue);
        public readonly Playback View = new Playback();

        public CamArchive() {
            Tape = new Tape(TapeBudget, double.MaxValue, Looks);
            Tape.EvictedRange = View.ForgetRange;
        }

        public ushort Camera { get; private set; }

        public int Dropped { get; private set; }

        public int Segments { get; private set; }

        public long Bytes => Tape.Bytes + Voice.Bytes + Looks.Bytes;

        public void Reset(ushort camera) {
            Camera = camera;
            Tape.Clear();
            Voice.Clear();
            Looks.Clear();
            View.Reset();
            assembling.Clear();
            expectPart = 0;
            expectedParts = 0;
            pendingLooks.Clear(); pendingGeneration = 0;
        }

        private void ReleaseLooks() {
            foreach (int id in pendingLooks) { Looks.Release(id); }
            pendingLooks.Clear();
        }

        private void Invalidate() {
            Tape.Clear(); Voice.Clear(); Looks.Clear(); pendingLooks.Clear();
            assembling.Clear(); expectPart = expectedParts = 0;
            View.Forget(); Dropped++;
        }

        public bool Chunk(uint generation, ushort camera, ref WireReader r) {
            if (camera != Camera || !View.Accept(generation)) {
                Dropped++;
                return false;
            }
            if (pendingGeneration != generation) { ReleaseLooks(); pendingGeneration = generation; assembling.Clear(); expectPart = expectedParts = 0; }
            var kind = (ChunkKind)r.U8();
            switch (kind) {
                case ChunkKind.Looks: {
                    uint count = r.U32();
                    if (count > MaxLooks) { return false; }
                    for (uint i = 0; i < count && !r.Failed; i++) {
                        int id = r.Count(MaxLooks);
                        LookDesc d = LookDesc.Read(ref r);
                        if (r.Failed) { return false; }
                        bool known = Looks.Get(id) != null;
                        if (!Looks.Put(id, d)) { Invalidate(); return false; }
                        if (pendingLooks.Add(id) && known) { Looks.Retain(id); }
                    }
                    return !r.Failed && r.End;
                }
                case ChunkKind.Segment: {
                    int part = r.Count((int)(TapeBudget / CamWire.MaxChunk) + 1);
                    int parts = r.Count((int)(TapeBudget / CamWire.MaxChunk) + 1);
                    ReadOnlySpan<byte> meta = r.Bytes(CamWire.MaxMeta);
                    ReadOnlySpan<byte> data = r.Bytes(CamWire.MaxChunk);
                    if (r.Failed || !r.End || parts == 0 || part >= parts) { return false; }
                    if (part == 0) {
                        assembling.Clear();
                        expectPart = 0;
                        expectedParts = parts;
                    }
                    if (part != expectPart || expectedParts != parts) {
                        assembling.Clear();
                        expectPart = 0;
                        return false;
                    }
                    if (assembling.Length + data.Length > TapeBudget) { assembling.Clear(); expectPart = expectedParts = 0; return false; }
                    assembling.Raw(data);
                    expectPart++;
                    if (part < parts - 1) { return true; }
                    var mr = new WireReader(meta);
                    Segment s = CamWire.ReadSegment(ref mr);
                    expectPart = 0;
                    if (s == null) {
                        assembling.Clear();
                        return false;
                    }
                    s.Data = assembling.Span.ToArray();
                    s.Length = s.Data.Length;
                    assembling.Clear();
                    Tape.Insert(s);
                    Segments++;
                    return true;
                }
                case ChunkKind.Voice: {
                    uint count = r.U32();
                    if (count > 4096) { return false; }
                    for (uint i = 0; i < count && !r.Failed; i++) {
                        double t = BitConverter.Int64BitsToDouble((long)r.U64());
                        var subject = new Subject((ushort)r.Count(ushort.MaxValue), (uint)r.Var());
                        ReadOnlySpan<byte> packet = r.Bytes(VoiceStore.MaxPacket);
                        if (!r.Failed && double.IsFinite(t)) { Voice.Add(t, subject, 0, packet); }
                    }
                    return !r.Failed && r.End;
                }
                default:
                    return false;
            }
        }

        public void Done(uint generation, ushort camera) {
            if (camera != Camera || generation != View.Generation) { return; }
            ReleaseLooks();
            View.Done(generation, false);
            IReadOnlyList<Segment> segments = Tape.Sealed;
            for (int i = 0; i < segments.Count; i++) {
                Segment segment = segments[i];
                double from = Math.Max(segment.Start, View.RequestFrom), to = Math.Min(segment.End, View.RequestTo);
                if (to > from) { View.Mark(from, to); }
            }
        }
    }
}
