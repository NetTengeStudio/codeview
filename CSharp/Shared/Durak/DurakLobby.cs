using System;
using System.Collections.Generic;

namespace ScpItdMod.Durak {
    public enum DurakOp : byte { Poll, Create, Join, Watch, Leave, Ready, Start, AddBot, RemoveBot, Play, Chat, Sit, Kick }

    public sealed class DurakMember {
        public readonly ulong Id;
        public string Name { get; internal set; }
        public uint Room { get; internal set; }
        public int Seat { get; internal set; } = -1;
        public double Seen { get; internal set; }
        public double LastChat { get; internal set; } = double.NegativeInfinity;
        public bool Online { get; internal set; } = true;
        internal DurakMember(ulong id, string name, double now) { Id = id; Name = name; Seen = now; }
    }

    public sealed class DurakSeat {
        public ulong Account { get; internal set; }
        public string Name { get; internal set; }
        public bool Bot { get; internal set; }
        public bool Ready { get; internal set; }
        public bool Online { get; internal set; } = true;
    }

    public sealed class DurakRoom {
        private readonly DurakSeat[] seats = new DurakSeat[DurakCards.Seats];
        private readonly string[] feed = new string[12];
        private int feedHead, feedCount;
        internal string Password;
        internal double AutoAt;
        internal double MoveAt;
        public uint Id { get; internal set; }
        public string Name { get; internal set; }
        public int Capacity { get; internal set; }
        public DurakRule Rule { get; internal set; }
        public ulong Owner { get; internal set; }
        public uint Revision { get; internal set; } = 1;
        public uint RosterRevision { get; private set; } = 1;
        public uint FeedRevision { get; private set; } = 1;
        public DurakGame Game { get; internal set; }
        public DurakPhase Phase => Game?.Phase ?? DurakPhase.Waiting;
        public bool Locked => Password.Length != 0;
        public int Spectators { get; internal set; }
        public int FeedCount => feedCount;
        public DurakSeat Seat(int index) => (uint)index < DurakCards.Seats ? seats[index] : null;
        public string Line(int index) => (uint)index < (uint)feedCount ? feed[(feedHead - feedCount + index + feed.Length) % feed.Length] : "";
        public int Occupied {
            get { int count = 0; for (int i = 0; i < Capacity; i++) { if (seats[i] != null) { count++; } } return count; }
        }
        internal void RosterChanged() { RosterRevision++; Revision++; }
        internal void Set(int index, DurakSeat seat) { seats[index] = seat; RosterChanged(); }
        internal void Log(string line) {
            feed[feedHead] = line;
            feedHead = (feedHead + 1) % feed.Length;
            feedCount = Math.Min(feedCount + 1, feed.Length);
            FeedRevision++;
            Revision++;
        }
        internal void Compact(Dictionary<ulong, DurakMember> members) {
            int to = 0;
            for (int from = 0; from < Capacity; from++) {
                DurakSeat seat = seats[from];
                if (seat == null) { continue; }
                seats[to] = seat;
                if (!seat.Bot && members.TryGetValue(seat.Account, out DurakMember member)) { member.Seat = to; }
                to++;
            }
            for (int i = to; i < seats.Length; i++) { seats[i] = null; }
            RosterChanged();
        }
    }

    public sealed class DurakLobby {
        public const int MaxRooms = 16, MaxMembers = 128, MaxSpectators = 16;
        public const double PresenceSeconds = 6, ReconnectSeconds = 60, TurnSeconds = 45, BotSeconds = 0.85;
        private readonly Dictionary<ulong, DurakMember> members = new Dictionary<ulong, DurakMember>();
        private readonly List<DurakRoom> rooms = new List<DurakRoom>(MaxRooms);
        private readonly List<ulong> expired = new List<ulong>(MaxMembers);
        private readonly byte[] shuffled = new byte[DurakCards.Count];
        private readonly Action<byte[]> shuffle;
        private uint nextRoom;
        public uint DirectoryRevision { get; private set; } = 1;
        public IReadOnlyList<DurakRoom> Rooms => rooms;
        public DurakMember Member(ulong id) => members.TryGetValue(id, out DurakMember member) ? member : null;
        public DurakRoom Room(uint id) { for (int i = 0; i < rooms.Count; i++) { if (rooms[i].Id == id) { return rooms[i]; } } return null; }

