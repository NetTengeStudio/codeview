using System;
using System.Collections.Generic;
using ScpItdMod.Paper;
using ScpItdMod.Durak;
using ScpItdMod.Ui;

namespace ScpItdMod.Xp {
    public sealed class DurakApp : IXpApp, IXpSized, IXpTyping, IXpSessionLifecycle {
        private static readonly Rgba Felt = Rgba.Hex(0x008000), FeltDark = Rgba.Hex(0x005A00), Gold = Rgba.Hex(0xFFF090);
        private static readonly string[] menus = { "Игра", "Справка" };
        private static readonly string[] gameMenu = { "Обновить", "Покинуть комнату", "", "Закрыть" };
        private static readonly string[] helpMenu = { "Правила и управление", "О программе" };
        private static readonly string[] faces = CardPaths();
        private static readonly string[] timers = TimerLabels();
        private const string Back = "Content/UI/Durak/back.png";
        private const float CardW = 71, CardH = 96;
        private readonly DurakSession connection;
        private readonly DurakPresentation presentation = new DurakPresentation(Motion.MotionClient.Runtime);
        private static readonly Func<int, string> cardPath = id => faces[id];
        private readonly XpTextBox name = new XpTextBox(false, 36, 1);
        private readonly XpTextBox password = new XpTextBox(false, 24, 1, masked: true);
        private readonly XpTextBox joinPassword = new XpTextBox(false, 24, 1, masked: true);
        private readonly XpTextBox message = new XpTextBox(false, 160, 1);
        private readonly byte[] hand = new byte[DurakCards.Count];
        private readonly string[] playerLabels = new string[DurakCards.Seats];
        private readonly string[] playerStates = new string[DurakCards.Seats];
        private readonly List<string> chatLines = new List<string>(36);
        private DurakView cached;
        private string summary = "", prompt = "", result = "", preparation = "", notice = "", lastChat = "";
        private int focus = 0, selected = -1, handCount, capacity = 4, openMenu = -1, lastSecond = -1;
        private DurakRule rule;
        private uint selectedRoom;
        private float browserScroll, chatScroll, chatWidth;
        private string timer = "";
        private uint chatRoom, chatRevision;
        private bool sendingChat;
        private int sentChatVersion;
        private DurakListing[] listed;
        private string[] listingSeats = Array.Empty<string>();

        public DurakApp(XpShell shell) {
            name.Load("Комната отдыха");
            connection = shell.Cards;
            if (connection != null) { connection.Begin(shell.Pc.Computer, shell.Now); }
        }

        public string Title => "Дурак";
        public string Icon => "durak16";
        public InkEdit Edit => focus == 0 ? name.Edit : focus == 1 ? password.Edit : focus == 2 ? message.Edit : focus == 3 ? joinPassword.Edit : null;

        public void Size(XpWindow window) { window.Rect = new Box(0, 0, 920, 666); window.MinW = 780; window.MinH = 640; }

        private static string[] CardPaths() {
            var paths = new string[DurakCards.Count];
            for (int i = 0; i < paths.Length; i++) { paths[i] = "Content/UI/Durak/card_" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture) + ".png"; }
            return paths;
        }

        private static string[] TimerLabels() {
            var labels = new string[(int)DurakLobby.TurnSeconds + 1];
            for (int i = 0; i < labels.Length; i++) { labels[i] = "Осталось " + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + " с"; }
            return labels;
        }

