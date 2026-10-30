using System;
using ScpItdMod.Chess;
using ScpItdMod.Ui;

namespace ScpItdMod.Xp {
    public sealed class ChessApp : IXpApp, IXpSized, IXpSessionLifecycle {
        private static readonly string[] pieces = { "", "pawn", "knight", "bishop", "rook", "queen", "king" };
        private static readonly string[] files = { "a", "b", "c", "d", "e", "f", "g", "h" }, ranks = { "1", "2", "3", "4", "5", "6", "7", "8" };
        private static readonly string[] images = Images();
        private static readonly string[] controls = { "Без часов", "5 мин", "10 мин", "15 мин" };
        private static readonly byte[] minutes = { 0, 5, 10, 15 };
        private readonly ChessSession session;
        private readonly string[] clocks = new string[2], names = new string[2];
        private readonly int[] seconds = { -1, -1 };
        private uint selectedRoom, cached;
        private uint cachedRoom, cachedRoomRevision;
        private int knownSeat = -2;
        private int selected = -1, promotion = -1, time = 2, scroll;
        private bool flipped, orientationSet;
        private ulong legal;
        private bool claiming;
        private Move intended;
        private string status = "", right = "";

        public ChessApp(XpShell shell) { session = shell.Chess; session?.Begin(shell.Pc.Computer, shell.Now); }
        public string Title => "Шахматы";
        public string Icon => "chess16";
        public void Size(XpWindow w) { w.Rect = new Box(0, 0, 800, 650); w.MinW = 650; w.MinH = 530; }
        private bool Available => session != null && session.Connected && !session.Busy && session.Error != ChessError.Unavailable;
        public bool WantsClose(XpShell shell, XpWindow self) {
            if (session == null || session.View == null) { return true; }
            if (session.Busy) { return false; }
            if (session.View.Game?.Result == ChessResult.Playing && session.View.Me >= 0) {
                shell.Ask(self, Title, "Сдаться и закрыть шахматы?", new[] { "Сдаться", "Остаться" }, picked => { if (picked == 0 && session.Command(ChessOp.Leave)) { shell.Close(self); } }, "chess32");
                return false;
            }
            session.Command(ChessOp.Leave); return true;
        }

        public void OnSessionSuspending(XpShell shell) => session?.End();

        public void OnSessionResumed(XpShell shell) => session?.Begin(shell.Pc.Computer, shell.Now);

        public void Closed(XpShell shell) { session?.End(); }
        public static string Piece(int piece) => images[(piece > 0 ? 0 : 7) + Math.Abs(piece)];
        private static readonly string[] worldImages = Images("Content/Items/Chess/");
        public static string WorldPiece(int piece) => worldImages[(piece > 0 ? 0 : 7) + Math.Abs(piece)];
        private static string[] Images(string directory = "Content/UI/Chess/") {
            var result = new string[14];
            for (int color = 0; color < 2; color++) { for (int p = 1; p <= 6; p++) { result[color * 7 + p] = directory + (color == 0 ? "w_" : "b_") + pieces[p] + ".png"; } }
            return result;
        }
        public void Paint(ICanvas c, UiState ui, XpFaces f, Box client, XpShell shell, XpWindow self) {
            c.Fill(client, XpLook.Surface); Box footer = client.TakeBottom(XpLook.StatusH), body = client.CutBottom(XpLook.StatusH).Inset(8);
            if (session == null) { XpPaint.Label(c, f, "Нет связи с сервером", body); return; }
            ChessView view = session.View;
            if (cached != session.Revision) { cached = session.Revision; Refresh(view); }
            if (view == null) { Browser(c, ui, f, body); }
            else {
                Box top = body.TakeTop(28);
                XpPaint.Label(c, f, "Шахматы", top.CutRight(170));
                if (XpPaint.Button(c, ui, f, 10, top.TakeRight(160), "Покинуть комнату", Available)) {
                    if (view.Game?.Result == ChessResult.Playing && view.Me >= 0) {
                        shell.Ask(self, Title, "Сдаться и покинуть комнату?", new[] { "Сдаться", "Остаться" }, picked => { if (picked == 0) { session.Command(ChessOp.Leave); } }, "chess32");
                    } else { session.Command(ChessOp.Leave); }
                }
                Box play = body.CutTop(34), actions = play.TakeRight(190), surface = play.CutRight(204);
                if (view.Game == null) { Waiting(c, ui, f, surface, view); }
                else { Board(c, ui, f, surface, view); }
                Actions(c, ui, f, actions, view, shell, self);
            }
            XpPaint.Status(c, f, footer, session.Error != ChessError.Ok ? ChessWire.Explain(session.Error) : !session.Connected ? "Подключение к серверу" : session.Busy ? "Ожидание ответа" : status, right);
        }

