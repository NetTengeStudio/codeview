using System;
using System.Collections.Generic;

namespace ScpItdMod.Chess {
    public enum ChessOp : byte { Poll, Create, Join, Leave, Ready, Start, Move, Draw, Decline, Claim, Swap, Resign, Sit }
    public enum ChessError : byte { Ok, Invalid, Stale, Full, Unavailable, Turn, Illegal, Waiting }

    public sealed class ChessMember {
        public ulong Account;
        public string Name;
        public uint Room;
        public bool Online;
        public double Seen;
    }
    public sealed class ChessRoom {
        public uint Id, Revision = 1;
        public ushort Table;
        public ulong Owner;
        public readonly ulong[] Players = new ulong[2];
        public readonly bool[] Ready = new bool[2];
        public readonly double[] Clock = new double[2];
        public int Minutes = 10, Offer = -1;
        public Board Game;
        public double ClockAt;
        public int Seat(ulong account) => account != 0 && Players[0] == account ? 0 : account != 0 && Players[1] == account ? 1 : -1;
        public bool Playing => Game != null && Game.Result == ChessResult.Playing;
    }

    public sealed class ChessLobby {
        public const int MaxRooms = 16, MaxMembers = 128, MaxViewers = 12;
        public const double Presence = 6, Reconnect = 90;
        private readonly Dictionary<ulong, ChessMember> members = new Dictionary<ulong, ChessMember>();
        private readonly Dictionary<uint, ChessRoom> rooms = new Dictionary<uint, ChessRoom>();
        private readonly Dictionary<ushort, ChessRoom> tables = new Dictionary<ushort, ChessRoom>();
        private readonly List<ulong> expired = new List<ulong>(MaxMembers);
        private readonly List<uint> empty = new List<uint>(MaxRooms);
        private uint nextId;
        public uint Directory { get; private set; } = 1;
        public IEnumerable<ChessRoom> Rooms => rooms.Values;
        public int Count => rooms.Count;
        public ChessMember Member(ulong actor) => members.TryGetValue(actor, out ChessMember member) ? member : null;
        public ChessRoom Room(uint id) => rooms.TryGetValue(id, out ChessRoom room) ? room : null;
        public void Clear() { members.Clear(); rooms.Clear(); tables.Clear(); expired.Clear(); empty.Clear(); Directory++; }

        public ChessRoom Table(ushort item) {
            if (item == 0) { return null; }
            if (tables.TryGetValue(item, out ChessRoom table)) { return table; }
            if (rooms.Count >= MaxRooms) { return null; }
            if (++nextId == 0) { nextId++; }
            table = new ChessRoom { Id = nextId, Table = item, Minutes = 0 };
            tables.Add(item, table); rooms.Add(table.Id, table); Directory++;
            return table;
        }

        public void RemoveTable(ushort item) {
            if (!tables.Remove(item, out ChessRoom table)) { return; }
            foreach (ChessMember member in members.Values) { if (member.Room == table.Id) { member.Room = 0; } }
            rooms.Remove(table.Id); Directory++;
        }

        public ChessError JoinTable(ulong actor, ushort item, double now) {
            ChessMember member = Member(actor);
            if (member == null || !member.Online) { return ChessError.Unavailable; }
            ChessRoom table = Table(item);
            if (table == null) { return ChessError.Full; }
            if (member.Room == table.Id) { return ChessError.Ok; }
            if (member.Room != 0) { return ChessError.Waiting; }
            return Handle(actor, ChessOp.Join, table.Id, table.Revision, 0, default, now);
        }

        public bool Touch(ulong actor, string name, double now) {
            if (actor == 0 || !double.IsFinite(now)) { return false; }
            if (!members.TryGetValue(actor, out ChessMember member)) {
                if (members.Count == MaxMembers) { return false; }
                member = new ChessMember { Account = actor }; members.Add(actor, member);
            }
            name ??= "Игрок"; if (name.Length > 48) { name = name.Substring(0, 48); }
            if (!member.Online || member.Name != name) { ChessRoom room = Room(member.Room); if (room != null) { room.Revision++; Directory++; } }
            member.Name = name; member.Seen = now; member.Online = true; return true;
        }
        public void Offline(ulong actor) {
            ChessMember member = Member(actor); if (member == null || !member.Online) { return; }
            member.Online = false; ChessRoom room = Room(member.Room); if (room != null) { room.Revision++; }
        }
        public int Spectators(ChessRoom room) { int n = 0; foreach (ChessMember m in members.Values) { if (m.Room == room.Id && room.Seat(m.Account) < 0) { n++; } } return n; }