        public void Paint(ICanvas c, UiState ui, XpFaces f, Box client, XpShell shell, XpWindow self) {
            c.Fill(client, XpLook.Surface);
            Box bar = client.TakeTop(XpLook.MenuH);
            int menu = XpPaint.Menu(c, ui, f, 10, bar, menus, openMenu);
            if (menu >= 0) { Menu(shell, self, f, bar, menu); }
            Box status = client.TakeBottom(XpLook.StatusH);
            Box body = client.CutTop(bar.H).CutBottom(status.H).Inset(8);
            if (connection == null) {
                XpPaint.Label(c, f, "Для сетевой игры откройте компьютер на сервере с установленным SCPITD.", body);
                return;
            }
            DurakView view = connection.View;
            if (sendingChat && !connection.Busy) {
                if (connection.Error == DurakError.Ok && message.Edit?.Version == sentChatVersion) { message.Load(""); }
                sendingChat = false;
            }
            Cache(view);
            var top = body.TakeTop(32);
            c.Image(XpLook.Image("durak32"), new Box(top.X, top.Y, 26, 30), XpLook.White);
            XpPaint.Label(c, f, view == null ? "Дурак по сети" : view.Name, new Box(top.X + 34, top.Y, top.W - 190, 17));
            XpPaint.Label(c, f, view == null ? "36 карт, от 2 до 6 игроков" : summary,
                new Box(top.X + 34, top.Y + 17, top.W - 190, 14), XpLook.GroupHeader);
            if (view != null && XpPaint.Button(c, ui, f, 24, new Box(top.Right - 138, top.Y + 2, 138, XpLook.ButtonH), "Покинуть комнату", CanAct)) { Leave(shell, self); }
            Box main = body.CutTop(38);
            if (view == null) { Browser(c, ui, f, main, shell, self); }
            else {
                Box chat = main.TakeBottom(112);
                Box play = main.CutBottom(120);
                if (view.Phase == DurakPhase.Playing) { Table(c, ui, f, play, shell, view); }
                else { Waiting(c, ui, f, play, shell, view); }
                Chat(c, ui, f, chat, shell, view);
            }
            string left = connection.Error != DurakError.Ok ? DurakWire.Explain(connection.Error) : notice.Length > 0 ? notice :
                !connection.Connected ? "Подключение к серверу..." : connection.Busy ? "Ожидание ответа сервера..." :
                view == null ? "Выберите комнату или создайте свою" : view.Me < 0 ? "Вы наблюдаете" : "";
            XpPaint.Status(c, f, status, left, connection.Connected ? "Подключено" : "Нет связи");
        }

        private bool CanAct => connection != null && connection.Connected && !connection.Busy && connection.Error != DurakError.Unavailable;

        private void Cache(DurakView view) {
            if (!ReferenceEquals(listed, connection.Rooms)) {
                listed = connection.Rooms;
                listingSeats = new string[listed.Length];
                for (int i = 0; i < listed.Length; i++) { listingSeats[i] = listed[i].Occupied + " / " + listed[i].Capacity; }
            }
            if (ReferenceEquals(cached, view)) { return; }
            if (cached == null && view != null) { password.Load(""); joinPassword.Load(""); focus = -1; }
            cached = view;
            notice = "";
            handCount = 0;
            lastSecond = -1;
            if (view != null && view.Phase != DurakPhase.Playing) { presentation.Dispose(); }
            if (view == null) { selected = -1; presentation.Reconcile(null, ReadOnlySpan<byte>.Empty, default, default, default, default); return; }
            summary = (view.Rule == DurakRule.Transfer ? "Переводной" : "Подкидной") + ", 36 карт, " + view.Occupied + " из " + view.Capacity + " мест" + (view.Spectators > 0 ? ", зрителей " + view.Spectators : "");
            int ready = 0;
            for (int i = 0; i < view.Capacity; i++) {
                DurakPlayerView player = view.Players[i];
                if (player.Present && player.Ready) { ready++; }
                playerLabels[i] = !player.Present ? "Свободное место" : player.Name + (i == view.Me ? " (вы)" : "");
                playerStates[i] = !player.Present ? "" : !player.Online ? "Отошёл" : view.Phase == DurakPhase.Playing ?
                    player.Out ? "Без карт" : "Карт " + player.Cards + (i == view.Defender ? ", отбивается" : i == view.AttackTurn ? ", ходит" : "") :
                    (player.Ready ? "Готов" : "Не готов") + (i == view.Owner ? ", хозяин" : player.Bot ? ", компьютер" : "");
            }
            int trump = DurakCards.Valid(view.TrumpCard) ? DurakCards.Suit(view.TrumpCard) : -1;
            for (int card = 0; card < DurakCards.Count; card++) {
                if (DurakCards.Suit(card) != trump && (view.Hand & (1UL << card)) != 0) { hand[handCount++] = (byte)card; }
            }
            for (int card = 0; card < DurakCards.Count; card++) {
                if (DurakCards.Suit(card) == trump && (view.Hand & (1UL << card)) != 0) { hand[handCount++] = (byte)card; }
            }
            if (!DurakCards.Valid(selected) || (view.Hand & (1UL << selected)) == 0) { selected = -1; }
            prompt = view.Me >= 0 && view.Players[view.Me].Out ? "Вы вышли из игры без карт" :
                view.Taking ? view.Me == view.Defender ? "Вы берёте. Ждём подкидывания" : view.Me == view.AttackTurn ? "Можно подкинуть" : "Ждём подкидывания" :
                view.Me == view.Defender && view.TableCount > 0 ? "Выберите свою карту, затем побейте карту на столе" :
                view.Me == view.AttackTurn ? view.TableCount == 0 ? "Ваш ход" : "Можно подкинуть" :
                view.AttackTurn >= 0 ? "Ходит " + view.Players[view.AttackTurn].Name : "Ждём окончания хода";
            result = view.Phase == DurakPhase.Finished ? view.Loser < 0 ? "Ничья" :
                view.Loser == view.Me ? "Вы остались с картами" : "С картами остался " + (view.Players[view.Loser].Name ?? "вышедший игрок") :
                "Готовы " + ready + " из " + view.Occupied;
            preparation = view.Occupied < 2 ? "Нужен ещё один игрок" : ready < view.Occupied ? "Ждём готовности игроков" : view.Me == view.Owner ? "Можно сдавать карты" : "Ждём раздачи";
        }

