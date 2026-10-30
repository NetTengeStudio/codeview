using System;
using ScpItdMod.Net;

namespace ScpItdMod.Durak {
    [Flags]
    public enum DurakParts : byte { None = 0, Roster = 1, Feed = 2, Game = 4, All = Roster | Feed | Game }
    public struct DurakCommand {
        public uint Session, Token, Room, Revision;
        public ushort Computer;
        public DurakOp Op;
        public int Arg, Arg2;
        public string Text, Password;
    }

    public struct DurakListing {
        public uint Id;
        public string Name;
        public byte Capacity, Occupied, Spectators;
        public DurakRule Rule;
        public DurakPhase Phase;
        public bool Locked;
    }

    public struct DurakPlayerView {
        public string Name;
        public bool Present, Ready, Bot, Online, Out;
        public byte Cards;
    }

    public sealed class DurakView {
        public uint Id, Revision, GameRevision, FeedRevision;
        public bool GameUpdated;
        public string Name;
        public DurakRule Rule;
        public DurakPhase Phase;
        public int Capacity, Occupied, Spectators, Me = -1, Owner = -1;
        public int Attacker = -1, Defender = -1, AttackTurn = -1, Loser = -1;
        public byte TrumpCard = DurakCards.None, Remaining, TableCount, AttackLimit, DiscardCount;
        public uint Bout;
        public int Milliseconds;
        public bool Taking, CanTake, CanPass, CanStart;
        public ulong Hand, CanAttack, CanTransfer;
        public DurakPlayerView[] Players { get; internal set; } = Array.Empty<DurakPlayerView>();
        public byte[] Attacks { get; internal set; } = Array.Empty<byte>();
        public byte[] Defenses { get; internal set; } = Array.Empty<byte>();
        public ulong[] CanDefend { get; internal set; } = Array.Empty<ulong>();
        public string[] Feed = Array.Empty<string>();

        internal void RetainGame(DurakView previous) {
            if (previous == null) { return; }
            GameRevision = previous.GameRevision; Attacker = previous.Attacker; Defender = previous.Defender; AttackTurn = previous.AttackTurn; Loser = previous.Loser;
            TrumpCard = previous.TrumpCard; Remaining = previous.Remaining; TableCount = previous.TableCount; AttackLimit = previous.AttackLimit;
            DiscardCount = previous.DiscardCount; Bout = previous.Bout; Taking = previous.Taking; Milliseconds = previous.Milliseconds;
            Hand = previous.Hand; CanAttack = previous.CanAttack; CanTransfer = previous.CanTransfer; CanTake = previous.CanTake; CanPass = previous.CanPass;
            Attacks = previous.Attacks; Defenses = previous.Defenses; CanDefend = previous.CanDefend;
        }
    }

    public static class DurakWire {
        public const string Channel = "scpitd.pc.durak";
        public const byte Version = 1;
        public const int MaxRequest = 1024, MaxReply = 12288;

        public static void Request(WireWriter w, in DurakCommand command) {
            w.Clear();
            w.U8(Version); w.U32(command.Session); w.U32(command.Token); w.U32(command.Computer);
            w.U8((byte)command.Op); w.U32(command.Room); w.U32(command.Revision);
            w.U8((byte)command.Arg); w.U32((uint)command.Arg2); w.Text(command.Text); w.Text(command.Password);
        }

        public static bool ReadRequest(ReadOnlySpan<byte> bytes, out DurakCommand command) {
            command = default;
            if (bytes.Length > MaxRequest) { return false; }
            var r = new WireReader(bytes);
            if (r.U8() != Version) { return false; }
            command.Session = r.U32(); command.Token = r.U32();
            uint computer = r.U32();
            command.Op = (DurakOp)r.U8(); command.Room = r.U32(); command.Revision = r.U32();
            command.Arg = r.U8(); uint arg2 = r.U32();
            command.Text = r.Text(160 * 4); command.Password = r.Text(24 * 4);
            if (r.Failed || !r.End || command.Session == 0 || computer == 0 || computer > ushort.MaxValue || command.Op > DurakOp.Kick ||
                command.Op != DurakOp.Poll && command.Token == 0 || arg2 > 2047 || command.Text.Length > 160 || command.Password.Length > 24) { return false; }
            command.Computer = (ushort)computer; command.Arg2 = (int)arg2;
            return true;
        }

        private static void Seat(WireWriter w, int value) => w.U8((byte)(value + 1));

