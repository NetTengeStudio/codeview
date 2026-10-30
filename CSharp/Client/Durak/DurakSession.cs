using System;
using ScpItdMod.Net;

namespace ScpItdMod.Durak {
    public delegate void DurakSend(ReadOnlySpan<byte> bytes);

    public sealed class DurakSession {
        private readonly WireWriter writer = new WireWriter(1024);
        private readonly WireWriter retry = new WireWriter(1024);
        private DurakCommand pending;
        private uint session, token, directoryRevision, replySequence;
        private bool received;
        private double now, nextPoll, deadline, retryAt;
        private bool active;
        public DurakSend Send;
        public ushort Computer { get; private set; }
        public DurakView View { get; private set; }
        public DurakListing[] Rooms { get; private set; } = Array.Empty<DurakListing>();
        public DurakError Error { get; private set; }
        public bool Busy => pending.Token != 0;
        public bool Connected => active && LastReply >= 0 && now - LastReply < DurakLobby.PresenceSeconds;
        public double LastReply { get; private set; } = -1;
        public double SnapshotAt { get; private set; }
        public uint Revision { get; private set; }
        public bool Active => active;

        public void Begin(ushort computer, double time) {
            End();
            Computer = computer; now = time;
            session++;
            if (session == 0) { session = 1; }
            active = true;
            Poll();
        }

        public void End() {
            active = false; Computer = 0; View = null; Rooms = Array.Empty<DurakListing>(); pending = default;
            Error = DurakError.Ok; LastReply = -1; directoryRevision = 0; replySequence = 0; received = false; Revision++;
        }

        public void Suspend() { active = false; pending = default; }

        public void Tick(double time) {
            if (!active || !double.IsFinite(time)) { return; }
            now = Math.Max(now, time);
            if (Busy && now >= deadline) { pending = default; Error = DurakError.Timeout; Revision++; }
            if (Busy && now >= retryAt) { retryAt = now + 2; Send?.Invoke(retry.Span); }
            if (now >= nextPoll) { Poll(); }
        }

        public void Poll(bool resync = false) {
            if (!active) { return; }
            nextPoll = now + 2;
            var command = new DurakCommand { Session = session, Computer = Computer, Op = DurakOp.Poll, Arg = resync || View == null || Error == DurakError.Timeout ? 1 : 0 };
            DurakWire.Request(writer, command);
            Send?.Invoke(writer.Span);
        }

        public bool Command(DurakOp op, int arg = 0, int arg2 = 0, string text = "", string password = "", uint room = 0) {
            if (!active || Busy || op == DurakOp.Poll || op > DurakOp.Kick || (uint)arg > byte.MaxValue || (uint)arg2 > 2047 || text?.Length > 160 || password?.Length > 24) { return false; }
            if (++token == 0) { token = 1; }
            pending = new DurakCommand { Session = session, Token = token, Computer = Computer, Op = op, Room = room == 0 ? View?.Id ?? 0 : room,
                Revision = op == DurakOp.Play ? View?.GameRevision ?? 0 : View?.Revision ?? 0, Arg = arg, Arg2 = arg2, Text = text ?? "", Password = password ?? "" };
            DurakWire.Request(retry, pending);
            deadline = now + 10; retryAt = now + 2;
            Error = DurakError.Ok;
            Send?.Invoke(retry.Span);
            return true;
        }

        public bool Move(DurakMove move, int card = DurakCards.None, int pair = 0) => Command(DurakOp.Play, (int)move, card | pair << 8);

        public bool Apply(ReadOnlySpan<byte> bytes) {
            if (!active || !DurakWire.ReadReply(bytes, session, out uint sequence, out uint ack, out DurakError error, out uint dirRev, out DurakListing[] directory, out bool hasState, out DurakView view, View)) { return false; }
            LastReply = now;
            bool newer = !received || (int)(sequence - replySequence) > 0;
            if (newer) { received = true; replySequence = sequence; }
            if (newer && directory != null && (directoryRevision == 0 || (int)(dirRev - directoryRevision) >= 0)) { Rooms = directory; directoryRevision = dirRev; Revision++; }
            if (newer && hasState && (view == null || View == null || view.Id != View.Id || (int)(view.Revision - View.Revision) >= 0)) {
                View = view; if (view == null || view.GameUpdated) { SnapshotAt = now; } Revision++;
            }
            if (ack != 0 && ack == pending.Token) { pending = default; Error = error; Revision++; }
            else if (newer && error == DurakError.Unavailable) { Error = error; Revision++; }
            else if (newer && error == DurakError.Ok && (Error == DurakError.Unavailable || Error == DurakError.Timeout && hasState)) { Error = DurakError.Ok; Revision++; }
            return true;
        }
    }
}
