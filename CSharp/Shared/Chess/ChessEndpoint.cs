namespace ScpItdMod.Chess {
    public sealed class ChessEndpoint {
        private uint token;
        private ChessError result;
        public uint Session { get; private set; }
        public void Reset() { Session = token = 0; result = ChessError.Ok; }
        public bool Open(uint session) {
            if (session == 0 || Session != 0 && session != Session && (int)(session - Session) <= 0) { return false; }
            if (session != Session) { Session = session; token = 0; }
            return true;
        }
        public ChessError Handle(ChessLobby lobby, ulong actor, in ChessCommand command, double now) {
            if (command.Session != Session) { return ChessError.Invalid; }
            if (command.Op == ChessOp.Poll) { return ChessError.Ok; }
            if (command.Token == 0) { return ChessError.Invalid; }
            if (command.Token == token) { return result; }
            if (token != 0 && (int)(command.Token - token) < 0) { return ChessError.Stale; }
            result = lobby.Handle(actor, command.Op, command.Room, command.Revision, command.Arg, command.Move, now); token = command.Token; return result;
        }
    }
}
