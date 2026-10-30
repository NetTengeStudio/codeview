using System;
using ScpItdMod.Net;

namespace ScpItdMod.Rec {
    public struct VoiceConsent {
        public bool Allowed { get; private set; }
        public uint Revision { get; private set; }
        private ushort start, last;

        public void Set(bool allowed, ushort latest) {
            if (allowed == Allowed) { return; }
            Allowed = allowed; start = last = latest;
            if (++Revision == 0) { Revision++; }
        }
        public VoiceConsent Fork() => new VoiceConsent { Allowed = Allowed, Revision = Revision, start = start, last = start };
        public bool Take(ushort latest, int capacity, out ushort first, out int count) {
            first = 0; count = 0;
            if (!Allowed || capacity <= 0) { return false; }
            int advance = (ushort)(latest - last);
            if (advance == 0 || advance >= 0x8000) { return false; }
            count = Math.Min(advance, capacity); last = latest;
            first = (ushort)(latest - count + 1); return true;
        }
    }

    public struct VoiceWindow {
        private ushort latest;
        private byte seen;
        public bool Accept(ushort buffer) {
            if (seen == 0) { latest = buffer; seen = 1; return true; }
            ushort advance = (ushort)(buffer - latest);
            if (advance != 0 && advance < 32768) {
                seen = advance >= 8 ? (byte)1 : (byte)((seen << advance) | 1); latest = buffer; return true;
            }
            ushort age = (ushort)(latest - buffer);
            if (age >= 8 || (seen & (1 << age)) != 0) { return false; }
            seen |= (byte)(1 << age); return true;
        }
    }

    public readonly ref struct VoiceFrame {
        public readonly byte Client;
        public readonly Subject Subject;
        public readonly ushort Buffer;
        public readonly float Lag;
        public readonly bool Radio;
        public readonly ReadOnlySpan<byte> Data;
        public VoiceFrame(byte client, Subject subject, ushort buffer, float lag, bool radio, ReadOnlySpan<byte> data) {
            Client = client; Subject = subject; Buffer = buffer; Lag = lag; Radio = radio; Data = data;
        }
    }

    public static class VoiceWire {
        public static void Write(WireWriter writer, in VoiceFrame frame) {
            CamWire.Header(writer, CamMsg.MemoryVoice);
            writer.U8(frame.Client); writer.U16(frame.Subject.Entity); writer.U32(frame.Subject.Serial); writer.U16(frame.Buffer);
            writer.F32(frame.Lag); writer.Bool(frame.Radio); writer.Bytes(frame.Data);
        }
        public static bool Read(ReadOnlySpan<byte> data, int maxPacket, float maxLag, out VoiceFrame frame) {
            frame = default; var reader = new WireReader(data);
            if (reader.U8() != CamWire.Proto || reader.U8() != (byte)CamMsg.MemoryVoice) { return false; }
            byte client = reader.U8(); var subject = new Subject(reader.U16(), reader.U32()); ushort buffer = reader.U16();
            float lag = reader.F32(); bool radio = reader.Bool(); ReadOnlySpan<byte> payload = reader.Bytes(maxPacket);
            if (reader.Failed || !reader.End || client == 0 || subject.Entity == 0 || subject.Serial == 0 || !float.IsFinite(lag) || lag < 0 || lag > maxLag || payload.IsEmpty) { return false; }
            frame = new VoiceFrame(client, subject, buffer, lag, radio, payload); return true;
        }
    }
}
