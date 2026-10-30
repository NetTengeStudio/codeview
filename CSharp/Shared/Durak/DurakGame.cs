using System;

namespace ScpItdMod.Durak {
    public enum DurakRule : byte { ThrowIn, Transfer }
    public enum DurakMove : byte { Attack, Defend, Transfer, Take, Pass }
    public enum DurakPhase : byte { Waiting, Playing, Finished }
    public enum DurakError : byte { Ok, Invalid, Stale, NotFound, Full, Password, Denied, NotReady, Turn, Card, Limit, Unavailable, Timeout, Busy }

    public static class DurakCards {
        public const int Count = 36, Seats = 6, HandSize = 6, Pairs = 6;
        public const byte None = 255;
        public const ulong All = (1UL << Count) - 1;

        public static int Suit(int card) => card / 9;
        public static int Rank(int card) => card % 9;
        public static bool Valid(int card) => (uint)card < Count;
        public static int CountOf(ulong cards) => System.Numerics.BitOperations.PopCount(cards);
        public static bool Beats(int defense, int attack, int trump) => Valid(defense) && Valid(attack) &&
            (Suit(defense) == Suit(attack) ? Rank(defense) > Rank(attack) : Suit(defense) == trump);
    }

    public sealed class DurakGame {
        private readonly ulong[] hands = new ulong[DurakCards.Seats];
        private readonly byte[] deck = new byte[DurakCards.Count];
        private readonly byte[] attacks = new byte[DurakCards.Pairs];
        private readonly byte[] defenses = new byte[DurakCards.Pairs];
        private int drawn;
        private int refillFrom;
        private byte passed;
        private ushort ranks;

        public DurakRule Rule { get; private set; }
        public DurakPhase Phase { get; private set; }
        public int Players { get; private set; }
        public int Attacker { get; private set; } = -1;
        public int Defender { get; private set; } = -1;
        public int AttackTurn { get; private set; } = -1;
        public int Trump { get; private set; }
        public byte TrumpCard { get; private set; } = DurakCards.None;
        public int Remaining => DurakCards.Count - drawn;
        public int TableCount { get; private set; }
        public int AttackLimit { get; private set; }
        public ulong Discard { get; private set; }
        public byte Out { get; private set; }
        public bool Taking { get; private set; }
        public int Loser { get; private set; } = -1;
        public uint Revision { get; private set; }
        public int Bout { get; private set; }
        public ulong Hand(int seat) => (uint)seat < (uint)Players ? hands[seat] : 0;
        public byte Attack(int pair) => (uint)pair < (uint)TableCount ? attacks[pair] : DurakCards.None;
        public byte Defense(int pair) => (uint)pair < (uint)TableCount ? defenses[pair] : DurakCards.None;
        public bool Covered {
            get {
                if (TableCount == 0) { return false; }
                for (int i = 0; i < TableCount; i++) { if (defenses[i] == DurakCards.None) { return false; } }
                return true;
            }
        }

        public bool Start(ReadOnlySpan<byte> shuffled, int players, DurakRule rule) {
            if (players < 2 || players > DurakCards.Seats || rule > DurakRule.Transfer || shuffled.Length != DurakCards.Count) { return false; }
            ulong seen = 0;
            for (int i = 0; i < shuffled.Length; i++) {
                int card = shuffled[i];
                if (!DurakCards.Valid(card) || (seen & (1UL << card)) != 0) { return false; }
                seen |= 1UL << card;
            }
            shuffled.CopyTo(deck);
            Array.Clear(hands, 0, hands.Length);
            Array.Fill(defenses, DurakCards.None);
            Players = players;
            Rule = rule;
            drawn = 0;
            Out = 0;
            Discard = 0;
            Loser = -1;
            TrumpCard = deck[DurakCards.Count - 1];
            Trump = DurakCards.Suit(TrumpCard);
            for (int card = 0; card < DurakCards.HandSize; card++) {
                for (int seat = 0; seat < players; seat++) { Draw(seat); }
            }
            int lowest = DurakCards.Count;
            int first = 0;
            for (int seat = 0; seat < players; seat++) {
                for (int rank = 0; rank < 9; rank++) {
                    int card = Trump * 9 + rank;
                    if ((hands[seat] & (1UL << card)) != 0 && card < lowest) { lowest = card; first = seat; }
                }
            }
            Phase = DurakPhase.Playing;
            Bout = 0;
            BeginBout(first);
            Revision++;
            return true;
        }