        private void Browser(ICanvas c, UiState ui, XpFaces f, Box area, XpShell shell, XpWindow self) {
            Box form = area.TakeRight(254);
            Box listArea = area.CutRight(264);
            XpPaint.Group(c, f, listArea, "Комнаты");
            Box list = listArea.Inset(8, 20, 8, 74);
            XpPaint.Sunken(c, list, XpLook.White);
            Box header = list.Inset(1).TakeTop(22);
            c.Shade(header, XpLook.TabFace, true, 1);
            float modeX = list.X + list.W * 0.48f, seatsX = list.X + list.W * 0.74f;
            XpPaint.Label(c, f, "Название", new Box(list.X + 7, header.Y, modeX - list.X - 14, 22));
            XpPaint.Label(c, f, "Правила", new Box(modeX, header.Y, 100, 22));
            XpPaint.Label(c, f, "Игроки", new Box(seatsX, header.Y, 60, 22));
            Box content = list.Inset(1).CutTop(22);
            Box rows = XpPaint.Scroll(c, ui, 130, content, listed.Length * 32, ref browserScroll);
            c.PushClip(rows);
            bool found = false;
            for (int i = 0; i < listed.Length; i++) {
                DurakListing entry = listed[i];
                found |= entry.Id == selectedRoom;
                Box row = new Box(rows.X, rows.Y + i * 32 - browserScroll, rows.W, 32);
                if (row.Bottom < rows.Y || row.Y >= rows.Bottom) { continue; }
                bool selectedRow = entry.Id == selectedRoom;
                if (selectedRow) { c.Fill(row, XpLook.Select); }
                Rgba ink = selectedRow ? XpLook.White : XpLook.Text;
                c.Image(XpLook.Image("durak16"), new Box(row.X + 4, row.Y + 5, 16, 16), XpLook.White);
                XpPaint.Label(c, f, entry.Name, new Box(row.X + 26, row.Y + 1, modeX - row.X - 32, 16), ink);
                XpPaint.Label(c, f, entry.Locked ? "С паролем" : entry.Phase == DurakPhase.Playing ? "Идёт игра" : entry.Phase == DurakPhase.Finished ? "Реванш" : "Ожидание",
                    new Box(row.X + 26, row.Y + 16, modeX - row.X - 32, 14), ink);
                XpPaint.Label(c, f, entry.Rule == DurakRule.Transfer ? "Переводной" : "Подкидной", new Box(modeX, row.Y, seatsX - modeX - 6, row.H), ink);
                XpPaint.Label(c, f, listingSeats[i], new Box(seatsX, row.Y, row.Right - seatsX, row.H), ink);
                if (ui.Click(200 + i, row) && selectedRoom != entry.Id) { selectedRoom = entry.Id; joinPassword.Load(""); if (focus == 3) { focus = -1; } }
            }
            c.PopClip();
            if (!found && listed.Length > 0) { selectedRoom = listed[0].Id; joinPassword.Load(""); }
            if (listed.Length == 0) {
                selectedRoom = 0;
                XpPaint.Label(c, f, connection.Connected ? "Комнат пока нет" : "Поиск комнат...", new Box(list.X + 14, list.Y + 45, list.W - 28, 18));
            }
            float y = listArea.Bottom - 64;
            bool joinable = false, locked = false;
            for (int i = 0; i < listed.Length; i++) { if (listed[i].Id == selectedRoom) { joinable = listed[i].Phase != DurakPhase.Playing && listed[i].Occupied < listed[i].Capacity; locked = listed[i].Locked; } }
            if (XpPaint.Button(c, ui, f, 30, new Box(listArea.X + 8, y, 112, 25), "Войти", CanAct && joinable, true)) { Join(false); }
            if (XpPaint.Button(c, ui, f, 31, new Box(listArea.X + 126, y, 104, 25), "Наблюдать", CanAct && selectedRoom != 0)) { Join(true); }
            if (XpPaint.Button(c, ui, f, 32, new Box(listArea.Right - 100, y, 92, 25), "Обновить", connection.Active)) { connection.Poll(true); }
            if (locked) {
                XpPaint.Label(c, f, "Пароль", new Box(listArea.X + 9, y + 32, 56, 23));
                Field(c, ui, f, shell, self, joinPassword, 3, new Box(listArea.X + 68, y + 32, listArea.W - 77, 23));
            } else if (focus == 3) { focus = -1; }

            XpPaint.Group(c, f, form, "Новая комната");
            Box inner = form.Inset(12, 26, 12, 12);
            XpPaint.Label(c, f, "Название", inner.TakeTop(17));
            Field(c, ui, f, shell, self, name, 0, new Box(inner.X, inner.Y + 20, inner.W, 23));
            XpPaint.Label(c, f, "Правила", new Box(inner.X, inner.Y + 57, inner.W, 17));
            bool normal = rule == DurakRule.ThrowIn;
            if (XpPaint.Button(c, ui, f, 36, new Box(inner.X, inner.Y + 78, 108, 25), "Подкидной", true, normal)) { rule = DurakRule.ThrowIn; }
            if (XpPaint.Button(c, ui, f, 37, new Box(inner.X + 114, inner.Y + 78, 108, 25), "Переводной", true, !normal)) { rule = DurakRule.Transfer; }
            XpPaint.Label(c, f, "Мест за столом", new Box(inner.X, inner.Y + 117, inner.W, 17));
            if (XpPaint.Button(c, ui, f, 38, new Box(inner.X, inner.Y + 138, 28, 24), "−", capacity > 2)) { capacity--; }
            XpPaint.Field(c, f, new Box(inner.X + 34, inner.Y + 138, 44, 24), XpPaint.Num("", capacity), true);
            if (XpPaint.Button(c, ui, f, 39, new Box(inner.X + 84, inner.Y + 138, 28, 24), "+", capacity < 6)) { capacity++; }
            XpPaint.Label(c, f, "Пароль (необязательно)", new Box(inner.X, inner.Y + 207, inner.W, 17));
            Field(c, ui, f, shell, self, password, 1, new Box(inner.X, inner.Y + 227, inner.W, 23));
            if (XpPaint.Button(c, ui, f, 40, new Box(inner.X, inner.Y + 267, inner.W, 27), "Создать комнату", CanAct && !string.IsNullOrWhiteSpace(name.Text))) { Create(); }
        }