        private void Refresh(ChessView view) {
            if (view == null || cachedRoom != view.Id || cachedRoomRevision != view.Revision) { promotion = -1; selected = -1; legal = 0; claiming = false; }
            if (view == null) { status = "Выберите комнату"; right = ""; orientationSet = false; cachedRoom = cachedRoomRevision = 0; knownSeat = -2; return; }
            cachedRoom = view.Id; cachedRoomRevision = view.Revision;
            if ((!orientationSet || knownSeat != view.Me) && view.Me >= 0) { flipped = view.Me == 1; orientationSet = true; }
            knownSeat = view.Me;
            for (int i = 0; i < 2; i++) { names[i] = view.Present[i] ? view.Names[i] + (view.Me == i ? " (вы)" : "") : "Свободно"; seconds[i] = -1; }
            Board game = view.Game;
            right = view.Me < 0 ? "Наблюдение" : view.Me == 0 ? "Вы играете белыми" : "Вы играете чёрными";
            status = game == null ? "Ждём готовности игроков" : game.Result == ChessResult.Playing ? game.InCheck(game.Turn) ? "Шах" : game.Turn == view.Me ? "Ваш ход" : "Ход " + (game.Turn == 0 ? "белых" : "чёрных") :
                (game.Result == ChessResult.Draw ? "Ничья" : game.Result == ChessResult.White ? "Победили белые" : "Победили чёрные") + ". " + End(game);
        }
        private static string End(Board game) => game.End switch { ChessEnd.Mate => "Мат", ChessEnd.Stalemate => "Пат", ChessEnd.Dead => "Недостаточно материала", ChessEnd.Repetition => "Повторение позиции", ChessEnd.Moves => game.Halfmove >= 150 ? "Правило 75 ходов" : "Правило 50 ходов", ChessEnd.Agreement => "По соглашению", ChessEnd.Resigned => game.Result == ChessResult.White ? "Чёрные сдались" : "Белые сдались", ChessEnd.Time => "Время вышло", ChessEnd.Left => game.Result == ChessResult.White ? "Чёрные вышли" : "Белые вышли", _ => "" };

