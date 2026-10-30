using System;
using ScpItdMod.Net;

namespace ScpItdMod.Chess {
    public struct ChessCommand {
        public ushort Computer;
        public uint Session, Token, Room, Revision;
        public ChessOp Op;
        public byte Arg;
        public Move Move;
    }
    public sealed class ChessListing {
        public uint Id;
        public string Name;
        public byte Occupied, Minutes;
        public bool Playing;
    }
    public sealed class ChessView {
        public uint Id, Revision;
        public int Me, Offer, Minutes;
        public bool Owner;
        public readonly string[] Names = new string[2];
        public readonly bool[] Present = new bool[2], Online = new bool[2], Ready = new bool[2];
        public readonly double[] Clock = new double[2];
        public Board Game;
        public bool Claim;
        public Move[] Claims = Array.Empty<Move>();
    }
    public static class ChessWire {
        public const string Channel = "scpitd.chess";
        public const int MaxRequest = 32, MaxReply = 4096;
        public static void Request(WireWriter w, in ChessCommand c) {
            w.Clear(); w.U8(1); w.U16(c.Computer); w.U32(c.Session); w.U32(c.Token); w.U8((byte)c.Op); w.U32(c.Room); w.U32(c.Revision); w.U8(c.Arg);
            w.U8(c.Move.From); w.U8(c.Move.To); w.U8(c.Move.Promote);
        }
        public static bool ReadRequest(ReadOnlySpan<byte> data, out ChessCommand c) {
            c = default; if (data.Length > MaxRequest) { return false; } var r = new WireReader(data);
            if (r.U8() != 1) { return false; }
            c.Computer = r.U16(); c.Session = r.U32(); c.Token = r.U32(); c.Op = (ChessOp)r.U8(); c.Room = r.U32(); c.Revision = r.U32(); c.Arg = r.U8(); c.Move = new Move(r.U8(), r.U8(), r.U8());
            bool move = c.Op == ChessOp.Move || c.Op == ChessOp.Claim && c.Move.From != 255;
            return !r.Failed && r.End && c.Computer != 0 && c.Session != 0 && c.Op <= ChessOp.Sit && (c.Op == ChessOp.Poll || c.Token != 0) &&
                (!move || c.Move.From < 64 && c.Move.To < 64 && (c.Move.Promote == 0 || c.Move.Promote >= Board.Knight && c.Move.Promote <= Board.Queen));
        }
        public static void Reply(WireWriter w, ChessLobby lobby, ulong actor, uint session, uint sequence, uint ack, ChessError error, bool directory, bool state, uint publicRoom = 0) {
            w.Clear(); w.U8(1); w.U32(session); w.U32(sequence); w.U32(ack); w.U8((byte)error); w.U32(lobby.Directory); w.Bool(directory);
            if (directory) {
                int listed = 0;
                foreach (ChessRoom room in lobby.Rooms) { if (room.Table == 0) { listed++; } }
                w.U8((byte)listed);
                foreach (ChessRoom room in lobby.Rooms) {
                    if (room.Table != 0) { continue; }
                    w.U32(room.Id); w.Text(lobby.Member(room.Owner)?.Name ?? "Шахматы"); w.U8((byte)((room.Players[0] != 0 ? 1 : 0) + (room.Players[1] != 0 ? 1 : 0))); w.U8((byte)room.Minutes); w.Bool(room.Playing);
                }
            }
            w.Bool(state); if (!state) { return; }
            ChessRoom view = lobby.Room(lobby.Member(actor)?.Room ?? publicRoom); w.U32(view?.Id ?? 0); if (view == null) { return; }
            w.U32(view.Revision); w.U8((byte)(view.Seat(actor) + 1)); w.Bool(actor != 0 && view.Owner == actor); w.U8((byte)view.Minutes); w.U8((byte)(view.Offer + 1));
            for (int i = 0; i < 2; i++) {
                ChessMember m = lobby.Member(view.Players[i]); w.Bool(m != null); w.Text(m?.Name ?? ""); w.Bool(m?.Online ?? false); w.Bool(view.Ready[i]); w.U32((uint)Math.Clamp(view.Clock[i] * 1000, 0, 900000));
            }
            Board game = view.Game; w.Bool(game != null); if (game == null) { return; }
            for (int i = 0; i < 64; i++) { w.U8((byte)(game.Squares[i] + 6)); }
            w.U8((byte)game.Turn); w.U8(game.Castles); w.U8((byte)(game.EnPassant + 1)); w.U16((ushort)game.Halfmove); w.U32((uint)game.Fullmove);
            w.U8((byte)game.Result); w.U8((byte)game.End); w.U8(game.Last.From); w.U8(game.Last.To); w.U8(game.Last.Promote);
            w.Bool(game.Halfmove >= 100 || game.Repetitions() >= 3);
            Span<Move> claims = stackalloc Move[Board.MaxMoves]; int count = actor == 0 ? 0 : game.ClaimMoves(claims); w.U16((ushort)count);
            for (int i = 0; i < count; i++) { w.U8(claims[i].From); w.U8(claims[i].To); w.U8(claims[i].Promote); }
        }
        public static bool ReadReply(ReadOnlySpan<byte> data, uint session, out uint sequence, out uint ack, out ChessError error, out uint directory, out ChessListing[] rooms, out bool state, out ChessView view) {
            sequence = ack = directory = 0; error = ChessError.Invalid; rooms = null; state = false; view = null;
            if (data.Length > MaxReply) { return false; } var r = new WireReader(data);
            if (r.U8() != 1 || r.U32() != session) { return false; }
            sequence = r.U32(); ack = r.U32(); error = (ChessError)r.U8(); directory = r.U32();
            if (error > ChessError.Waiting) { return false; }
            if (r.Bool()) {
                int n = r.U8(); if (n > ChessLobby.MaxRooms) { return false; } rooms = new ChessListing[n];
                for (int i = 0; i < n; i++) {
                    var listing = new ChessListing { Id = r.U32(), Name = r.Text(192), Occupied = r.U8(), Minutes = r.U8(), Playing = r.Bool() };
                    if (listing.Id == 0 || listing.Occupied > 2 || !Minutes(listing.Minutes)) { return false; }
                    for (int j = 0; j < i; j++) { if (rooms[j].Id == listing.Id) { return false; } }
                    rooms[i] = listing;
                }
            }
            state = r.Bool(); uint id = state ? r.U32() : 0;
            if (id != 0) {
                view = new ChessView { Id = id, Revision = r.U32(), Me = r.U8() - 1, Owner = r.Bool(), Minutes = r.U8(), Offer = r.U8() - 1 };
                if (view.Me < -1 || view.Me > 1 || view.Offer < -1 || view.Offer > 1 || !Minutes(view.Minutes)) { return false; }
                for (int i = 0; i < 2; i++) { view.Present[i] = r.Bool(); view.Names[i] = r.Text(192); view.Online[i] = r.Bool(); view.Ready[i] = r.Bool(); uint ms = r.U32(); if (ms > 900000) { return false; } view.Clock[i] = ms / 1000.0; }
                if (r.Bool()) {
                    Span<sbyte> cells = stackalloc sbyte[64]; for (int i = 0; i < 64; i++) { cells[i] = (sbyte)(r.U8() - 6); }
                    int turn = r.U8(); byte castles = r.U8(); int ep = r.U8() - 1, half = r.U16(); uint full = r.U32(); ChessResult result = (ChessResult)r.U8(); ChessEnd end = (ChessEnd)r.U8(); Move last = new Move(r.U8(), r.U8(), r.U8());
                    if (full > int.MaxValue || last.From != 255 && (last.From > 63 || last.To > 63 || last.Promote > Board.Queen)) { return false; }
                    view.Game = new Board(); if (!view.Game.Snapshot(cells, turn, castles, ep, half, (int)full, result, end, last)) { return false; }
                    view.Claim = r.Bool();
                    int count = r.U16(); if (count > Board.MaxMoves) { return false; }
                    if (count > 0) { view.Claims = new Move[count]; }
                    for (int i = 0; i < count; i++) { Move claim = new Move(r.U8(), r.U8(), r.U8()); if (claim.From > 63 || claim.To > 63 || claim.Promote > Board.Queen) { return false; } view.Claims[i] = claim; }
                }
            }
            return !r.Failed && r.End;
        }
        private static bool Minutes(int n) => n == 0 || n == 5 || n == 10 || n == 15;
        public static string Explain(ChessError error) => error switch { ChessError.Stale => "Позиция изменилась", ChessError.Full => "Нет мест", ChessError.Unavailable => "Нет связи с компьютером", ChessError.Turn => "Ход соперника", ChessError.Illegal => "Недопустимый ход", ChessError.Waiting => "Ждём игроков", ChessError.Invalid => "Действие недоступно", _ => "" };
    }
}