        public DurakLobby(Action<byte[]> shuffle) => this.shuffle = shuffle ?? throw new ArgumentNullException(nameof(shuffle));

        public void Clear() { members.Clear(); rooms.Clear(); expired.Clear(); DirectoryRevision++; }

        public bool Touch(ulong id, string name, double now) {
            if (id == 0 || !double.IsFinite(now)) { return false; }
            if (!members.TryGetValue(id, out DurakMember member)) {
                if (members.Count >= MaxMembers) { return false; }
                member = new DurakMember(id, Clean(name, 32), now);
                members.Add(id, member);
            }
            member.Seen = now;
            if (!member.Online) {
                member.Online = true;
                DurakRoom room = Room(member.Room);
                DurakSeat seat = room?.Seat(member.Seat);
                if (seat != null) { seat.Online = true; room.RosterChanged(); room.Log(member.Name + " снова за компьютером."); }
            }
            return true;
        }

        public void Offline(ulong id) {
            DurakMember member = Member(id);
            if (member == null || !member.Online) { return; }
            member.Online = false;
            DurakRoom room = Room(member.Room);
            DurakSeat seat = room?.Seat(member.Seat);
            if (seat == null) { return; }
            seat.Online = false;
            seat.Ready = false;
            room.RosterChanged();
            room.Log(member.Name + " отошёл. Место сохраняется на 60 с.");
        }