        public static void Reply(WireWriter w, DurakLobby lobby, DurakMember member, uint session, uint sequence, uint token, DurakError error,
            double now, bool directory, bool state, DurakParts parts = DurakParts.All, uint publicRoom = 0) {
            w.Clear();
            w.U8(Version); w.U32(session); w.U32(sequence); w.U32(token); w.U8((byte)error); w.U32(lobby.DirectoryRevision);
            w.Bool(directory);
            if (directory) {
                w.U8((byte)lobby.Rooms.Count);
                for (int i = 0; i < lobby.Rooms.Count; i++) {
                    DurakRoom room = lobby.Rooms[i];
                    w.U32(room.Id); w.Text(room.Name); w.U8((byte)room.Capacity); w.U8((byte)room.Occupied);
                    w.U8((byte)room.Spectators); w.U8((byte)room.Rule); w.U8((byte)room.Phase); w.Bool(room.Locked);
                }
            }
            w.Bool(state);
            if (!state) { return; }
            DurakRoom current = lobby.Room(member?.Room ?? publicRoom);
            w.U32(current?.Id ?? 0);
            if (current == null) { return; }
            WriteRoom(w, current, member, now, parts);
        }

        private static void WriteRoom(WireWriter w, DurakRoom room, DurakMember member, double now, DurakParts parts) {
            w.U32(room.Revision); w.Text(room.Name); w.U8((byte)room.Rule); w.U8((byte)room.Phase);
            w.U8((byte)room.Capacity); w.U8((byte)room.Occupied); w.U8((byte)room.Spectators); Seat(w, member?.Seat ?? -1);
            int owner = -1;
            bool canStart = room.Occupied >= 2 && room.Phase != DurakPhase.Playing;
            for (int i = 0; i < room.Capacity; i++) {
                DurakSeat seat = room.Seat(i);
                if (seat != null && !seat.Bot && seat.Account == room.Owner) { owner = i; }
                if (seat != null && (!seat.Ready || !seat.Online)) { canStart = false; }
            }
            Seat(w, owner); w.Bool(canStart && member != null && room.Owner == member.Id);
            w.U8((byte)parts);
            DurakGame game = room.Game;
            if ((parts & DurakParts.Roster) != 0) {
                for (int i = 0; i < room.Capacity; i++) {
                    DurakSeat seat = room.Seat(i);
                    w.Bool(seat != null);
                    if (seat == null) { continue; }
                    w.Text(seat.Name); w.Bool(seat.Ready); w.Bool(seat.Bot); w.Bool(seat.Online);
                }
            }
            if ((parts & DurakParts.Feed) != 0) {
                w.U32(room.FeedRevision); w.U8((byte)room.FeedCount);
                for (int i = 0; i < room.FeedCount; i++) { w.Text(room.Line(i)); }
            }
            if ((parts & DurakParts.Game) == 0 || game == null) { return; }
            w.U32(game.Revision);
            for (int i = 0; i < room.Capacity; i++) {
                w.Bool((game.Out & (1 << i)) != 0); w.U8((byte)DurakCards.CountOf(game.Hand(i)));
            }
            Seat(w, game.Attacker); Seat(w, game.Defender); Seat(w, game.AttackTurn); Seat(w, game.Loser);
            w.U8(game.TrumpCard); w.U8((byte)game.Remaining); w.U8((byte)game.TableCount); w.U8((byte)game.AttackLimit);
            w.U8((byte)DurakCards.CountOf(game.Discard)); w.U32((uint)game.Bout); w.Bool(game.Taking);
            w.U32((uint)Math.Clamp((room.MoveAt + DurakLobby.TurnSeconds - now) * 1000, 0, DurakLobby.TurnSeconds * 1000));
            int me = member?.Seat ?? -1;
            bool seated = me >= 0 && room.Seat(me)?.Account == member.Id && !room.Seat(me).Bot;
            ulong hand = seated ? game.Hand(me) : 0;
            w.U64(hand);
            ulong attack = 0, transfer = 0;
            if (seated) {
                for (int card = 0; card < DurakCards.Count; card++) {
                    if ((hand & (1UL << card)) == 0) { continue; }
                    if (game.Check(me, DurakMove.Attack, card) == DurakError.Ok) { attack |= 1UL << card; }
                    if (game.Check(me, DurakMove.Transfer, card) == DurakError.Ok) { transfer |= 1UL << card; }
                }
            }
            w.U64(attack); w.U64(transfer);
            w.Bool(seated && game.Check(me, DurakMove.Take) == DurakError.Ok);
            w.Bool(seated && game.Check(me, DurakMove.Pass) == DurakError.Ok);
            for (int pair = 0; pair < game.TableCount; pair++) {
                w.U8(game.Attack(pair)); w.U8(game.Defense(pair));
                ulong defend = 0;
                if (seated) {
                    for (int card = 0; card < DurakCards.Count; card++) {
                        if ((hand & (1UL << card)) != 0 && game.Check(me, DurakMove.Defend, card, pair) == DurakError.Ok) { defend |= 1UL << card; }
                    }
                }
                w.U64(defend);
            }
        }