        private void Browser(ICanvas c, UiState ui, XpFaces f, Box area) {
            Box toolbar = area.TakeTop(26);
            if (XpPaint.Button(c, ui, f, 11, toolbar.TakeLeft(150), "Создать комнату", Available)) { session.Command(ChessOp.Create, minutes[time]); }
            if (XpPaint.Button(c, ui, f, 12, new Box(toolbar.X + 158, toolbar.Y, 110, 26), controls[time], Available)) { time = (time + 1) % controls.Length; }
            if (XpPaint.Button(c, ui, f, 13, toolbar.TakeRight(100), "Обновить", !session.Busy)) { session.Poll(); }
            Box frame = area.CutTop(36).CutBottom(36); XpPaint.Sunken(c, frame, XpLook.White);
            Box header = frame.Inset(1).TakeTop(24); c.Fill(header, XpLook.Surface);
            XpPaint.Label(c, f, "Комната", header.Inset(8, 0, 180, 0)); XpPaint.Label(c, f, "Игроки", header.TakeRight(175).TakeLeft(70)); XpPaint.Label(c, f, "Время", header.TakeRight(100));
            Box rows = frame.Inset(2).CutTop(26); int visible = Math.Max(1, (int)(rows.H / 28));
            if (rows.Contains(ui.In.X, ui.In.Y) && ui.In.Wheel != 0) { scroll -= Math.Sign(ui.In.Wheel); }
            scroll = Math.Clamp(scroll, 0, Math.Max(0, session.Rooms.Length - visible));
            for (int i = scroll; i < Math.Min(session.Rooms.Length, scroll + visible); i++) {
                ChessListing room = session.Rooms[i]; Box row = new Box(rows.X, rows.Y + (i - scroll) * 28, rows.W, 28);
                if (selectedRoom == room.Id) { c.Fill(row, Rgba.Hex(0x316AC5)); }
                Rgba ink = selectedRoom == room.Id ? XpLook.White : XpLook.Text;
                XpPaint.Label(c, f, room.Name, row.Inset(8, 0, 180, 0), ink);
                XpPaint.Label(c, f, room.Playing ? "Идёт игра" : room.Occupied == 2 ? "2 / 2" : room.Occupied == 1 ? "1 / 2" : "0 / 2", row.TakeRight(175).TakeLeft(80), ink);
                XpPaint.Label(c, f, room.Minutes == 0 ? "Без часов" : XpPaint.Num("", room.Minutes) + " мин", row.TakeRight(100), ink);
                if (ui.Click(1000 + i, row)) { selectedRoom = room.Id; }
            }
            if (session.Rooms.Length == 0) { XpPaint.Label(c, f, "Комнат пока нет", rows.Inset(8)); }
            Box buttons = area.TakeBottom(26);
            if (XpPaint.Button(c, ui, f, 14, buttons.TakeLeft(110), "Играть", Available && selectedRoom != 0)) { session.Command(ChessOp.Join, 0, selectedRoom); }
            if (XpPaint.Button(c, ui, f, 15, new Box(buttons.X + 118, buttons.Y, 125, 26), "Наблюдать", Available && selectedRoom != 0)) { session.Command(ChessOp.Join, 1, selectedRoom); }
        }

        private void Waiting(ICanvas c, UiState ui, XpFaces f, Box area, ChessView view) {
            XpPaint.Group(c, f, area.TakeTop(150), "Игроки");
            for (int i = 0; i < 2; i++) {
                Box row = new Box(area.X + 12, area.Y + 30 + i * 52, area.W - 24, 46);
                c.Image(Piece(i == 0 ? Chess.Board.King : -Chess.Board.King), row.TakeLeft(32), XpLook.White);
                XpPaint.Label(c, f, names[i], row.CutLeft(42).TakeTop(22));
                XpPaint.Label(c, f, !view.Present[i] ? "" : !view.Online[i] ? "Отошёл" : view.Ready[i] ? "Готов" : "Не готов", row.CutLeft(42).CutTop(22), XpLook.GroupHeader);
                if (!view.Present[i] && view.Me < 0 && XpPaint.Button(c, ui, f, 20 + i, row.TakeRight(100).TakeTop(26), "Занять место", Available)) { session.Command(ChessOp.Sit, (byte)i); }
            }
        }