        public DurakError Handle(ulong account, DurakOp op, uint roomId, uint revision, int arg, int arg2, string text, string password, double now) {
            if (!double.IsFinite(now)) { return DurakError.Invalid; }
            DurakMember member = Member(account);
            if (member == null) { return DurakError.Unavailable; }
            if (op == DurakOp.Poll) { return DurakError.Ok; }
            if (op == DurakOp.Leave) { Leave(member, false); Cleanup(); return DurakError.Ok; }
            if (op == DurakOp.Create) {
                if (member.Room != 0) { return DurakError.Busy; }
                if (rooms.Count >= MaxRooms) { return DurakError.Full; }
                if (arg < 2 || arg > DurakCards.Seats || (uint)arg2 > (uint)DurakRule.Transfer) { return DurakError.Invalid; }
                string title = Clean(text, 36);
                if (title.Length == 0 || password == null || password.Length > 24) { return DurakError.Invalid; }
                var created = new DurakRoom { Id = ++nextRoom, Name = title, Password = password, Capacity = arg, Rule = (DurakRule)arg2, Owner = account };
                if (created.Id == 0) { created.Id = ++nextRoom; }
                rooms.Add(created);
                Sit(member, created);
                return DurakError.Ok;
            }
            DurakRoom room = Room(roomId);
            if (room == null) { return DurakError.NotFound; }
            if (op == DurakOp.Join || op == DurakOp.Watch) {
                if (member.Room != 0) { return DurakError.Busy; }
                if (!string.Equals(room.Password, password, StringComparison.Ordinal)) { return DurakError.Password; }
                if (op == DurakOp.Join) {
                    if (room.Phase == DurakPhase.Playing) { return DurakError.Busy; }
                    return Sit(member, room);
                }
                if (room.Spectators >= MaxSpectators) { return DurakError.Full; }
                member.Room = room.Id;
                member.Seat = -1;
                room.Spectators++;
                room.Revision++;
                DirectoryRevision++;
                return DurakError.Ok;
            }
            if (member.Room != room.Id) { return DurakError.Denied; }
            if (op == DurakOp.Chat) {
                if (now - member.LastChat < 1) { return DurakError.Busy; }
                string message = Clean(text, 160);
                if (message.Length == 0) { return DurakError.Invalid; }
                member.LastChat = now;
                room.Log("[" + member.Name + "] " + message);
                return DurakError.Ok;
            }
            if (op != DurakOp.Ready && revision != (op == DurakOp.Play ? room.Game?.Revision ?? 0 : room.Revision)) { return DurakError.Stale; }
            if (op == DurakOp.Sit) {
                if (member.Seat >= 0 || room.Phase == DurakPhase.Playing) { return DurakError.Denied; }
                DurakError error = Sit(member, room);
                if (error == DurakError.Ok) { room.Spectators--; }
                return error;
            }
            DurakSeat own = room.Seat(member.Seat);
            if (own == null || own.Account != account || own.Bot) { return DurakError.Denied; }
            if (op == DurakOp.Play) {
                if (room.Phase != DurakPhase.Playing || arg < 0 || arg > (int)DurakMove.Pass || arg2 < 0 || arg2 > 2047) { return DurakError.Invalid; }
                int oldBout = room.Game.Bout, oldDefender = room.Game.Defender;
                ulong oldDiscard = room.Game.Discard;
                DurakError error = room.Game.Play(member.Seat, (DurakMove)arg, arg2 & 255, arg2 >> 8);
                if (error == DurakError.Ok) { Moved(room, now, own.Name, (DurakMove)arg); EndMove(room, oldBout, oldDefender, oldDiscard); }
                return error;
            }
            if (room.Phase == DurakPhase.Playing) { return DurakError.Busy; }
            if (op == DurakOp.Ready) { own.Ready = !own.Ready; room.RosterChanged(); return DurakError.Ok; }
            if (account != room.Owner) { return DurakError.Denied; }
            if (op == DurakOp.Kick) {
                DurakSeat target = room.Seat(arg);
                if (target == null || target.Bot || target.Account == account || Member(target.Account) is not DurakMember kicked) { return DurakError.Invalid; }
                Leave(kicked, false);
                return DurakError.Ok;
            }
            if (op == DurakOp.AddBot) {
                for (int i = 0; i < room.Capacity; i++) {
                    if (room.Seat(i) != null) { continue; }
                    room.Set(i, new DurakSeat { Bot = true, Ready = true, Name = "Компьютер " + (i + 1) });
                    room.Log("Добавлен компьютерный соперник."); DirectoryRevision++;
                    return DurakError.Ok;
                }
                return DurakError.Full;
            }
            if (op == DurakOp.RemoveBot) {
                if ((uint)arg >= (uint)room.Capacity || room.Seat(arg)?.Bot != true) { return DurakError.Invalid; }
                room.Set(arg, null); room.Revision++; DirectoryRevision++;
                return DurakError.Ok;
            }
            if (op != DurakOp.Start) { return DurakError.Invalid; }
            if (room.Occupied < 2) { return DurakError.NotReady; }
            for (int i = 0; i < room.Capacity; i++) {
                DurakSeat seat = room.Seat(i);
                if (seat != null && (!seat.Ready || !seat.Online)) { return DurakError.NotReady; }
            }
            for (int i = 0; i < shuffled.Length; i++) { shuffled[i] = (byte)i; }
            shuffle(shuffled);
            room.Compact(members);
            var game = new DurakGame();
            if (!game.Start(shuffled, room.Occupied, room.Rule)) { throw new InvalidOperationException("invalid shuffled Durak deck"); }
            room.Game = game;
            room.MoveAt = now;
            room.AutoAt = now + BotSeconds;
            room.Log("Карты сданы. Первым ходит " + room.Seat(game.Attacker).Name + ".");
            DirectoryRevision++;
            return DurakError.Ok;
        }

        private DurakError Sit(DurakMember member, DurakRoom room) {
            for (int i = 0; i < room.Capacity; i++) {
                if (room.Seat(i) != null) { continue; }
                room.Set(i, new DurakSeat { Account = member.Id, Name = member.Name });
                member.Room = room.Id;
                member.Seat = i;
                if (room.Owner == 0) { room.Owner = member.Id; }
                room.Log(member.Name + " занял место за столом.");
                DirectoryRevision++;
                return DurakError.Ok;
            }
            return DurakError.Full;
        }

        private void Leave(DurakMember member, bool expiredLease) {
            DurakRoom room = Room(member.Room);
            if (room == null) { member.Room = 0; member.Seat = -1; return; }
            DurakSeat seat = room.Seat(member.Seat);
            if (seat == null) { room.Spectators = Math.Max(0, room.Spectators - 1); }
            else if (room.Phase == DurakPhase.Playing) {
                seat.Account = 0;
                seat.Bot = seat.Ready = seat.Online = true;
                seat.Name = member.Name + " (авто)";
                room.RosterChanged();
                room.Log(member.Name + (expiredLease ? " не вернулся. Место передано компьютеру." : " сдался. Место передано компьютеру."));
            } else {
                room.Set(member.Seat, null);
                room.Log(member.Name + " вышел из комнаты.");
            }
            member.Room = 0;
            member.Seat = -1;
            if (room.Owner == member.Id) {
                room.Owner = 0;
                for (int i = 0; i < room.Capacity; i++) {
                    DurakSeat candidate = room.Seat(i);
                    if (candidate != null && !candidate.Bot) { room.Owner = candidate.Account; break; }
                }
            }
            room.Revision++;
            DirectoryRevision++;
        }

