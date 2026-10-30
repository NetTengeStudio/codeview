using System;

namespace ScpItdMod.Chess {
    public readonly struct Move : IEquatable<Move> {
        public readonly byte From, To, Promote;
        public Move(int from, int to, int promote = 0) { From = (byte)from; To = (byte)to; Promote = (byte)promote; }
        public bool Equals(Move other) => From == other.From && To == other.To && Promote == other.Promote;
    }
    public enum ChessResult : byte { Playing, White, Black, Draw }
    public enum ChessEnd : byte { None, Mate, Stalemate, Dead, Repetition, Moves, Agreement, Resigned, Time, Left }

    public sealed class Board {
        public const int Pawn = 1, Knight = 2, Bishop = 3, Rook = 4, Queen = 5, King = 6, MaxMoves = 256;
        private readonly sbyte[] squares = new sbyte[64];
        private readonly ulong[] history = new ulong[151];
        private int histories;
        public ReadOnlySpan<sbyte> Squares => squares;
        public int Turn { get; private set; }
        public byte Castles { get; private set; }
        public int EnPassant { get; private set; } = -1;
        public int Halfmove { get; private set; }
        public int Fullmove { get; private set; } = 1;
        public uint Revision { get; private set; } = 1;
        public ChessResult Result { get; private set; }
        public ChessEnd End { get; private set; }
        public Move Last { get; private set; } = new Move(255, 255);
        private struct Undo { public sbyte Captured; public byte Castles; public int Ep, Halfmove; }

        public Board() => Load("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");

        public void Load(string fen) {
            if (fen == null) { throw new ArgumentNullException(nameof(fen)); }
            ReadOnlySpan<char> remaining = fen.AsSpan();
            Span<sbyte> parsed = stackalloc sbyte[64];
            parsed.Clear();
            if (!Field(ref remaining, out ReadOnlySpan<char> placement) ||
                !Field(ref remaining, out ReadOnlySpan<char> active) ||
                !Field(ref remaining, out ReadOnlySpan<char> rights) ||
                !Field(ref remaining, out ReadOnlySpan<char> enPassant) ||
                !Field(ref remaining, out ReadOnlySpan<char> halfmoveText) ||
                !Field(ref remaining, out ReadOnlySpan<char> fullmoveText)) {
                throw new FormatException("Invalid FEN");
            }
            SkipSpaces(ref remaining);
            if (!remaining.IsEmpty) { throw new FormatException("Invalid FEN"); }

            int rank = 7, file = 0, whiteKings = 0, blackKings = 0;
            foreach (char c in placement) {
                if (c == '/') {
                    if (file != 8 || rank == 0) { throw new FormatException("Invalid rank"); }
                    rank--; file = 0; continue;
                }
                if (c >= '1' && c <= '8') {
                    file += c - '0';
                    if (file > 8) { throw new FormatException("Invalid rank"); }
                    continue;
                }
                int kind = " pnbrqk".IndexOf(char.ToLowerInvariant(c));
                if (kind <= 0 || file >= 8) { throw new FormatException("Invalid piece"); }
                parsed[rank * 8 + file++] = (sbyte)(char.IsUpper(c) ? kind : -kind);
                if (kind == King) {
                    if (char.IsUpper(c)) { whiteKings++; }
                    else { blackKings++; }
                }
            }
            if (rank != 0 || file != 8 || whiteKings != 1 || blackKings != 1 || active.Length != 1 ||
                active[0] != 'w' && active[0] != 'b') { throw new FormatException("Invalid position"); }

            byte castles = 0;
            if (!(rights.Length == 1 && rights[0] == '-')) {
                for (int i = 0; i < rights.Length; i++) {
                    byte bit = rights[i] switch { 'K' => (byte)1, 'Q' => (byte)2, 'k' => (byte)4, 'q' => (byte)8, _ => (byte)0 };
                    if (bit == 0 || (castles & bit) != 0) { throw new FormatException("Invalid castling rights"); }
                    castles |= bit;
                }
            }
            int turn = active[0] == 'w' ? 0 : 1;
            int ep = enPassant.Length == 1 && enPassant[0] == '-' ? -1 : Square(enPassant);
            if (ep >= 0 && (ep >> 3) != (turn == 0 ? 5 : 2)) { throw new FormatException("Invalid en-passant square"); }
            if (!Counter(halfmoveText, out int halfmove) || halfmove > 150 ||
                !Counter(fullmoveText, out int fullmove) || fullmove < 1) {
                throw new FormatException("Invalid counters");
            }

            parsed.CopyTo(squares);
            Turn = turn; Castles = castles; EnPassant = ep; Halfmove = halfmove; Fullmove = fullmove;
            Result = ChessResult.Playing; End = ChessEnd.None; Last = new Move(255, 255); histories = 1; history[0] = Key(); Revision++;
        }