        private void Board(ICanvas c, UiState ui, XpFaces f, Box area, ChessView view) {
            float cell = MathF.Floor(MathF.Min(area.W - 32, area.H - 32) / 8); Box grid = new Box(area.X + 22, area.Y + 8, cell * 8, cell * 8);
            Ui.Paint.Frame(c, grid.Inset(-2), 2, XpLook.InputBorder);
            c.Image("Content/UI/Chess/board.png", grid, XpLook.White);
            bool canMove = Available && view.Me == view.Game.Turn && view.Game.Result == ChessResult.Playing && promotion < 0 && !claiming;
            ReadOnlySpan<sbyte> board = view.Game.Squares;
            for (int row = 0; row < 8; row++) {
                for (int col = 0; col < 8; col++) {
                    int file = flipped ? 7 - col : col, rank = flipped ? row : 7 - row, square = rank * 8 + file;
                    Box box = new Box(grid.X + col * cell, grid.Y + row * cell, cell, cell);
                    if (square == selected) { c.Fill(box, Rgba.Hex(0xF2CD72).WithAlpha(140)); }
                    else if (square == view.Game.Last.From || square == view.Game.Last.To) { c.Fill(box, Rgba.Hex(0xCBD4A0).WithAlpha(100)); }
                    if ((legal & 1UL << square) != 0) { Ui.Paint.Frame(c, box.Inset(2), 2, Rgba.Hex(0x254D21)); }
                    if (board[square] != 0) { c.Image(Piece(board[square]), box.Inset(MathF.Max(2, cell * 0.08f)), XpLook.White); }
                    if (canMove && ui.Click(100 + square, box)) { Pick(view, square); }
                }
                XpPaint.Label(c, f, ranks[flipped ? row : 7 - row], new Box(area.X, grid.Y + row * cell, 18, cell), null, Align.Center);
                XpPaint.Label(c, f, files[flipped ? 7 - row : row], new Box(grid.X + row * cell, grid.Bottom + 4, cell, 18), null, Align.Center);
            }
        }
        private void Pick(ChessView view, int square) {
            if ((legal & 1UL << square) != 0 && selected >= 0) {
                int piece = Math.Abs(view.Game.Squares[selected]);
                if (piece == Chess.Board.Pawn && (square >> 3) is 0 or 7) { promotion = square; }
                else {
                    intended = new Move(selected, square);
                    for (int i = 0; i < view.Claims.Length; i++) { if (view.Claims[i].Equals(intended)) { claiming = true; return; } }
                    session.Command(ChessOp.Move, move: intended);
                }
                return;
            }
            int p = view.Game.Squares[square]; selected = p != 0 && (p > 0 ? 0 : 1) == view.Me ? square : -1; legal = 0;
            if (selected < 0) { return; }
            Span<Move> moves = stackalloc Move[Chess.Board.MaxMoves]; int count = view.Game.Generate(moves);
            for (int i = 0; i < count; i++) { if (moves[i].From == selected) { legal |= 1UL << moves[i].To; } }
        }

