namespace ScpItdMod.Durak {
    public sealed class DurakEndpoint {
        private uint lastToken, replies;
        private DurakError lastResult;
        public uint Session { get; private set; }
        public void Reset() { Session = lastToken = replies = 0; lastResult = DurakError.Ok; }
        public bool Open(uint session) {
            if (session == 0 || Session != 0 && session != Session && (int)(session - Session) <= 0) { return false; }
            if (Session != session) { Session = session; lastToken = replies = 0; lastResult = DurakError.Ok; }
            return true;
        }
        public uint NextReply() => ++replies;
        public bool Repeated(uint token) => token != 0 && token == lastToken;
        public DurakError Handle(DurakLobby lobby, ulong actor, in DurakCommand command, double now) {
            if (command.Session != Session) { return DurakError.Invalid; }
            if (command.Op == DurakOp.Poll) { return DurakError.Ok; }
            if (command.Token == 0) { return DurakError.Invalid; }
            if (command.Token == lastToken) { return lastResult; }
            if (lastToken != 0 && (int)(command.Token - lastToken) < 0) { return DurakError.Stale; }
            lastResult = lobby.Handle(actor, command.Op, command.Room, command.Revision, command.Arg, command.Arg2, command.Text, command.Password, now);
            lastToken = command.Token;
            return lastResult;
        }
    }
}