        private static bool Field(ref ReadOnlySpan<char> remaining, out ReadOnlySpan<char> field) {
            SkipSpaces(ref remaining);
            int length = remaining.IndexOf(' ');
            if (length < 0) { field = remaining; remaining = ReadOnlySpan<char>.Empty; }
            else { field = remaining.Slice(0, length); remaining = remaining.Slice(length + 1); }
            return !field.IsEmpty;
        }

        private static void SkipSpaces(ref ReadOnlySpan<char> remaining) {
            while (!remaining.IsEmpty && remaining[0] == ' ') { remaining = remaining.Slice(1); }
        }

        private static bool Counter(ReadOnlySpan<char> value, out int number) {
            number = 0;
            if (value.IsEmpty) { return false; }
            for (int i = 0; i < value.Length; i++) {
                int digit = value[i] - '0';
                if ((uint)digit > 9 || number > (int.MaxValue - digit) / 10) { return false; }
                number = number * 10 + digit;
            }
            return true;
        }

        public static int Square(string name) => name == null ? throw new ArgumentNullException(nameof(name)) : Square(name.AsSpan());

        private static int Square(ReadOnlySpan<char> name) {
            if (name.Length != 2 || name[0] < 'a' || name[0] > 'h' || name[1] < '1' || name[1] > '8') { throw new FormatException("Invalid square"); }
            return name[0] - 'a' + (name[1] - '1') * 8;
        }

        internal bool Snapshot(ReadOnlySpan<sbyte> cells, int turn, byte castles, int ep, int halfmove, int fullmove, ChessResult result, ChessEnd end, Move last) {
            if (cells.Length != 64 || (uint)turn > 1 || castles > 15 || ep < -1 || ep > 63 || halfmove < 0 || halfmove > 150 || fullmove < 1 || result > ChessResult.Draw || end > ChessEnd.Left ||
                !ConsistentResult(result, end) ||
                (last.From == 255 ? last.To != 255 || last.Promote != 0 : last.From > 63 || last.To > 63 || last.Promote != 0 && (last.Promote < Knight || last.Promote > Queen))) { return false; }
            int white = 0, black = 0;
            for (int i = 0; i < 64; i++) { if (cells[i] < -King || cells[i] > King) { return false; } if (cells[i] == King) { white++; } if (cells[i] == -King) { black++; } }
            if (white != 1 || black != 1) { return false; }
            cells.CopyTo(squares); Turn = turn; Castles = castles; EnPassant = ep; Halfmove = halfmove; Fullmove = fullmove; Result = result; End = end; Last = last;
            histories = 1; history[0] = Key(); Revision++; return true;
        }

        private static bool ConsistentResult(ChessResult result, ChessEnd end) => end switch {
            ChessEnd.None => result == ChessResult.Playing,
            ChessEnd.Mate => result is ChessResult.White or ChessResult.Black,
            ChessEnd.Stalemate or ChessEnd.Dead or ChessEnd.Repetition or ChessEnd.Moves or ChessEnd.Agreement => result == ChessResult.Draw,
            ChessEnd.Time => result is ChessResult.White or ChessResult.Black or ChessResult.Draw,
            ChessEnd.Resigned or ChessEnd.Left => result is ChessResult.White or ChessResult.Black,
            _ => false
        };
        private static int Side(int piece) => piece > 0 ? 0 : 1;