        private void Waiting(ICanvas c, UiState ui, XpFaces f, Box area, XpShell shell, DurakView view) {
            bool host = view.Me >= 0 && view.Me == view.Owner;
            Box roster = area.CutRight(242);
            XpPaint.Group(c, f, roster, view.Phase == DurakPhase.Finished ? "Результат партии" : "Игроки за столом");
            XpPaint.Label(c, f, result, new Box(roster.X + 12, roster.Y + 20, roster.W - 24, 28), XpLook.GroupHeader);
            float y = roster.Y + 57;
            for (int i = 0; i < view.Capacity; i++) {
                Box row = new Box(roster.X + 10, y + i * 39, roster.W - 20, 36);
                DurakPlayerView player = view.Players[i];
                if (i == view.Me) { c.Fill(row, Rgba.Hex(0xD9E8FB)); }
                c.Image(XpLook.Image(player.Bot ? "computer16" : "durak16"), new Box(row.X + 5, row.Y + 7, 16, 16), player.Present ? XpLook.White : XpLook.White.WithAlpha(100));
                bool kick = player.Present && !player.Bot && i != view.Me && host;
                float labelWidth = row.W - (kick ? 104 : 68);
                XpPaint.Label(c, f, playerLabels[i], new Box(row.X + 28, row.Y + 1, labelWidth, 16), player.Present ? XpLook.Text : XpLook.Shadow);
                XpPaint.Label(c, f, playerStates[i], new Box(row.X + 28, row.Y + 18, labelWidth, 15), XpLook.GroupHeader);
                if (player.Bot && host && XpPaint.Button(c, ui, f, 400 + i, new Box(row.Right - 32, row.Y + 6, 25, 23), "×", CanAct)) { connection.Command(DurakOp.RemoveBot, i); }
                if (kick && XpPaint.Button(c, ui, f, 410 + i, new Box(row.Right - 68, row.Y + 6, 62, 23), "Удалить", CanAct)) {
                    int seat = i;
                    shell.Ask(shell.FocusedWindow, "Дурак", "Удалить игрока «" + player.Name + "» из комнаты?", new[] { "Удалить", "Отмена" }, picked => {
                        if (picked == 0) { connection.Command(DurakOp.Kick, seat); }
                    }, "durak32");
                }
            }
            Box controls = area.TakeRight(232);
            XpPaint.Group(c, f, controls, "Подготовка к игре");
            Box inner = controls.Inset(12, 22, 12, 12);
            XpPaint.Label(c, f, host ? "Ваша комната" : "Раздаёт хозяин комнаты", inner.TakeTop(32));
            if (view.Me >= 0) {
                bool ready = view.Players[view.Me].Ready;
                if (XpPaint.Button(c, ui, f, 42, new Box(inner.X, inner.Y + 42, inner.W, 30), ready ? "Не готов" : "Готов", CanAct, !ready)) { connection.Command(DurakOp.Ready); }
            } else if (XpPaint.Button(c, ui, f, 43, new Box(inner.X, inner.Y + 42, inner.W, 30), "Занять место", CanAct && view.Occupied < view.Capacity)) { connection.Command(DurakOp.Sit); }
            if (host) {
                if (XpPaint.Button(c, ui, f, 44, new Box(inner.X, inner.Y + 84, inner.W, 28), "Добавить компьютер", CanAct && view.Occupied < view.Capacity)) { connection.Command(DurakOp.AddBot); }
                if (XpPaint.Button(c, ui, f, 45, new Box(inner.X, inner.Y + 124, inner.W, 32), view.Phase == DurakPhase.Finished ? "Реванш" : "Сдать карты", CanAct && view.CanStart, true)) { connection.Command(DurakOp.Start); focus = -1; }
            }
            XpPaint.Label(c, f, preparation, new Box(inner.X, inner.Y + (host ? 174 : 84), inner.W, 32), XpLook.GroupHeader);
        }

