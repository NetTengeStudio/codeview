using System;
using Vector2 = ScpItdMod.Core.Vec2;

namespace ScpItdMod.Pool {
    public enum PoolMode : byte { EightBall, Free }
    public enum PoolPhase : byte { Waiting, Lag, BreakChoice, Aiming, Moving, Choice, Finished }
    public enum PoolChoice : byte { None, IllegalBreak, EightOnBreak, FoulOnBreak }
    public enum PoolAction : byte { Accept, Kitchen, RackSelf, RackOther, Spot, BreakSelf, BreakOther }
    public enum PoolFoul : byte { None, Scratch, WrongBall, NoRail, OffTable, Kitchen, Break }

    public sealed class Match {
        public readonly Physics Physics;
        public readonly ulong[] Players = new ulong[2];
        public readonly byte[] Groups = new byte[2];
        public PoolMode Mode { get; private set; }
        public PoolPhase Phase { get; private set; }
        public PoolChoice Choice { get; private set; }
        public PoolFoul Foul { get; private set; }
        public int Turn { get; private set; }
        public int Winner { get; private set; } = -1;
        public int Breaker { get; private set; }
        public bool Break { get; private set; }
        public bool InHand { get; private set; }
        public bool Kitchen { get; private set; }
        public int CalledBall { get; private set; } = -1;
        public int CalledPocket { get; private set; } = -1;
        public bool Safety { get; private set; }
        public int StalemateBy { get; private set; } = -1;
        public uint Revision { get; private set; } = 1;
        private readonly System.Collections.Generic.Dictionary<ulong, uint> wins = new System.Collections.Generic.Dictionary<ulong, uint>();
        public uint Wins(int seat) => (uint)seat < 2 && wins.TryGetValue(Players[seat], out uint count) ? count : 0;
        private void Finish(int winner) {
            if (Phase == PoolPhase.Finished) { return; }
            Winner = winner; Phase = PoolPhase.Finished;
            if ((uint)winner < 2 && Players[winner] != 0) {
                uint count = Wins(winner);
                if (count < uint.MaxValue) { wins[Players[winner]] = count + 1; }
            }
        }
        private ushort before;
        private bool shotKitchen, lagRunning, lagBad;
        private readonly float[] lag = new float[2];
        private uint seed;
        private double accumulator;

        public Match(Table table) { Physics = new Physics(table); }
        public int Seat(ulong actor) => actor == 0 ? -1 : Players[0] == actor ? 0 : Players[1] == actor ? 1 : -1;

        public bool Join(ulong actor) {
            if (actor == 0) { return false; }
            if (Seat(actor) >= 0) { return true; }
            if (Phase != PoolPhase.Waiting && Phase != PoolPhase.Finished && Mode != PoolMode.Free) { return false; }
            for (int i = 0; i < 2; i++) {
                if (Players[i] != 0) { continue; }
                Players[i] = actor;
                if (Phase == PoolPhase.Finished) { Phase = PoolPhase.Waiting; Winner = -1; Groups[0] = Groups[1] = 0; }
                Revision++; return true;
            }
            return false;
        }

        public void Leave(ulong actor) {
            int seat = Seat(actor); if (seat < 0) { return; }
            if (Mode == PoolMode.EightBall && Phase != PoolPhase.Waiting && Phase != PoolPhase.Finished) { Finish(1 - seat); }
            Players[seat] = 0; Revision++;
            if (Players[0] == 0 && Players[1] == 0 && !Physics.Moving) { Phase = PoolPhase.Waiting; }
        }

        public bool Start(ulong actor, PoolMode mode, uint random) {
            if (Seat(actor) < 0 || Physics.Moving || Phase != PoolPhase.Waiting && Phase != PoolPhase.Finished && !(Mode == PoolMode.Free && Phase == PoolPhase.Aiming) || mode > PoolMode.Free || mode == PoolMode.EightBall && (Players[0] == 0 || Players[1] == 0)) { return false; }
            Mode = mode; seed = random == 0 ? 1u : random; Winner = -1; Groups[0] = Groups[1] = 0; StalemateBy = -1; Foul = PoolFoul.None; Choice = PoolChoice.None;
            if (mode == PoolMode.Free) { Rack(Seat(actor)); }
            else { Turn = 0; lag[0] = lag[1] = float.PositiveInfinity; PrepareLag(); }
            Revision++; return true;
        }

        private void PrepareLag() {
            accumulator = 0;
            Physics.Rack(++seed);
            for (int i = 1; i < 16; i++) { Physics.Balls[i].Pocket = 7; }
            Physics.Balls[0].Position = new Vector2(Physics.Table.Length / 4 - Physics.Table.Radius, Physics.Table.Width * (Turn == 0 ? 0.25f : 0.75f));
            Phase = PoolPhase.Lag; Break = false; InHand = Kitchen = false; lagRunning = lagBad = false;
        }