        public int Generate(Span<Move> moves) {
            if (moves.Length < MaxMoves) { throw new ArgumentException("256 move slots required", nameof(moves)); }
            int n = 0;
            for (int from = 0; from < 64; from++) {
                int p = squares[from]; if (p == 0 || Side(p) != Turn) { continue; }
                int kind = Math.Abs(p), x = from & 7, y = from >> 3;
                if (kind == Pawn) {
                    int dy = Turn == 0 ? 1 : -1, to = from + dy * 8;
                    if ((uint)to < 64 && squares[to] == 0) {
                        AddPawn(moves, ref n, from, to);
                        if (y == (Turn == 0 ? 1 : 6) && squares[to + dy * 8] == 0) { Add(moves, ref n, new Move(from, to + dy * 8)); }
                    }
                    for (int dx = -1; dx <= 1; dx += 2) {
                        int tx = x + dx, target = from + dy * 8 + dx;
                        if ((uint)tx >= 8 || (uint)target >= 64) { continue; }
                        if (squares[target] != 0 && Side(squares[target]) != Turn && Math.Abs(squares[target]) != King || target == EnPassant && squares[target - dy * 8] == (Turn == 0 ? -Pawn : Pawn)) { AddPawn(moves, ref n, from, target); }
                    }
                } else {
                    for (int dx = -2; dx <= 2; dx++) {
                        for (int dy = -2; dy <= 2; dy++) {
                            if (kind == Knight ? Math.Abs(dx * dy) != 2 : Math.Abs(dx) > 1 || Math.Abs(dy) > 1 || dx == 0 && dy == 0) { continue; }
                            if (kind == Bishop && (dx == 0 || dy == 0) || kind == Rook && dx != 0 && dy != 0) { continue; }
                            int tx = x + dx, ty = y + dy;
                            while ((uint)tx < 8 && (uint)ty < 8) {
                                int target = ty * 8 + tx, victim = squares[target];
                                if (victim != 0 && (Side(victim) == Turn || Math.Abs(victim) == King)) { break; }
                                Add(moves, ref n, new Move(from, target));
                                if (victim != 0 || kind == Knight || kind == King) { break; }
                                tx += dx; ty += dy;
                            }
                        }
                    }
                    if (kind == King && from == (Turn == 0 ? 4 : 60) && !InCheck(Turn)) {
                        int home = Turn == 0 ? 0 : 56;
                        for (int queen = 0; queen < 2; queen++) {
                            int right = 1 << (Turn * 2 + queen), rook = home + (queen == 0 ? 7 : 0), step = queen == 0 ? 1 : -1;
                            if ((Castles & right) == 0 || squares[rook] != (Turn == 0 ? Rook : -Rook)) { continue; }
                            bool clear = true;
                            for (int s = from + step; s != rook; s += step) { if (squares[s] != 0) { clear = false; break; } }
                            if (clear && !Attacked(from + step, 1 - Turn) && !Attacked(from + step * 2, 1 - Turn)) { Add(moves, ref n, new Move(from, from + step * 2)); }
                        }
                    }
                }
            }
            return n;
        }

        private void AddPawn(Span<Move> moves, ref int n, int from, int to) {
            if ((to >> 3) == 0 || (to >> 3) == 7) { for (int p = Knight; p <= Queen; p++) { Add(moves, ref n, new Move(from, to, p)); } }
            else { Add(moves, ref n, new Move(from, to)); }
        }
        private void Add(Span<Move> moves, ref int n, Move move) {
            Undo undo = Make(move); bool legal = !InCheck(1 - Turn); Unmake(move, undo);
            if (legal) { if (n == moves.Length) { throw new InvalidOperationException("Move buffer exhausted"); } moves[n++] = move; }
        }