        public void Tick(double now) {
            if (!double.IsFinite(now)) { return; }
            expired.Clear();
            foreach (var entry in members) {
                DurakMember member = entry.Value;
                if (now - member.Seen > PresenceSeconds) { Offline(member.Id); }
                if (now - member.Seen > ReconnectSeconds) { Leave(member, true); expired.Add(member.Id); }
            }
            for (int i = 0; i < expired.Count; i++) { members.Remove(expired[i]); }
            for (int i = 0; i < rooms.Count; i++) {
                DurakRoom room = rooms[i];
                DurakGame game = room.Game;
                if (game == null || game.Phase != DurakPhase.Playing || now < room.AutoAt) { continue; }
                int actor = -1;
                bool timedOut = now - room.MoveAt >= TurnSeconds;
                if (!game.Taking && game.TableCount > 0 && !game.Covered && (room.Seat(game.Defender).Bot || timedOut)) { actor = game.Defender; }
                else if (game.AttackTurn >= 0 && (room.Seat(game.AttackTurn).Bot || timedOut)) { actor = game.AttackTurn; }
                if (actor < 0) { continue; }
                DurakSeat seat = room.Seat(actor);
                int bout = game.Bout, defender = game.Defender;
                ulong discard = game.Discard;
                if (!DurakBot.Step(game, actor)) { continue; }
                room.MoveAt = now;
                room.AutoAt = now + BotSeconds;
                room.Revision++;
                if (timedOut && !seat.Bot) { room.Log(seat.Name + " не успел. За него сходил компьютер."); }
                EndMove(room, bout, defender, discard);
            }
            Cleanup();
        }

        private void Moved(DurakRoom room, double now, string name, DurakMove move) {
            room.MoveAt = now;
            room.AutoAt = now + BotSeconds;
            room.Revision++;
            if (move == DurakMove.Take) { room.Log(name + " берёт карты."); }
            else if (move == DurakMove.Transfer) { room.Log(name + " переводит атаку."); }
        }

        private void EndMove(DurakRoom room, int oldBout, int oldDefender, ulong oldDiscard) {
            if (room.Phase == DurakPhase.Finished) {
                for (int i = 0; i < room.Capacity; i++) { DurakSeat seat = room.Seat(i); if (seat != null) { seat.Ready = seat.Bot; } }
                room.RosterChanged();
                room.Log(room.Game.Loser < 0 ? "Ничья." : "Партия окончена. С картами остался " + room.Seat(room.Game.Loser).Name + ".");
                DirectoryRevision++;
            } else if (room.Game.Bout != oldBout) {
                room.Log(room.Game.Discard != oldDiscard ? "Бито. Ходит " + room.Seat(room.Game.Attacker).Name + "." :
                    room.Seat(oldDefender).Name + " забирает карты. Ходит " + room.Seat(room.Game.Attacker).Name + ".");
            }
        }

        private void Cleanup() {
            for (int i = rooms.Count - 1; i >= 0; i--) {
                DurakRoom room = rooms[i];
                bool human = room.Spectators > 0;
                for (int seat = 0; seat < room.Capacity && !human; seat++) { DurakSeat player = room.Seat(seat); human |= player != null && !player.Bot; }
                if (human) { continue; }
                rooms.RemoveAt(i);
                DirectoryRevision++;
            }
        }

        public static string Clean(string value, int max) {
            if (string.IsNullOrWhiteSpace(value)) { return ""; }
            ReadOnlySpan<char> source = value.AsSpan().Trim();
            Span<char> chars = stackalloc char[160];
            int length = Math.Min(source.Length, Math.Min(max, chars.Length));
            if (length > 0 && char.IsHighSurrogate(source[length - 1])) { length--; }
            for (int i = 0; i < length; i++) { char ch = source[i]; chars[i] = char.IsControl(ch) || ch == '<' || ch == '>' ? ' ' : ch; }
            return new string(chars.Slice(0, length)).Trim();
        }
    }
}