        public ChessError Handle(ulong actor, ChessOp op, uint id, uint revision, int arg, Move move, double now) {
            Tick(now); ChessMember member = Member(actor); if (member == null || !member.Online) { return ChessError.Unavailable; }
            if (op == ChessOp.Poll) { return ChessError.Ok; }
            if (op == ChessOp.Create) {
                if (member.Room != 0 || rooms.Count == MaxRooms || arg != 0 && arg != 5 && arg != 10 && arg != 15) { return ChessError.Invalid; }
                if (++nextId == 0) { nextId++; }
                var created = new ChessRoom { Id = nextId, Owner = actor, Minutes = arg }; created.Players[0] = actor;
                rooms.Add(created.Id, created); member.Room = created.Id; Directory++; return ChessError.Ok;
            }
            if (op == ChessOp.Join) {
                ChessRoom target = Room(id);
                if (target == null || member.Room != 0 || arg > 1 || arg < 0) { return ChessError.Invalid; }
                if (Spectators(target) >= MaxViewers) { return ChessError.Full; }
                if (!target.Playing && arg == 0) {
                    int seat = target.Players[0] == 0 ? 0 : target.Players[1] == 0 ? 1 : -1;
                    if (seat >= 0) { target.Players[seat] = actor; target.Ready[0] = target.Ready[1] = false; target.Game = null; target.Offer = -1; }
                }
                member.Room = target.Id; if (target.Owner == 0) { target.Owner = actor; } target.Revision++; Directory++; return ChessError.Ok;
            }
            ChessRoom room = Room(member.Room); if (room == null || room.Id != id) { return ChessError.Invalid; }
            if (op == ChessOp.Leave) { Leave(member); return ChessError.Ok; }
            if (revision != room.Revision) { return ChessError.Stale; }
            int me = room.Seat(actor);
            if (op == ChessOp.Sit && !room.Playing && me < 0 && (uint)arg < 2 && room.Players[arg] == 0) { room.Players[arg] = actor; room.Ready[0] = room.Ready[1] = false; room.Game = null; room.Offer = -1; room.Revision++; Directory++; return ChessError.Ok; }
            if (me < 0) { return ChessError.Invalid; }
            if (op == ChessOp.Ready && !room.Playing) { room.Ready[me] = !room.Ready[me]; room.Revision++; return ChessError.Ok; }
            if (op == ChessOp.Swap && !room.Playing && room.Owner == actor) {
                ulong a = room.Players[0]; room.Players[0] = room.Players[1]; room.Players[1] = a; room.Game = null; room.Ready[0] = room.Ready[1] = false; room.Revision++; return ChessError.Ok;
            }
            if (op == ChessOp.Start) {
                if (room.Owner != actor || room.Playing || !room.Ready[0] || !room.Ready[1] || room.Players[0] == 0 || room.Players[1] == 0 || !Member(room.Players[0]).Online || !Member(room.Players[1]).Online) { return ChessError.Waiting; }
                room.Game = new Board(); room.Clock[0] = room.Clock[1] = room.Minutes * 60; room.ClockAt = now; room.Offer = -1;
                room.Ready[0] = room.Ready[1] = false; room.Revision++; Directory++; return ChessError.Ok;
            }
            if (!room.Playing) { return ChessError.Waiting; }
            Board game = room.Game;
            if (op == ChessOp.Move) {
                if (game.Turn != me) { return ChessError.Turn; }
                if (!game.Play(move)) { return ChessError.Illegal; }
                room.Offer = -1;
            } else if (op == ChessOp.Resign) { game.Finish(me == 0 ? ChessResult.Black : ChessResult.White, ChessEnd.Resigned); }
            else if (op == ChessOp.Draw) {
                if (room.Offer == 1 - me) { game.Finish(ChessResult.Draw, ChessEnd.Agreement); room.Offer = -1; }
                else { room.Offer = me; }
            } else if (op == ChessOp.Decline) { if (room.Offer != 1 - me) { return ChessError.Invalid; } room.Offer = -1; }
            else if (op == ChessOp.Claim) {
                if (game.Turn != me) { return ChessError.Turn; }
                if (!game.Claim(move.From == 255 ? null : move)) { return ChessError.Illegal; }
            } else { return ChessError.Invalid; }
            room.Revision++; if (!room.Playing) { Directory++; } return ChessError.Ok;
        }

        private void Leave(ChessMember member) {
            ChessRoom room = Room(member.Room); member.Room = 0; if (room == null) { return; }
            int seat = room.Seat(member.Account);
            if (seat >= 0) {
                if (room.Playing) { room.Game.Finish(seat == 0 ? ChessResult.Black : ChessResult.White, ChessEnd.Left); }
                room.Players[seat] = 0; room.Ready[seat] = false;
            }
            if (room.Owner == member.Account) {
                room.Owner = room.Players[0] != 0 ? room.Players[0] : room.Players[1];
                if (room.Owner == 0) { foreach (ChessMember m in members.Values) { if (m.Room == room.Id) { room.Owner = m.Account; break; } } }
            }
            if (room.Owner == 0 && room.Table == 0) { rooms.Remove(room.Id); } else { room.Revision++; }
            Directory++;
        }

        public void Tick(double now) {
            if (!double.IsFinite(now)) { return; }
            foreach (ChessRoom room in rooms.Values) {
                if (!room.Playing) { continue; }
                double elapsed = Math.Max(0, now - room.ClockAt); room.ClockAt = Math.Max(now, room.ClockAt);
                if (room.Minutes == 0) { continue; }
                int side = room.Game.Turn; room.Clock[side] = Math.Max(0, room.Clock[side] - elapsed);
                if (room.Clock[side] <= 0) { room.Game.Finish(room.Game.CanMate(1 - side) ? side == 0 ? ChessResult.Black : ChessResult.White : ChessResult.Draw, ChessEnd.Time); room.Revision++; Directory++; }
            }
            expired.Clear();
            foreach (ChessMember member in members.Values) {
                if (member.Online && now - member.Seen >= Presence) { Offline(member.Account); }
                if (now - member.Seen >= Reconnect) { expired.Add(member.Account); }
            }
            for (int i = 0; i < expired.Count; i++) { ChessMember member = Member(expired[i]); Leave(member); members.Remove(expired[i]); }
        }
    }
}