        private Undo Make(Move m) {
            int piece = squares[m.From];
            var undo = new Undo { Captured = squares[m.To], Castles = Castles, Ep = EnPassant, Halfmove = Halfmove };
            squares[m.From] = 0; squares[m.To] = m.Promote == 0 ? (sbyte)piece : (sbyte)(Turn == 0 ? m.Promote : -m.Promote);
            if (Math.Abs(piece) == Pawn && m.To == EnPassant && undo.Captured == 0 && (m.From & 7) != (m.To & 7)) { squares[m.To + (Turn == 0 ? -8 : 8)] = 0; }
            if (Math.Abs(piece) == King) {
                Castles &= (byte)~(Turn == 0 ? 3 : 12);
                if (Math.Abs(m.To - m.From) == 2) { int rook = m.To > m.From ? m.From + 3 : m.From - 4; squares[(m.From + m.To) / 2] = squares[rook]; squares[rook] = 0; }
            }
            Castles &= Rights(m.From); Castles &= Rights(m.To);
            EnPassant = Math.Abs(piece) == Pawn && Math.Abs(m.To - m.From) == 16 ? (m.To + m.From) / 2 : -1;
            Halfmove = Math.Abs(piece) == Pawn || undo.Captured != 0 || m.To == undo.Ep && Math.Abs(piece) == Pawn ? 0 : Halfmove + 1;
            if (Turn == 1) { Fullmove++; } Turn = 1 - Turn; return undo;
        }
        private void Unmake(Move m, Undo u) {
            Turn = 1 - Turn; if (Turn == 1) { Fullmove--; }
            int piece = m.Promote == 0 ? squares[m.To] : Turn == 0 ? Pawn : -Pawn;
            squares[m.From] = (sbyte)piece; squares[m.To] = u.Captured;
            if (Math.Abs(piece) == Pawn && m.To == u.Ep && u.Captured == 0 && (m.From & 7) != (m.To & 7)) { squares[m.To + (Turn == 0 ? -8 : 8)] = (sbyte)(Turn == 0 ? -Pawn : Pawn); }
            if (Math.Abs(piece) == King && Math.Abs(m.To - m.From) == 2) { int rook = m.To > m.From ? m.From + 3 : m.From - 4; squares[rook] = squares[(m.From + m.To) / 2]; squares[(m.From + m.To) / 2] = 0; }
            Castles = u.Castles; EnPassant = u.Ep; Halfmove = u.Halfmove;
        }
        private static byte Rights(int square) => square switch { 0 => 13, 7 => 14, 56 => 7, 63 => 11, _ => 15 };

        public bool InCheck(int side) {
            int king = side == 0 ? King : -King;
            for (int s = 0; s < 64; s++) { if (squares[s] == king) { return Attacked(s, 1 - side); } }
            return true;
        }
        public bool Attacked(int square, int side) {
            int x = square & 7, y = square >> 3, sign = side == 0 ? 1 : -1;
            for (int dx = -2; dx <= 2; dx++) {
                for (int dy = -2; dy <= 2; dy++) {
                    if (Math.Abs(dx * dy) == 2) {
                        int tx = x + dx, ty = y + dy;
                        if ((uint)tx < 8 && (uint)ty < 8 && squares[ty * 8 + tx] == sign * Knight) { return true; }
                    }
                }
            }
            for (int dx = -1; dx <= 1; dx++) {
                for (int dy = -1; dy <= 1; dy++) {
                    if (dx == 0 && dy == 0) { continue; }
                    int tx = x + dx, ty = y + dy, distance = 1;
                    while ((uint)tx < 8 && (uint)ty < 8) {
                        int p = squares[ty * 8 + tx];
                        if (p != 0) {
                            if (Side(p) == side) {
                                int kind = Math.Abs(p);
                                if (distance == 1 && (kind == King || kind == Pawn && dy == -sign && dx != 0) || kind == Queen || kind == Rook && (dx == 0 || dy == 0) || kind == Bishop && dx != 0 && dy != 0) { return true; }
                            }
                            break;
                        }
                        tx += dx; ty += dy; distance++;
                    }
                }
            }
            return false;
        }

        public bool Play(Move move) {
            if (Result != ChessResult.Playing) { return false; }
            Span<Move> legal = stackalloc Move[MaxMoves]; int count = Generate(legal); bool found = false;
            for (int i = 0; i < count; i++) { if (legal[i].Equals(move)) { found = true; break; } }
            if (!found) { return false; }
            byte castles = Castles; Make(move); Last = move; Revision++;
            if (Halfmove == 0 || Castles != castles) { histories = 0; }
            if (histories < history.Length) { history[histories++] = Key(); }
            int next = Generate(legal);
            if (next == 0) { Finish(InCheck(Turn) ? Turn == 0 ? ChessResult.Black : ChessResult.White : ChessResult.Draw, InCheck(Turn) ? ChessEnd.Mate : ChessEnd.Stalemate); }
            else if (Dead()) { Finish(ChessResult.Draw, ChessEnd.Dead); }
            else if (Halfmove >= 150) { Finish(ChessResult.Draw, ChessEnd.Moves); }
            else if (Repetitions() >= 5) { Finish(ChessResult.Draw, ChessEnd.Repetition); }
            return true;
        }