        private void Table(ICanvas c, UiState ui, XpFaces f, Box area, XpShell shell, DurakView view) {
            Box sidebar = area.TakeRight(164);
            Box table = area.CutRight(172);
            XpPaint.Sunken(c, table, Felt);
            Box felt = table.Inset(4);
            XpPaint.Label(c, f, prompt, new Box(felt.X + 7, felt.Y + 3, felt.W - 14, 29), XpLook.White);
            Box actions = felt.TakeBottom(29);
            Box playField = felt.CutRight(110);
            Box mine = playField.CutBottom(37).TakeBottom(110);
            Box center = playField.CutTop(84).CutBottom(153);
            Box deck = new Box(felt.Right - 94, felt.Y + 58, 59, 80);
            Box discard = new Box(felt.Right - 94, felt.Bottom - 145, 59, 80);
            XpPaint.Label(c, f, XpPaint.Num("Колода ", view.Remaining), new Box(deck.X - 6, deck.Y - 25, 94, 18), XpLook.White);
            Card(c, view.TrumpCard, deck.Offset(22, 12), XpLook.White);
            if (view.Remaining > 0) { c.Image(Back, deck, XpLook.White); }
            XpPaint.Label(c, f, XpPaint.Num("Отбой ", view.DiscardCount), new Box(discard.X - 6, discard.Y - 25, 94, 18), XpLook.White);
            if (view.DiscardCount > 0) { c.Image(Back, discard, XpLook.White.WithAlpha(190)); }
            if (view.TableCount == 0) {
                if (ui.Click(490, center) && selected >= 0 && (view.CanAttack & (1UL << selected)) != 0 && CanAct) { connection.Move(DurakMove.Attack, selected); }
            }
            presentation.Reconcile(view, hand.AsSpan(0, handCount), mine, center, deck, discard);
            float stride = MathF.Min(96, (center.W - 14) / DurakCards.Pairs);
            float startX = MathF.Round(center.CenterX - (stride * view.TableCount - (stride - 87)) * 0.5f);
            float cardY = MathF.Round(center.CenterY - 58);
            for (int i = 0; i < view.TableCount; i++) {
                Box attack = new Box(startX + i * stride, cardY, CardW, CardH);
                attack = presentation.Pose(view.Attacks[i]).Bounds;
                if (view.Defenses[i] == DurakCards.None) {
                    bool can = selected >= 0 && (view.CanDefend[i] & (1UL << selected)) != 0;
                    if (can) { Border(c, attack.Inset(-2), Gold); }
                    if (ui.Click(500 + i, presentation.Pose(view.Attacks[i])) && CanAct && selected >= 0) {
                        if (can) { connection.Move(DurakMove.Defend, selected, i); }
                        else { notice = "Эта карта не бьёт выбранную. Нужна старшая той же масти или козырь."; }
                    }
                }
            }
            OwnHand(c, ui, f, mine, shell, view);
            c.PushClip(felt);
            presentation.Paint(c, cardPath);
            c.PopClip();
            float x = actions.X + 5;
            if (view.Me >= 0 && view.Me == view.Defender && !view.Taking && view.TableCount > 0) {
                bool defendable = false;
                for (int i = 0; i < view.TableCount; i++) { defendable |= selected >= 0 && (view.CanDefend[i] & (1UL << selected)) != 0; }
                if (XpPaint.Button(c, ui, f, 54, new Box(x, actions.Y + 2, 94, 25), "Побить", CanAct && defendable, true)) { PlaySelected(view); }
                x += 101;
                if (view.Rule == DurakRule.Transfer) {
                    bool transferable = selected >= 0 && (view.CanTransfer & (1UL << selected)) != 0;
                    if (XpPaint.Button(c, ui, f, 51, new Box(x, actions.Y + 2, 94, 25), "Перевести", CanAct && transferable)) { connection.Move(DurakMove.Transfer, selected); }
                    x += 101;
                }
                if (XpPaint.Button(c, ui, f, 52, new Box(x, actions.Y + 2, 75, 25), "Беру", CanAct && view.CanTake)) { connection.Move(DurakMove.Take); }
            } else if (view.Me >= 0 && view.Me == view.AttackTurn) {
                bool attackable = selected >= 0 && (view.CanAttack & (1UL << selected)) != 0;
                if (XpPaint.Button(c, ui, f, 50, new Box(x, actions.Y + 2, 94, 25), view.TableCount == 0 ? "Сходить" : "Подкинуть", CanAct && attackable, true)) { connection.Move(DurakMove.Attack, selected); }
                bool covered = view.TableCount > 0;
                for (int i = 0; i < view.TableCount; i++) { covered &= view.Defenses[i] != DurakCards.None; }
                if (view.TableCount > 0 && XpPaint.Button(c, ui, f, 53, new Box(x + 101, actions.Y + 2, 126, 25), covered ? "Бито" : "Не подкидываю", CanAct && view.CanPass)) { connection.Move(DurakMove.Pass); }
            }

            XpPaint.Group(c, f, sidebar, "Игроки");
            for (int i = 0; i < view.Capacity; i++) {
                DurakPlayerView player = view.Players[i];
                if (!player.Present) { continue; }
                Box row = new Box(sidebar.X + 8, sidebar.Y + 20 + i * 34, sidebar.W - 16, 33);
                if (i == view.Defender || i == view.AttackTurn) { c.Fill(row, Rgba.Hex(0xD9E8FB)); }
                XpPaint.Label(c, f, playerLabels[i], row.TakeTop(16));
                XpPaint.Label(c, f, playerStates[i], row.CutTop(17).TakeTop(15), XpLook.GroupHeader);
            }
            float deckY = sidebar.Y + 28 + view.Capacity * 34;
            int second = Math.Clamp((int)Math.Ceiling(view.Milliseconds / 1000.0 - (shell.Now - connection.SnapshotAt)), 0, (int)DurakLobby.TurnSeconds);
            if (second != lastSecond) { lastSecond = second; timer = timers[second]; }
            XpPaint.Label(c, f, timer, new Box(sidebar.X + 12, deckY + 130, sidebar.W - 24, 18), second <= 10 ? Rgba.Hex(0x9C2020) : XpLook.GroupHeader);
        }