        private void Rack(int breaker) {
            accumulator = 0;
            Physics.Rack(++seed); Turn = Breaker = breaker; Break = Mode == PoolMode.EightBall; InHand = true; Kitchen = Break;
            if (Break) { Physics.Balls[0].Position -= new Vector2(Physics.Table.Radius, 0); }
            Groups[0] = Groups[1] = 0; Choice = PoolChoice.None; Foul = PoolFoul.None; Phase = PoolPhase.Aiming; StalemateBy = -1;
        }

        public bool Place(ulong actor, Vector2 position) {
            int seat = Seat(actor);
            if (seat < 0 || Phase != PoolPhase.Aiming || !InHand || Mode != PoolMode.Free && seat != Turn || !Physics.Place(0, position, Kitchen)) { return false; }
            Revision++; return true;
        }

        public bool Strike(ulong actor, Vector2 direction, float speed, Vector2 tip, int calledBall, int calledPocket, bool safety) {
            int seat = Seat(actor);
            if (seat < 0 || Mode != PoolMode.Free && seat != Turn || Phase != PoolPhase.Aiming && Phase != PoolPhase.Lag) { return false; }
            if (Phase == PoolPhase.Lag && (tip != Vector2.Zero || direction.X <= 0 || lagRunning)) { return false; }
            if (Mode == PoolMode.EightBall && Phase != PoolPhase.Lag && !Break && !safety && (!LegalCall(calledBall, seat) || (uint)calledPocket >= 6)) { return false; }
            shotKitchen = Kitchen; before = LiveMask();
            if (!Physics.Strike(direction, speed, tip)) { return false; }
            Turn = seat; CalledBall = calledBall; CalledPocket = calledPocket; Safety = safety;
            InHand = Kitchen = false; StalemateBy = -1; Foul = PoolFoul.None;
            if (Phase == PoolPhase.Lag) { lagRunning = true; } else { Phase = PoolPhase.Moving; }
            Revision++; return true;
        }

        public bool LegalCall(int ball, int seat) {
            return (uint)seat < 2 && CallAllowed(LiveMask(), Groups[seat], ball);
        }

        public static bool CallAllowed(ushort live, byte group, int ball) {
            if (ball < 1 || ball > 15 || (live & 1 << ball) == 0) { return false; }
            if (group == 0) { return ball != 8 || (live & 0x00FE) == 0 || (live & 0xFE00) == 0; }
            return (live & GroupMask(group)) == 0 ? ball == 8 : Group(ball) == group;
        }

        public int AdvanceTime(double seconds) {
            if (!double.IsFinite(seconds) || seconds < 0) { return 0; }
            if (!Physics.Moving) { accumulator = 0; return 0; }
            accumulator += seconds; int steps = 0;
            while (accumulator >= Physics.Step && Physics.Moving && steps < 64) { Advance(); accumulator -= Physics.Step; steps++; }
            if (!Physics.Moving) { accumulator = 0; }
            return steps;
        }

        public void Advance() {
            if (!Physics.Moving) { return; }
            Physics.Advance();
            if (Physics.Fault) { Finish(-1); Revision++; return; }
            if (Phase == PoolPhase.Lag) {
                float y = Physics.Balls[0].Position.Y;
                lagBad |= Turn == 0 ? y >= Physics.Table.Width / 2 : y <= Physics.Table.Width / 2;
                if (!Physics.Moving) { EndLag(); }
            } else if (!Physics.Moving && Phase == PoolPhase.Moving) { Resolve(Physics.Shot); }
        }

        private void EndLag() {
            Shot shot = Physics.Shot; Vector2 p = Physics.Balls[0].Position;
            lag[Turn] = lagBad || shot.CueFoot != 1 || shot.CueSide || shot.Potted != 0 || shot.Off != 0 || p.X < Physics.Table.Radius ? float.PositiveInfinity : p.X;
            if (Turn == 0) { Turn = 1; PrepareLag(); }
            else if (float.IsPositiveInfinity(lag[0]) && float.IsPositiveInfinity(lag[1]) || MathF.Abs(lag[0] - lag[1]) < 0.0005f) { Turn = 0; PrepareLag(); }
            else { Turn = lag[0] < lag[1] ? 0 : 1; Phase = PoolPhase.BreakChoice; }
            Revision++;
        }

