using System;
using System.Collections.Generic;
using ScpItdMod.Net;

namespace ScpItdMod.Rec {
    public enum ChunkKind : byte { Looks, Segment, Voice }
    public enum CamMsg : byte { List, Control, Fetch, Cameras, Chunk, Done, Denied, ScreenWatch, ScreenFrame, VoiceConsent, MemoryVoice, ScreenPart }

    public enum CamDeny : byte { None, NoTerminal, TooFar, NoPower, NoRights, NoCamera, Busy, Range }

    public sealed class CamInfo {
        public ushort Item;
        public uint Epoch = 1;
        public string Name = "";
        public bool Recording;
        public bool Powered;
        public bool Lost;
        public bool Control;
        public double Oldest;
        public double Newest;
        public float X;
        public float Y;
        public float HalfW;
        public float HalfH;

        public void Write(WireWriter w) {
            w.U32(Item);
            w.U32(Epoch);
            w.Text(Name);
            w.U8((byte)((Recording ? 1 : 0) | (Powered ? 2 : 0) | (Lost ? 4 : 0) | (Control ? 8 : 0)));
            w.U64((ulong)BitConverter.DoubleToInt64Bits(Oldest));
            w.U64((ulong)BitConverter.DoubleToInt64Bits(Newest));
            w.U32((uint)BitConverter.SingleToInt32Bits(X));
            w.U32((uint)BitConverter.SingleToInt32Bits(Y));
            w.U32((uint)BitConverter.SingleToInt32Bits(HalfW));
            w.U32((uint)BitConverter.SingleToInt32Bits(HalfH));
        }

        public static CamInfo Read(ref WireReader r) {
            uint item = r.U32();
            var c = new CamInfo { Epoch = r.U32(), Name = r.Text(256) };
            byte flags = r.U8();
            c.Recording = (flags & 1) != 0;
            c.Powered = (flags & 2) != 0;
            c.Lost = (flags & 4) != 0;
            c.Control = (flags & 8) != 0;
            c.Oldest = BitConverter.Int64BitsToDouble((long)r.U64());
            c.Newest = BitConverter.Int64BitsToDouble((long)r.U64());
            c.X = BitConverter.Int32BitsToSingle((int)r.U32());
            c.Y = BitConverter.Int32BitsToSingle((int)r.U32());
            c.HalfW = BitConverter.Int32BitsToSingle((int)r.U32());
            c.HalfH = BitConverter.Int32BitsToSingle((int)r.U32());
            c.Item = item > ushort.MaxValue ? (ushort)0 : (ushort)item;
            return c;
        }
    }

    public static class CamWire {
        public const string Channel = "scpitd.cam";
        public const byte Proto = 7;
        public const int MaxRequest = 64;
        public const int MaxChunk = 14 * 1024;
        public const double MaxSpan = 60.0;
        public const int MaxCameras = 48;
        public const int MaxReply = MaxChunk * 3;
        public const int MaxLooks = 8192;
        public const int MaxMeta = 64 * 1024 - MaxChunk - 64;

        public static void Header(WireWriter w, CamMsg msg) {
            w.Clear();
            w.U8(Proto);
            w.U8((byte)msg);
        }

        public const int PartSize = MaxChunk - 512;

        public static int Parts(int length) => Math.Max(1, (length + PartSize - 1) / PartSize);

        public static void Chunk(WireWriter w, uint generation, ushort camera, ChunkKind kind) {
            Header(w, CamMsg.Chunk);
            w.U32(generation);
            w.U32(camera);
            w.U8((byte)kind);
        }

        public static int SegmentPart(WireWriter w, WireWriter meta, uint generation, ushort camera, Segment s, byte[] data, int part) {
            int parts = Parts(data.Length);
            int from = part * PartSize;
            int len = Math.Max(0, Math.Min(PartSize, data.Length - from));
            Chunk(w, generation, camera, ChunkKind.Segment);
            w.Var((ulong)part);
            w.Var((ulong)parts);
            if (part == parts - 1) {
                meta.Clear();
                WriteSegment(meta, s, ReadOnlySpan<byte>.Empty);
                w.Bytes(meta.Span);
            } else {
                w.Bytes(ReadOnlySpan<byte>.Empty);
            }
            w.Bytes(new ReadOnlySpan<byte>(data, from, len));
            return parts;
        }

        public static void Done(WireWriter w, uint generation, ushort camera) {
            Header(w, CamMsg.Done);
            w.U32(generation);
            w.U32(camera);
        }

        public static void WriteSegment(WireWriter w, Segment s, ReadOnlySpan<byte> data) {
            w.U64((ulong)BitConverter.DoubleToInt64Bits(s.Start));
            w.U64((ulong)BitConverter.DoubleToInt64Bits(s.End));
            w.Var((ulong)s.Frames);
            w.Bool(s.Gap);
            w.Bool(s.Thinned);
            w.Var((ulong)s.Looks.Count);
            foreach (int id in s.Looks) { w.Var((ulong)id); }
            w.Bytes(data);
            w.Var((ulong)s.Notes.Count);
            for (int i = 0; i < s.Notes.Count; i++) {
                TapeNote n = s.Notes[i];
                w.U64((ulong)BitConverter.DoubleToInt64Bits(n.Time));
                w.U8((byte)n.Kind);
                w.Var(n.Subject.Entity);
                w.Var(n.Subject.Serial);
                w.Var(n.Value);
                w.Text(n.Text ?? "");
            }
        }

        public static Segment ReadSegment(ref WireReader r) {
            var s = new Segment {
                Start = BitConverter.Int64BitsToDouble((long)r.U64()),
                End = BitConverter.Int64BitsToDouble((long)r.U64()),
                Frames = r.Count(1 << 16),
                Gap = r.Bool(),
                Thinned = r.Bool()
            };
            int lookCount = r.Count(MaxLooks);
            for (int i = 0; i < lookCount && !r.Failed; i++) {
                int id = r.Count(MaxLooks);
                if (id == 0 || !s.Looks.Add(id)) { return null; }
            }
            ReadOnlySpan<byte> data = r.Bytes(MaxChunk);
            s.Data = data.ToArray();
            s.Length = s.Data.Length;
            int notes = r.Count(Tape.MaxNotes);
            for (int i = 0; i < notes && !r.Failed; i++) {
                s.Notes.Add(new TapeNote {
                    Time = BitConverter.Int64BitsToDouble((long)r.U64()),
                    Kind = (TapeEvent)r.U8(),
                    Subject = new Subject((ushort)r.Count(ushort.MaxValue), (uint)r.Var()),
                    Value = (uint)r.Var(),
                    Text = r.Text(Tape.MaxNoteText * 4)
                });
            }
            if (r.Failed || !r.End || !double.IsFinite(s.Start) || !double.IsFinite(s.End) || s.End < s.Start) { return null; }
            return s;
        }
    }
}