        private void OwnHand(ICanvas c, UiState ui, XpFaces f, Box box, XpShell shell, DurakView view) {
            c.Fill(box.TakeTop(1), FeltDark);
            XpPaint.Label(c, f, view.Me < 0 ? "Вы наблюдаете за игрой." : handCount == 0 ? "В руке нет карт." : "Ваши карты", box.TakeTop(15), XpLook.White);
            int hot = presentation.HitHand(ui.In.X, ui.In.Y);
            presentation.Interact(selected, ui.In.X, ui.In.Y, ui.In.Down);
            if (hot >= 0 && ui.In.Pressed) { selected = hot; focus = -1; notice = ""; presentation.BeginDrag(hot, ui.In.X, ui.In.Y); }
            if (presentation.DragCard >= 0 && ui.In.Down) { presentation.Drag(ui.In.X, ui.In.Y); }
            if (ui.In.Released && presentation.DragCard >= 0) {
                if (presentation.Dragged && CanAct) {
                    int pair = presentation.AttackAt(ui.In.X, ui.In.Y);
                    if (pair >= 0 && (view.CanDefend[pair] & (1UL << selected)) != 0) { connection.Move(DurakMove.Defend, selected, pair); }
                    else if (presentation.OverTable(ui.In.X, ui.In.Y) && (view.CanAttack & (1UL << selected)) != 0) { connection.Move(DurakMove.Attack, selected); }
                    else if (presentation.OverTable(ui.In.X, ui.In.Y) && (view.CanTransfer & (1UL << selected)) != 0) { connection.Move(DurakMove.Transfer, selected); }
                } else if (hot == selected && shell.Clicked(30000 + selected, out bool twice) && twice) { PlaySelected(view); }
                presentation.EndDrag();
            }
        }