        public void Resolve(Shot shot) {
            if (Phase != PoolPhase.Moving || Physics.Moving) { return; }
            if (Mode == PoolMode.Free) {
                if (Physics.Balls[0].Down) { InHand = true; }
                Phase = PoolPhase.Aiming; Revision++; return;
            }
            bool scratch = ((shot.Potted | shot.Off) & 1) != 0;
            ushort first = shot.First;
            bool wrong = first == 0;
            if (!Break && first != 0) {
                ushort valid = Groups[Turn] == 0 ? (ushort)0xFEFE : OwnRemaining(Turn, before) == 0 ? (ushort)0x0100 : GroupMask(Groups[Turn]);
                if (Groups[Turn] == 0 && CalledBall == 8 && ((before & 0x00FE) == 0 || (before & 0xFE00) == 0)) { valid = 0x100; }
                wrong = (first & valid) == 0;
            }
            Foul = scratch ? PoolFoul.Scratch : shot.Off != 0 ? PoolFoul.OffTable : wrong ? PoolFoul.WrongBall : shotKitchen && !Break && !shot.BeyondHead ? PoolFoul.Kitchen :
                !Break && shot.Potted == 0 && !shot.RailAfter ? PoolFoul.NoRail : PoolFoul.None;
            if (Break) {
                Break = false;
                if ((shot.Off & 0x100) != 0) {
                    if (!Physics.Spot(8)) { throw new InvalidOperationException("No legal spot for the eight ball"); }
                    Turn = 1 - Turn; Choice = PoolChoice.FoulOnBreak; Phase = PoolPhase.Choice;
                } else if ((shot.Potted & 0x100) != 0) {
                    if (Foul != PoolFoul.None) { Turn = 1 - Turn; }
                    Choice = PoolChoice.EightOnBreak; Phase = PoolPhase.Choice;
                } else if ((shot.Potted & 0xFFFE) == 0 && System.Numerics.BitOperations.PopCount((uint)(shot.Rails & 0xFFFE)) < 4 && Foul == PoolFoul.None) {
                    Foul = PoolFoul.Break; Turn = 1 - Turn; Choice = PoolChoice.IllegalBreak; Phase = PoolPhase.Choice;
                } else if (Foul != PoolFoul.None) { Turn = 1 - Turn; Choice = PoolChoice.FoulOnBreak; Phase = PoolPhase.Choice; }
                else { if ((shot.Potted & 0xFFFE) == 0) { Turn = 1 - Turn; } Phase = PoolPhase.Aiming; }
            } else if (((shot.Potted | shot.Off) & 0x100) != 0) {
                bool cleared = Groups[Turn] == 0 ? (before & 0x00FE) == 0 || (before & 0xFE00) == 0 : OwnRemaining(Turn, before) == 0;
                Finish(Foul == PoolFoul.None && cleared && !Safety && CalledBall == 8 && shot.Pockets[8] == CalledPocket + 1 && (shot.Off & 0x100) == 0 ? Turn : 1 - Turn);
            } else {
                bool made = Foul == PoolFoul.None && !Safety && CalledBall > 0 && (shot.Potted & (1 << CalledBall)) != 0 && shot.Pockets[CalledBall] == CalledPocket + 1;
                if (made && Groups[Turn] == 0) { Groups[Turn] = Group(CalledBall); Groups[1 - Turn] = (byte)(3 - Groups[Turn]); }
                if (!made) { Turn = 1 - Turn; }
                if (Foul != PoolFoul.None) { InHand = true; Kitchen = false; Physics.Balls[0].Pocket = 7; }
                Phase = PoolPhase.Aiming;
            }
            Revision++;
        }

        public bool Choose(ulong actor, PoolAction action) {
            if (Seat(actor) != Turn || Physics.Moving) { return false; }
            if (Phase == PoolPhase.BreakChoice && (action == PoolAction.BreakSelf || action == PoolAction.BreakOther)) { Rack(action == PoolAction.BreakSelf ? Turn : 1 - Turn); Revision++; return true; }
            if (Phase != PoolPhase.Choice) { return false; }
            if (Choice == PoolChoice.IllegalBreak && (action == PoolAction.RackSelf || action == PoolAction.RackOther) || Choice == PoolChoice.EightOnBreak && action == PoolAction.RackSelf) { Rack(action == PoolAction.RackOther ? 1 - Turn : Turn); Revision++; return true; }
            if (Choice == PoolChoice.EightOnBreak) {
                if (action != PoolAction.Spot || !Physics.Spot(8)) { return false; }
                if (Foul != PoolFoul.None) { InHand = Kitchen = true; Physics.Balls[0].Pocket = 7; }
            } else {
                if (action != PoolAction.Accept && (Choice != PoolChoice.FoulOnBreak || action != PoolAction.Kitchen)) { return false; }
                if (action == PoolAction.Kitchen || Physics.Balls[0].Down) { InHand = Kitchen = true; Physics.Balls[0].Pocket = 7; }
            }
            Choice = PoolChoice.None; Phase = PoolPhase.Aiming; Revision++; return true;
        }

        public bool Stalemate(ulong actor) {
            int seat = Seat(actor);
            if (seat < 0 || Physics.Moving || Phase != PoolPhase.Aiming || Mode != PoolMode.EightBall) { return false; }
            if (StalemateBy == 1 - seat) { Rack(Breaker); } else { StalemateBy = StalemateBy == seat ? -1 : seat; }
            Revision++; return true;
        }

        public ushort LiveMask() { ushort mask = 0; for (int i = 0; i < 16; i++) { if (!Physics.Balls[i].Down) { mask |= (ushort)(1 << i); } } return mask; }
        public static byte Group(int ball) => ball >= 1 && ball <= 7 ? (byte)1 : ball >= 9 && ball <= 15 ? (byte)2 : (byte)0;
        public static ushort GroupMask(byte group) => group == 1 ? (ushort)0x00FE : group == 2 ? (ushort)0xFE00 : (ushort)0;
        private int OwnRemaining(int seat, ushort mask) => System.Numerics.BitOperations.PopCount((uint)(mask & GroupMask(Groups[seat])));
    }
}