        private void Draw(int seat) => hands[seat] |= 1UL << deck[drawn++];

        private int Next(int seat) {
            for (int n = 1; n <= Players; n++) {
                int candidate = (seat + n) % Players;
                if ((Out & (1 << candidate)) == 0) { return candidate; }
            }
            return -1;
        }

        private void BeginBout(int first) {
            Attacker = first;
            refillFrom = first;
            Defender = Next(first);
            AttackTurn = first;
            TableCount = 0;
            AttackLimit = Math.Min(DurakCards.HandSize, DurakCards.CountOf(hands[Defender]));
            Taking = false;
            passed = 0;
            ranks = 0;
            Bout++;
        }

        public DurakError Check(int seat, DurakMove move, int card = DurakCards.None, int pair = 0) {
            if (Phase != DurakPhase.Playing || (uint)seat >= (uint)Players || (Out & (1 << seat)) != 0) { return DurakError.Turn; }
            if (move == DurakMove.Take) { return seat == Defender && !Taking && TableCount > 0 && !Covered ? DurakError.Ok : DurakError.Turn; }
            if (move == DurakMove.Pass) { return seat == AttackTurn && TableCount > 0 ? DurakError.Ok : DurakError.Turn; }
            if (move > DurakMove.Pass || !DurakCards.Valid(card) || (hands[seat] & (1UL << card)) == 0) { return DurakError.Card; }
            switch (move) {
                case DurakMove.Attack:
                    if (seat != AttackTurn || seat == Defender) { return DurakError.Turn; }
                    if (TableCount >= AttackLimit) { return DurakError.Limit; }
                    return TableCount == 0 || (ranks & (1 << DurakCards.Rank(card))) != 0 ? DurakError.Ok : DurakError.Card;
                case DurakMove.Defend:
                    if (seat != Defender || Taking || (uint)pair >= (uint)TableCount || defenses[pair] != DurakCards.None) { return DurakError.Turn; }
                    return DurakCards.Beats(card, attacks[pair], Trump) ? DurakError.Ok : DurakError.Card;
                case DurakMove.Transfer:
                    if (Rule != DurakRule.Transfer || seat != Defender || Taking || TableCount == 0) { return DurakError.Turn; }
                    for (int i = 0; i < TableCount; i++) {
                        if (defenses[i] != DurakCards.None || DurakCards.Rank(attacks[i]) != DurakCards.Rank(card)) { return DurakError.Card; }
                    }
                    int next = Next(Defender);
                    return next != Defender && TableCount < DurakCards.Pairs && TableCount + 1 <= DurakCards.CountOf(hands[next]) ? DurakError.Ok : DurakError.Limit;
                default: return DurakError.Invalid;
            }
        }

        public DurakError Play(int seat, DurakMove move, int card = DurakCards.None, int pair = 0) {
            DurakError error = Check(seat, move, card, pair);
            if (error != DurakError.Ok) { return error; }
            if (move == DurakMove.Take) {
                Taking = true;
                passed = 0;
            } else if (move == DurakMove.Pass) {
                passed |= (byte)(1 << seat);
            } else {
                hands[seat] &= ~(1UL << card);
                ranks |= (ushort)(1 << DurakCards.Rank(card));
                passed = 0;
                if (move == DurakMove.Defend) {
                    defenses[pair] = (byte)card;
                } else {
                    attacks[TableCount] = (byte)card;
                    defenses[TableCount++] = DurakCards.None;
                    if (move == DurakMove.Transfer) {
                        Attacker = Defender;
                        Defender = Next(Defender);
                        AttackLimit = Math.Min(DurakCards.Pairs, DurakCards.CountOf(hands[Defender]));
                    }
                }
            }
            Advance();
            Revision++;
            return DurakError.Ok;
        }

        private bool CanAdd(int seat) {
            if (seat == Defender || (Out & (1 << seat)) != 0 || (passed & (1 << seat)) != 0 || TableCount >= AttackLimit) { return false; }
            const ulong suits = 1UL | (1UL << 9) | (1UL << 18) | (1UL << 27);
            ulong hand = hands[seat];
            for (int rank = 0; rank < 9; rank++) {
                if ((ranks & (1 << rank)) != 0 && (hand & (suits << rank)) != 0) { return true; }
            }
            return false;
        }

        private void Advance() {
            AttackTurn = -1;
            for (int offset = 0; offset < Players; offset++) {
                int seat = (Attacker + offset) % Players;
                if (CanAdd(seat)) { AttackTurn = seat; break; }
            }
            if (AttackTurn < 0 && (Taking || Covered)) { Resolve(); }
        }

