namespace ScpItdMod.Pool {
    public sealed class PoolEndpoint {
        private uint token, epoch;
        private ushort table;
        private PoolError result;
        public void Reset() { token = epoch = 0; table = 0; result = PoolError.Ok; }
        public PoolError Handle(Match match, ulong actor, in PoolCommand command, uint currentEpoch, uint random, bool reachable) {
            if (command.Op == PoolOp.Poll) { return PoolError.Ok; }
            if (command.Token == 0) { return PoolError.Invalid; }
            if (token == command.Token && table == command.Table && epoch == command.Epoch) { return result; }
            if (token != 0 && (int)(command.Token - token) <= 0) { return PoolError.Stale; }
            result = reachable ? PoolWire.Apply(match, actor, command, currentEpoch, random) : PoolError.Reach;
            token = command.Token; table = command.Table; epoch = command.Epoch; return result;
        }
    }
}