        public bool Claim(Move? intended = null) {
            if (!Claimable(intended, out ChessEnd reason)) { return false; }
            Finish(ChessResult.Draw, reason); return true;
        }
        private bool Claimable(Move? intended, out ChessEnd reason) {
            reason = ChessEnd.None;
            if (Result != ChessResult.Playing) { return false; }
            if (Halfmove >= 100 || Repetitions() >= 3) { reason = Halfmove >= 100 ? ChessEnd.Moves : ChessEnd.Repetition; return true; }
            if (!intended.HasValue) { return false; }
            Span<Move> legal = stackalloc Move[MaxMoves]; int n = Generate(legal);
            for (int i = 0; i < n; i++) {
                Move m = legal[i]; if (!m.Equals(intended.Value)) { continue; }
                Undo undo = Make(m); ulong key = Key(); int repeats = 1;
                for (int j = 0; j < histories; j++) { if (history[j] == key) { repeats++; } }
                bool moves = Halfmove >= 100, valid = moves || repeats >= 3; Unmake(m, undo);
                if (valid) { reason = moves ? ChessEnd.Moves : ChessEnd.Repetition; } return valid;
            }
            return false;
        }
        public int ClaimMoves(Span<Move> result) {
            if (result.Length < MaxMoves) { throw new ArgumentException("256 move slots required", nameof(result)); }
            if (Halfmove >= 100 || Repetitions() >= 3 || Result != ChessResult.Playing) { return 0; }
            Span<Move> moves = stackalloc Move[MaxMoves]; int count = Generate(moves), claims = 0;
            for (int i = 0; i < count; i++) { if (Claimable(moves[i], out _)) { result[claims++] = moves[i]; } }
            return claims;
        }
        public void Finish(ChessResult result, ChessEnd end) { if (Result != ChessResult.Playing || result == ChessResult.Playing) { return; } Result = result; End = end; Revision++; }
        public int Repetitions() { ulong key = Key(); int count = 0; for (int i = 0; i < histories; i++) { if (history[i] == key) { count++; } } return count; }
        public bool Dead() {
            int minor = 0, bishops = 0, color = -1; bool same = true;
            for (int s = 0; s < 64; s++) {
                int kind = Math.Abs(squares[s]); if (kind == 0 || kind == King) { continue; }
                if (kind != Bishop && kind != Knight) { return false; }
                minor++;
                if (kind == Bishop) { bishops++; int c = ((s & 7) + (s >> 3)) & 1; if (color >= 0 && color != c) { same = false; } color = c; }
            }
            return minor <= 1 || bishops == minor && same;
        }
        public bool CanMate(int side) {
            int knights = 0, bishops = 0, other = 0, bishopColor = -1; bool bothColors = false;
            for (int s = 0; s < 64; s++) {
                int p = squares[s], kind = Math.Abs(p); if (kind == 0 || kind == King) { continue; }
                if (Side(p) != side) { other++; continue; }
                if (kind == Pawn || kind == Rook || kind == Queen) { return true; }
                if (kind == Knight) { knights++; }
                else { bishops++; int color = ((s & 7) + (s >> 3)) & 1; bothColors |= bishopColor >= 0 && bishopColor != color; bishopColor = color; }
            }
            return knights >= 2 || knights > 0 && bishops > 0 || bothColors || other > 0 && knights + bishops > 0 && !Dead();
        }

        public ulong Key() {
            ulong key = 14695981039346656037UL;
            for (int s = 0; s < 64; s++) { key = (key ^ (byte)(squares[s] + 6)) * 1099511628211UL; }
            key = (key ^ (uint)(Castles | Turn << 4)) * 1099511628211UL;
            int ep = -1;
            if (EnPassant >= 0) {
                int step = Turn == 0 ? 8 : -8;
                for (int dx = -1; dx <= 1; dx += 2) {
                    int from = EnPassant - step + dx;
                    if ((uint)from >= 64 || Math.Abs((from & 7) - (EnPassant & 7)) != 1 || squares[from] != (Turn == 0 ? Pawn : -Pawn)) { continue; }
                    Move m = new Move(from, EnPassant); Undo undo = Make(m); bool legal = !InCheck(1 - Turn); Unmake(m, undo);
                    if (legal) { ep = EnPassant; break; }
                }
            }
            return (key ^ (uint)(ep + 1)) * 1099511628211UL;
        }

        public long Perft(int depth) {
            if (depth == 0) { return 1; }
            Span<Move> moves = stackalloc Move[MaxMoves]; int count = Generate(moves); if (depth == 1) { return count; }
            long nodes = 0;
            for (int i = 0; i < count; i++) { Undo undo = Make(moves[i]); nodes += Perft(depth - 1); Unmake(moves[i], undo); }
            return nodes;
        }
    }
}