        private void Resolve() {
            ulong table = 0;
            for (int i = 0; i < TableCount; i++) {
                table |= 1UL << attacks[i];
                if (defenses[i] != DurakCards.None) { table |= 1UL << defenses[i]; }
            }
            int oldDefender = Defender;
            bool took = Taking;
            if (took) { hands[Defender] |= table; } else { Discard |= table; }
            for (int offset = 0; offset < Players; offset++) {
                int seat = (refillFrom + offset) % Players;
                if (seat == Defender && Defender != refillFrom) { continue; }
                while (drawn < DurakCards.Count && DurakCards.CountOf(hands[seat]) < DurakCards.HandSize) { Draw(seat); }
            }
            while (Defender != refillFrom && drawn < DurakCards.Count && DurakCards.CountOf(hands[Defender]) < DurakCards.HandSize) { Draw(Defender); }
            int active = 0;
            for (int seat = 0; seat < Players; seat++) {
                if (drawn == DurakCards.Count && hands[seat] == 0) { Out |= (byte)(1 << seat); }
                if ((Out & (1 << seat)) == 0) { active++; Loser = seat; }
            }
            TableCount = 0;
            Taking = false;
            if (active <= 1) {
                Phase = DurakPhase.Finished;
                if (active == 0) { Loser = -1; }
                AttackTurn = -1;
                return;
            }
            Loser = -1;
            int first = !took && (Out & (1 << oldDefender)) == 0 ? oldDefender : Next(oldDefender);
            BeginBout(first);
        }

        public bool Conserved() {
            if (Players < 2 || drawn < 0 || drawn > DurakCards.Count) { return false; }
            ulong seen = Discard;
            int count = DurakCards.CountOf(seen);
            for (int i = 0; i < Players; i++) { if ((seen & hands[i]) != 0) { return false; } seen |= hands[i]; count += DurakCards.CountOf(hands[i]); }
            for (int i = drawn; i < deck.Length; i++) {
                ulong bit = 1UL << deck[i];
                if ((seen & bit) != 0) { return false; }
                seen |= bit; count++;
            }
            for (int i = 0; i < TableCount; i++) {
                ulong bit = 1UL << attacks[i];
                if ((seen & bit) != 0) { return false; }
                seen |= bit; count++;
                if (defenses[i] == DurakCards.None) { continue; }
                bit = 1UL << defenses[i];
                if ((seen & bit) != 0) { return false; }
                seen |= bit; count++;
            }
            return seen == DurakCards.All && count == DurakCards.Count;
        }
    }

    public static class DurakBot {
        public static bool Step(DurakGame game, int seat) {
            if (game.Phase != DurakPhase.Playing) { return false; }
            if (seat == game.Defender && !game.Taking) {
                for (int pair = 0; pair < game.TableCount; pair++) {
                    if (game.Defense(pair) != DurakCards.None) { continue; }
                    int best = -1, score = int.MaxValue;
                    for (int card = 0; card < DurakCards.Count; card++) {
                        if (game.Check(seat, DurakMove.Defend, card, pair) != DurakError.Ok) { continue; }
                        int cost = DurakCards.Rank(card) + (DurakCards.Suit(card) == game.Trump ? 16 : 0);
                        if (cost < score) { score = cost; best = card; }
                    }
                    if (best >= 0) { return game.Play(seat, DurakMove.Defend, best, pair) == DurakError.Ok; }
                    for (int card = 0; card < DurakCards.Count; card++) {
                        if (game.Check(seat, DurakMove.Transfer, card) == DurakError.Ok) { return game.Play(seat, DurakMove.Transfer, card) == DurakError.Ok; }
                    }
                    return game.Play(seat, DurakMove.Take) == DurakError.Ok;
                }
            }
            if (seat != game.AttackTurn) { return false; }
            int attack = -1, cheapest = int.MaxValue;
            for (int card = 0; card < DurakCards.Count; card++) {
                if (game.Check(seat, DurakMove.Attack, card) != DurakError.Ok) { continue; }
                int cost = DurakCards.Rank(card) + (DurakCards.Suit(card) == game.Trump ? 16 : 0);
                if (cost < cheapest) { attack = card; cheapest = cost; }
            }
            return attack >= 0 ? game.Play(seat, DurakMove.Attack, attack) == DurakError.Ok : game.Play(seat, DurakMove.Pass) == DurakError.Ok;
        }
    }
}