        public static void Card(ICanvas c, int card, in Box rect, Rgba? tint = null) {
            if (DurakCards.Valid(card)) { c.Image(faces[card], rect, tint ?? XpLook.White); }
        }

        private static void Border(ICanvas c, in Box b, Rgba color) {
            c.Fill(b.TakeTop(2), color); c.Fill(b.TakeBottom(2), color); c.Fill(b.TakeLeft(2), color); c.Fill(b.TakeRight(2), color);
        }

        private void Chat(ICanvas c, UiState ui, XpFaces f, Box area, XpShell shell, DurakView view) {
            XpPaint.Group(c, f, area, "Чат комнаты");
            Box entry = new Box(area.X + 8, area.Bottom - 30, area.W - 108, 23);
            Box list = new Box(area.X + 8, area.Y + 18, area.W - 16, area.H - 53);
            XpPaint.Sunken(c, list, XpLook.White);
            float width = list.W - XpLook.ScrollW - 12;
            string latest = view.Feed.Length == 0 ? "" : view.Feed[view.Feed.Length - 1];
            if (chatRoom != view.Id || chatRevision != view.FeedRevision || MathF.Abs(chatWidth - width) > 1) {
                bool changed = !string.Equals(latest, lastChat, StringComparison.Ordinal);
                chatRoom = view.Id; chatRevision = view.FeedRevision; lastChat = latest; chatWidth = width; chatLines.Clear();
                for (int i = 0; i < view.Feed.Length; i++) {
                    string line = view.Feed[i];
                    int at = 0;
                    while (at < line.Length) {
                        int count = Math.Max(1, Ui.Paint.Fit(f.Body, line.AsSpan(at), width));
                        count = Math.Min(count, line.Length - at);
                        if (count < line.Length - at) { int space = line.LastIndexOf(' ', at + count - 1, count); if (space > at) { count = space - at + 1; } }
                        chatLines.Add(line.Substring(at, count)); at += count;
                    }
                }
                if (changed) { chatScroll = float.MaxValue; }
            }
            Box rows = XpPaint.Scroll(c, ui, 720, list.Inset(1), chatLines.Count * 14 + 4, ref chatScroll);
            c.PushClip(rows);
            for (int i = 0; i < chatLines.Count; i++) {
                Box row = new Box(rows.X + 4, rows.Y + 2 + i * 14 - chatScroll, rows.W - 8, 14);
                if (row.Bottom >= rows.Y && row.Y < rows.Bottom) { XpPaint.Label(c, f, chatLines[i], row); }
            }
            c.PopClip();
            Field(c, ui, f, shell, shell.FocusedWindow, message, 2, entry);
            if (XpPaint.Button(c, ui, f, 72, new Box(entry.Right + 8, entry.Y, 84, 23), "Отправить", CanAct && !sendingChat && !string.IsNullOrWhiteSpace(message.Text))) { SendChat(); }
        }

        private void Field(ICanvas c, UiState ui, XpFaces f, XpShell shell, XpWindow self, XpTextBox box, int which, in Box rect) {
            box.Paint(c, ui, 800 + which * 4, f.Body, rect, focus == which && ReferenceEquals(shell.FocusedWindow, self), shell.Now, out bool clicked);
            if (clicked) { focus = which; }
        }

        private void Create() { if (CanAct && connection.Command(DurakOp.Create, capacity, (int)rule, name.Text, password.Text)) { focus = -1; } }
        private void Join(bool watch) {
            if (!CanAct) { return; }
            for (int i = 0; i < listed.Length; i++) {
                DurakListing room = listed[i];
                if (room.Id != selectedRoom) { continue; }
                if (!watch && (room.Phase == DurakPhase.Playing || room.Occupied >= room.Capacity)) { return; }
                if (connection.Command(watch ? DurakOp.Watch : DurakOp.Join, password: room.Locked ? joinPassword.Text : "", room: selectedRoom)) { focus = room.Locked ? 3 : -1; }
                return;
            }
        }
        private void SendChat() {
            if (!CanAct || sendingChat || string.IsNullOrWhiteSpace(message.Text)) { return; }
            sentChatVersion = message.Edit?.Version ?? -1;
            sendingChat = true;
            if (!connection.Command(DurakOp.Chat, text: message.Text)) { sendingChat = false; }
        }