        private static int ReadSeat(ref WireReader r) => r.U8() - 1;
        private static bool ValidSeat(int value, int capacity) => value >= -1 && value < capacity;

        public static bool ReadReply(ReadOnlySpan<byte> bytes, uint session, out uint sequence, out uint token, out DurakError error, out uint directoryRevision,
            out DurakListing[] directory, out bool hasState, out DurakView view, DurakView previous = null) {
            sequence = token = 0; error = DurakError.Ok; directoryRevision = 0; directory = null; hasState = false; view = null;
            if (bytes.Length > MaxReply) { return false; }
            var r = new WireReader(bytes);
            if (r.U8() != Version || r.U32() != session) { return false; }
            sequence = r.U32(); token = r.U32(); error = (DurakError)r.U8(); directoryRevision = r.U32();
            if (error > DurakError.Busy) { return false; }
            if (r.Bool()) {
                int count = r.U8();
                if (count > DurakLobby.MaxRooms) { return false; }
                directory = new DurakListing[count];
                for (int i = 0; i < count; i++) {
                    var row = new DurakListing {
                        Id = r.U32(), Name = r.Text(144), Capacity = r.U8(), Occupied = r.U8(), Spectators = r.U8(),
                        Rule = (DurakRule)r.U8(), Phase = (DurakPhase)r.U8(), Locked = r.Bool()
                    };
                    if (row.Id == 0 || row.Capacity < 2 || row.Capacity > DurakCards.Seats || row.Occupied > row.Capacity ||
                        row.Spectators > DurakLobby.MaxSpectators || row.Rule > DurakRule.Transfer || row.Phase > DurakPhase.Finished) { return false; }
                    for (int j = 0; j < i; j++) { if (directory[j].Id == row.Id) { return false; } }
                    directory[i] = row;
                }
            }
            hasState = r.Bool();
            if (hasState) {
                uint room = r.U32();
                if (room != 0) {
                    view = ReadRoom(ref r, room, previous);
                    if (view == null) { return false; }
                }
            }
            return !r.Failed && r.End;
        }