        private void Actions(ICanvas c, UiState ui, XpFaces f, Box area, ChessView view, XpShell shell, XpWindow self) {
            float y = area.Y; Board game = view.Game;
            for (int i = 0; i < 2; i++) {
                XpPaint.Label(c, f, names[i], new Box(area.X, y, area.W, 22)); y += 22;
                int remaining = (int)Math.Ceiling(Math.Max(0, view.Clock[i] - (game?.Result == ChessResult.Playing && game.Turn == i && view.Minutes != 0 ? shell.Now - session.SnapshotAt : 0)));
                if (seconds[i] != remaining) { seconds[i] = remaining; clocks[i] = view.Minutes == 0 ? "Без часов" : (remaining / 60).ToString("D2") + ":" + (remaining % 60).ToString("D2"); }
                XpPaint.Label(c, f, !view.Online[i] && view.Present[i] ? "Отошёл" : game != null ? clocks[i] : view.Ready[i] ? "Готов" : "Не готов", new Box(area.X, y, area.W, 24), XpLook.GroupHeader); y += 40;
            }
            bool playing = game?.Result == ChessResult.Playing;
            if (!playing) {
                if (XpPaint.Button(c, ui, f, 30, new Box(area.X, y, area.W, 26), view.Me >= 0 && view.Ready[view.Me] ? "Не готов" : "Готов", Available && view.Me >= 0)) { session.Command(ChessOp.Ready); } y += 34;
                if (XpPaint.Button(c, ui, f, 31, new Box(area.X, y, area.W, 26), game == null ? "Начать" : "Новая партия", Available && view.Owner && view.Ready[0] && view.Ready[1])) { session.Command(ChessOp.Start); } y += 34;
                if (XpPaint.Button(c, ui, f, 32, new Box(area.X, y, area.W, 26), "Поменять цвет", Available && view.Owner)) { session.Command(ChessOp.Swap); } y += 40;
                for (int i = 0; i < 2; i++) { if (!view.Present[i] && view.Me < 0 && XpPaint.Button(c, ui, f, 39 + i, new Box(area.X, y, area.W, 26), i == 0 ? "Сесть за белых" : "Сесть за чёрных", Available)) { session.Command(ChessOp.Sit, (byte)i); } if (!view.Present[i] && view.Me < 0) { y += 34; } }
            } else if (view.Me >= 0) {
                if (claiming) {
                    XpPaint.Label(c, f, "Можно требовать ничью", new Box(area.X, y, area.W, 22)); y += 28;
                    if (XpPaint.Button(c, ui, f, 60, new Box(area.X, y, area.W, 26), "Требовать ничью", Available)) { session.Command(ChessOp.Claim, move: intended); } y += 34;
                    if (XpPaint.Button(c, ui, f, 61, new Box(area.X, y, area.W, 26), "Сделать ход", Available)) { session.Command(ChessOp.Move, move: intended); } y += 34;
                    if (XpPaint.Button(c, ui, f, 62, new Box(area.X, y, area.W, 26), "Отмена", true)) { claiming = false; } y += 40;
                } else if (promotion >= 0) {
                    XpPaint.Label(c, f, "Превращение", new Box(area.X, y, area.W, 22)); y += 24;
                    for (int p = Chess.Board.Knight; p <= Chess.Board.Queen; p++) {
                        Box b = new Box(area.X + (p - 2) * 46, y, 42, 42); XpPaint.ButtonFace(c, b, Available, ui.Over(50 + p, b), false, false);
                        c.Image(Piece(view.Me == 0 ? p : -p), b.Inset(4), XpLook.White);
                        if (Available && ui.Click(50 + p, b)) { session.Command(ChessOp.Move, move: new Move(selected, promotion, p)); promotion = -1; }
                    }
                    y += 52;
                    if (XpPaint.Button(c, ui, f, 59, new Box(area.X, y, area.W, 26), "Отмена", true)) { promotion = -1; } y += 34;
                } else {
                    if (XpPaint.Button(c, ui, f, 33, new Box(area.X, y, area.W, 26), view.Offer == 1 - view.Me ? "Принять ничью" : "Предложить ничью", Available && view.Offer != view.Me)) { session.Command(ChessOp.Draw); } y += 34;
                    if (view.Offer == 1 - view.Me) { if (XpPaint.Button(c, ui, f, 34, new Box(area.X, y, area.W, 26), "Продолжить игру", Available)) { session.Command(ChessOp.Decline); } y += 34; }
                    if (view.Claim) { if (XpPaint.Button(c, ui, f, 35, new Box(area.X, y, area.W, 26), "Требовать ничью", Available && game.Turn == view.Me)) { session.Command(ChessOp.Claim, move: new Move(255, 255)); } y += 34; }
                    if (XpPaint.Button(c, ui, f, 36, new Box(area.X, y, area.W, 26), "Сдаться", Available)) { shell.Ask(self, Title, "Сдаться?", new[] { "Сдаться", "Продолжить" }, pick => { if (pick == 0) { session.Command(ChessOp.Resign); } }, "chess32"); } y += 40;
                }
            }
            if (game != null && XpPaint.Button(c, ui, f, 37, new Box(area.X, y, area.W, 26), "Развернуть доску", true)) { flipped = !flipped; }
        }
    }
}
