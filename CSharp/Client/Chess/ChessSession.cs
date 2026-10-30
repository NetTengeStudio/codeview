using System;
using ScpItdMod.Net;

namespace ScpItdMod.Chess {
    public delegate void ChessSend(ReadOnlySpan<byte> data);
    public sealed class ChessSession {
        private readonly WireWriter writer = new WireWriter(ChessWire.MaxRequest), retry = new WireWriter(ChessWire.MaxRequest);
        private uint session, token, sequence;
        private ChessCommand pending;
        private bool received, active;
        private double now, pollAt, retryAt, deadline;
        public ChessSend Send;
        public ushort Computer { get; private set; }
        public ChessView View { get; private set; }
        public ChessListing[] Rooms { get; private set; } = Array.Empty<ChessListing>();
        public ChessError Error { get; private set; }
        public uint Revision { get; private set; }
        public double SnapshotAt { get; private set; }
        public double LastReply { get; private set; } = -1;
        public bool Busy => pending.Token != 0;
        public bool Connected => active && LastReply >= 0 && now - LastReply < ChessLobby.Presence;
        public bool Active => active;
        public void Begin(ushort computer, double time) { End(); Computer = computer; now = time; if (++session == 0) { session++; } active = true; Poll(); }
        public void End() { active = received = false; Computer = 0; View = null; Rooms = Array.Empty<ChessListing>(); pending = default; LastReply = -1; sequence = 0; Error = ChessError.Ok; Revision++; }
        public void Tick(double time) {
            if (!active || !double.IsFinite(time)) { return; } now = Math.Max(now, time);
            if (Busy && now >= deadline) { pending = default; Error = ChessError.Unavailable; Revision++; }
            if (Busy && now >= retryAt) { retryAt = now + 2; Send?.Invoke(retry.Span); }
            if (now >= pollAt) { Poll(); }
        }
        public void Poll() { if (!active) { return; } pollAt = now + 2; ChessWire.Request(writer, new ChessCommand { Computer = Computer, Session = session, Op = ChessOp.Poll }); Send?.Invoke(writer.Span); }
        public bool Command(ChessOp op, byte arg = 0, uint room = 0, Move move = default) {
            if (!active || Busy || op == ChessOp.Poll || op > ChessOp.Sit) { return false; }
            if (++token == 0) { token++; }
            pending = new ChessCommand { Computer = Computer, Session = session, Token = token, Op = op, Room = room == 0 ? View?.Id ?? 0 : room, Revision = View?.Revision ?? 0, Arg = arg, Move = move };
            ChessWire.Request(retry, pending); deadline = now + 10; retryAt = now + 2; Error = ChessError.Ok; Send?.Invoke(retry.Span); return true;
        }
        public bool Apply(ReadOnlySpan<byte> data) {
            if (!active || !ChessWire.ReadReply(data, session, out uint reply, out uint ack, out ChessError error, out _, out ChessListing[] rooms, out bool state, out ChessView view)) { return false; }
            LastReply = now;
            bool newer = !received || (int)(reply - sequence) > 0;
            if (newer) {
                sequence = reply; received = true;
                if (rooms != null) { Rooms = rooms; Revision++; }
                if (state) { View = view; SnapshotAt = now; Revision++; }
                if (error == ChessError.Unavailable) { Error = error; }
            }
            if (ack != 0 && ack == pending.Token) { pending = default; Error = error; Revision++; }
            else if (newer && error == ChessError.Ok && Error == ChessError.Unavailable) { Error = ChessError.Ok; Revision++; }
            return true;
        }
    }
}