        private static DurakView ReadRoom(ref WireReader r, uint id, DurakView previous) {
            var v = new DurakView { Id = id, Revision = r.U32(), Name = r.Text(144), Rule = (DurakRule)r.U8(), Phase = (DurakPhase)r.U8(),
                Capacity = r.U8(), Occupied = r.U8(), Spectators = r.U8(), Me = ReadSeat(ref r), Owner = ReadSeat(ref r), CanStart = r.Bool() };
            if (v.Capacity < 2 || v.Capacity > DurakCards.Seats || v.Occupied > v.Capacity || v.Spectators > DurakLobby.MaxSpectators ||
                v.Rule > DurakRule.Transfer || v.Phase > DurakPhase.Finished || !ValidSeat(v.Me, v.Capacity) || !ValidSeat(v.Owner, v.Capacity)) { return null; }
            DurakParts parts = (DurakParts)r.U8();
            if ((parts & ~DurakParts.All) != 0 || parts != DurakParts.All && (previous == null || previous.Id != id || previous.Capacity != v.Capacity)) { return null; }
            bool roster = (parts & DurakParts.Roster) != 0;
            bool game = (parts & DurakParts.Game) != 0;
            if (!game && (previous.Phase != v.Phase || previous.Me != v.Me)) { return null; }
            v.GameUpdated = game;
            v.Players = roster || game ? new DurakPlayerView[DurakCards.Seats] : previous.Players;
            if (!roster && game && previous != null) { Array.Copy(previous.Players, v.Players, v.Players.Length); }
            if (roster) {
                for (int i = 0; i < v.Capacity; i++) {
                    if (!r.Bool()) { continue; }
                    v.Players[i] = new DurakPlayerView { Present = true, Name = r.Text(192), Ready = r.Bool(), Bot = r.Bool(), Online = r.Bool() };
                    if (!game && previous != null) { v.Players[i].Out = previous.Players[i].Out; v.Players[i].Cards = previous.Players[i].Cards; }
                }
            }
            int occupied = 0;
            for (int i = 0; i < v.Capacity; i++) { if (v.Players[i].Present) { occupied++; } }
            if (occupied != v.Occupied || v.Me >= 0 && (!v.Players[v.Me].Present || v.Players[v.Me].Bot)) { return null; }
            if ((parts & DurakParts.Feed) != 0) {
                v.FeedRevision = r.U32(); int lines = r.U8();
                if (lines > 12) { return null; }
                v.Feed = new string[lines];
                for (int i = 0; i < lines; i++) { v.Feed[i] = r.Text(800); }
            } else { v.Feed = previous.Feed; v.FeedRevision = previous.FeedRevision; }
            if (v.Phase == DurakPhase.Waiting) { return v; }
            if (!game) { v.RetainGame(previous); return v; }
            v.GameRevision = r.U32();
            for (int i = 0; i < v.Capacity; i++) {
                v.Players[i].Out = r.Bool(); v.Players[i].Cards = r.U8();
                if (v.Players[i].Cards > DurakCards.Count) { return null; }
            }
            v.Attacks = new byte[DurakCards.Pairs]; v.Defenses = new byte[DurakCards.Pairs]; v.CanDefend = new ulong[DurakCards.Pairs];
            v.Attacker = ReadSeat(ref r); v.Defender = ReadSeat(ref r); v.AttackTurn = ReadSeat(ref r); v.Loser = ReadSeat(ref r);
            v.TrumpCard = r.U8(); v.Remaining = r.U8(); v.TableCount = r.U8(); v.AttackLimit = r.U8(); v.DiscardCount = r.U8();
            v.Bout = r.U32(); v.Taking = r.Bool(); uint millis = r.U32();
            v.Hand = r.U64(); v.CanAttack = r.U64(); v.CanTransfer = r.U64(); v.CanTake = r.Bool(); v.CanPass = r.Bool();
            if (!ValidSeat(v.Attacker, v.Capacity) || !ValidSeat(v.Defender, v.Capacity) || !ValidSeat(v.AttackTurn, v.Capacity) || !ValidSeat(v.Loser, v.Capacity) ||
                !DurakCards.Valid(v.TrumpCard) || v.Remaining > DurakCards.Count || v.TableCount > DurakCards.Pairs || v.AttackLimit > DurakCards.Pairs ||
                v.DiscardCount > DurakCards.Count || millis > DurakLobby.TurnSeconds * 1000 || (v.Hand & ~DurakCards.All) != 0 ||
                (v.CanAttack & ~v.Hand) != 0 || (v.CanTransfer & ~v.Hand) != 0 || v.Me < 0 && v.Hand != 0) { return null; }
            v.Milliseconds = (int)millis;
            ulong seen = v.Hand;
            for (int i = 0; i < v.TableCount; i++) {
                int attack = r.U8(), defense = r.U8();
                v.CanDefend[i] = r.U64();
                if (!DurakCards.Valid(attack) || (seen & (1UL << attack)) != 0 || (v.CanDefend[i] & ~v.Hand) != 0) { return null; }
                seen |= 1UL << attack;
                if (defense != DurakCards.None) {
                    if (!DurakCards.Valid(defense) || (seen & (1UL << defense)) != 0) { return null; }
                    seen |= 1UL << defense;
                }
                v.Attacks[i] = (byte)attack; v.Defenses[i] = (byte)defense;
            }
            return v;
        }

        public static string Explain(DurakError error) => error switch {
            DurakError.Ok => "",
            DurakError.Stale => "Стол обновился. Повторите ход.",
            DurakError.NotFound => "Комната закрыта. Выберите другую.",
            DurakError.Full => "Мест нет. Можно наблюдать за игрой.",
            DurakError.Password => "Неверный пароль. Попробуйте ещё раз.",
            DurakError.Denied => "У вас нет прав на это действие.",
            DurakError.NotReady => "Нужны хотя бы два игрока. Каждый должен нажать «Готов».",
            DurakError.Turn => "Сейчас ходит другой игрок.",
            DurakError.Card => "Эту карту нельзя сыграть. Выберите другую.",
            DurakError.Limit => "Больше карт в этом ходу подкинуть нельзя.",
            DurakError.Unavailable => "Связь потеряна. Вернитесь к работающему компьютеру.",
            DurakError.Timeout => "Сервер не ответил. Проверьте соединение.",
            DurakError.Busy => "Подождите и повторите действие.",
            _ => "Некорректный запрос. Обновите список комнат."
        };
    }
}