        private void PlaySelected(DurakView view) {
            if (!CanAct || view == null || selected < 0) { return; }
            ulong bit = 1UL << selected;
            if ((view.CanAttack & bit) != 0) { connection.Move(DurakMove.Attack, selected); return; }
            int target = -1;
            for (int i = 0; i < view.TableCount; i++) {
                if ((view.CanDefend[i] & bit) == 0) { continue; }
                if (target >= 0) { notice = "Выберите на столе карту, которую хотите побить."; return; }
                target = i;
            }
            if (target >= 0) { connection.Move(DurakMove.Defend, selected, target); }
        }

        public bool Command(XpShell shell, char control) {
            if (control == '\t') {
                if (connection?.View == null) {
                    bool locked = false;
                    if (listed != null) { for (int i = 0; i < listed.Length; i++) { locked |= listed[i].Id == selectedRoom && listed[i].Locked; } }
                    focus = focus == 0 ? 1 : focus == 1 && locked ? 3 : 0;
                } else { focus = focus == 2 ? -1 : 2; }
                return true;
            }
            if (control != '\r' && control != '\n') { return false; }
            if (connection?.View == null) { if (focus == 3) { Join(false); } else { Create(); } }
            else if (focus == 2) { SendChat(); }
            else { PlaySelected(connection.View); }
            return true;
        }

        private void Leave(XpShell shell, XpWindow self) {
            if (connection.View?.Phase == DurakPhase.Playing && connection.View.Me >= 0) {
                shell.Ask(self, "Дурак", "Сдаться и покинуть комнату?\nВаши карты доиграет компьютер.", new[] { "Сдаться", "Остаться" }, picked => {
                    if (picked == 0) { connection.Command(DurakOp.Leave); }
                }, "durak32");
            } else { connection.Command(DurakOp.Leave); }
        }

        private void Menu(XpShell shell, XpWindow self, XpFaces f, in Box bar, int index) {
            openMenu = index;
            if (index == 0) {
                shell.Popup(self, XpPaint.MenuX(f, bar, menus, index), bar.Bottom, gameMenu,
                    new[] { connection != null, connection?.View != null && CanAct, true, true }, null, picked => {
                        openMenu = -1;
                        if (picked == 0) { connection.Poll(true); }
                        else if (picked == 1) { Leave(shell, self); }
                        else if (picked == 3) { shell.RequestClose(self); }
                    });
            } else {
                shell.Popup(self, XpPaint.MenuX(f, bar, menus, index), bar.Bottom, helpMenu, null, null, picked => {
                    openMenu = -1;
                    if (picked == 0) {
                        shell.Ask(self, "Правила и управление",
                            "36 карт от шестёрки до туза. Каждому по 6 карт.\nНачинает игрок с младшим козырем.\n\nБейте старшей картой той же масти или козырем.\nПодкидывайте достоинства, которые уже есть на столе.\nЗа ход можно подкинуть не больше 6 карт\nи не больше, чем было у защищающегося.\nВ переводном до первого отбивания можно положить\nкарту того же достоинства и перевести атаку дальше.\n\nВыберите свою карту, затем карту на столе.\nКнопка «Побить» и двойной щелчок играют карту,\nесли ей можно побить только одну карту на столе.\nEnter играет карту или отправляет сообщение.\nTab переключает поля ввода.\n\n«Беру» позволяет соперникам ещё подкинуть.\n«Не подкидываю» передаёт очередь следующему игроку.\n«Бито» делает то же, когда все карты побиты.\nПервым добирает атакующий, последним защищавшийся.\nВзявший карты пропускает ход. Последний с картами дурак.\nЧерез 45 секунд бездействия за вас ходит компьютер.", new[] { "ОК" }, _ => { }, "durak32");
                    } else {
                        shell.Ask(self, "О программе «Дурак»", "Подкидной и переводной дурак по сети.\nОт 2 до 6 игроков. Можно играть с компьютером.\n\nКарты devurandom с OpenGameArt.\nЛицензия CC0.\n\nПолные сведения об источнике находятся\nв Content/UI/Durak/LICENSE.txt.", new[] { "ОК" }, _ => { }, "durak32");
                    }
                });
            }
        }

        public bool WantsClose(XpShell shell, XpWindow self) => true;

        public void OnSessionSuspending(XpShell shell) => connection?.Suspend();

        public void OnSessionResumed(XpShell shell) {
            if (connection != null) { connection.Begin(shell.Pc.Computer, shell.Now); }
        }

        public void Closed(XpShell shell) { presentation.Dispose(); connection?.Suspend(); }
    }
}
