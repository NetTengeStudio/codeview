using System;
using ScpItdMod.Pc;
using ScpItdMod.Durak;
using ScpItdMod.Ui;

namespace ScpItdMod.Xp {
    public static class DisplayPaint {
        private static readonly string[] titles = { "Рабочая станция", "Проводник", "Блокнот", "Личные дела", "Печать", "Камеры наблюдения", "Дурак", "Шахматы", "Метеослужба" };
        private static readonly string[] desktopNames = { "Мой компьютер", "Мои документы", "Личные дела", "Камеры", "Принтеры", "Корзина", "Дурак", "Шахматы", "Метеорология" };
        private static readonly string[] desktopIcons = { "computer32", "folder32", "dossier32", "camera32", "printer32", "recycle32", "durak32", "chess32", "meteo32" };
        private static readonly Rgba Felt = Rgba.Hex(0x144834);
        private static readonly string[] counts = Counts();

        private static string[] Counts() {
            var result = new string[37];
            for (int i = 0; i < result.Length; i++) { result[i] = i.ToString(System.Globalization.CultureInfo.InvariantCulture); }
            return result;
        }

        public static void Draw(ICanvas canvas, XpFaces faces, DisplayView view, in Box bounds) {
            Box area = bounds;
            PcApp app = view?.Ready == true ? view.State.App : PcApp.Desktop;
            PublicDisplayMode mode = DisplaySource.Mode(view?.State ?? default, view?.Ready == true);
            if (mode == PublicDisplayMode.Off) { canvas.Fill(area, XpLook.Black); return; }
            Desktop(canvas, faces, area);
            if (mode == PublicDisplayMode.Desktop) { return; }
            if (mode == PublicDisplayMode.Starting || mode == PublicDisplayMode.Private) {
                Box dialog = new Box(area.CenterX - 240, area.CenterY - 65, 480, 130);
                XpPaint.Window(canvas, faces, dialog, "Рабочая станция", "computer16", true, false, true, false, false, -1, false);
                Paint.Text(canvas, faces.Header, mode == PublicDisplayMode.Private ? "Защищённый сеанс" : "Подключение к рабочей станции...", XpDesk.Client(dialog), XpLook.Text, Align.Center);
                return;
            }
            area = area.Inset(100, 40, 20, 50);
            canvas.Fill(area, XpLook.Surface);
            Box title = area.TakeTop(32);
            canvas.Fill(title, XpLook.GroupHeader);
            Paint.Text(canvas, faces.Header, titles[(int)app], title.Inset(8, 4, 8, 4), XpLook.White, Align.Left);
            Box body = area.CutTop(32).Inset(16);
            if (app == PcApp.Durak && view?.Cards != null) { Cards(canvas, faces, view.Cards, body); }
            else if (app == PcApp.Chess && view?.Chess?.Game != null) { Chess(canvas, faces, view.Chess, body); }
            else {
                string text = app == PcApp.Durak || app == PcApp.Chess ? "Ожидание игроков" : app == PcApp.Desktop ? "Рабочая станция" :
                    view?.State.Busy == true ? "Станция используется" : "Нет активных операций";
                Paint.Text(canvas, faces.Header, text, body, XpLook.Text, Align.Center);
            }
        }

        private static void Desktop(ICanvas canvas, XpFaces faces, in Box area) {
            canvas.Fill(area, XpLook.Desktop);
            for (int i = 0; i < desktopNames.Length; i++) {
                Box icon = new Box(area.X + 20, area.Y + 10 + i * 80, 64, 72);
                canvas.Image(XpLook.Image(desktopIcons[i]), new Box(icon.CenterX - 16, icon.Y, 32, 32), XpLook.White);
                Paint.Text(canvas, faces.Body, desktopNames[i], new Box(icon.X - 16, icon.Y + 36, 96, 30), XpLook.White, Align.Center);
            }
            Box taskbar = area.TakeBottom(XpLook.TaskbarH);
            canvas.Fill(taskbar, XpLook.Select);
            canvas.Fill(taskbar.TakeLeft(99), Rgba.Hex(0x247D2E));
            Paint.Text(canvas, faces.Bold, "Пуск", taskbar.TakeLeft(99), XpLook.White, Align.Center);
        }

        private static void Cards(ICanvas c, XpFaces f, DurakView view, in Box area) {
            c.Fill(area, Felt);
            float width = MathF.Min(86, area.W / Math.Max(4, view.Capacity));
            for (int i = 0; i < view.Capacity; i++) {
                DurakPlayerView player = view.Players[i]; if (!player.Present) { continue; }
                float x = area.X + (i + 0.5f) * area.W / view.Capacity;
                Paint.Text(c, f.Bold, player.Name, new Box(x - width, area.Y + 12, width * 2, 26), XpLook.White, Align.Center);
                if (player.Cards != 0) { c.Image("Content/UI/Durak/back.png", new Box(x - 24, area.Y + 46, 48, 65), XpLook.White); }
                Paint.Text(c, f.Bold, counts[Math.Min(36, (int)player.Cards)], new Box(x - 24, area.Y + 116, 48, 22), XpLook.White, Align.Center);
            }
            if (view.Phase == DurakPhase.Waiting) {
                Paint.Text(c, f.Header, "Подготовка к партии", area, XpLook.White, Align.Center); return;
            }
            float cardW = MathF.Min(88, (area.W - 40) / Math.Max(3, (int)view.TableCount)), cardH = cardW * 96 / 71;
            float left = area.CenterX - view.TableCount * (cardW + 16) / 2;
            for (int i = 0; i < view.TableCount; i++) {
                Box card = new Box(left + i * (cardW + 16), area.CenterY - cardH / 2, cardW, cardH);
                DurakApp.Card(c, view.Attacks[i], card);
                if (view.Defenses[i] != DurakCards.None) { DurakApp.Card(c, view.Defenses[i], card.Offset(14, 24)); }
            }
            DurakApp.Card(c, view.TrumpCard, new Box(area.X + 20, area.Bottom - 118, 59, 80));
            Paint.Text(c, f.Bold, counts[view.Remaining], new Box(area.X + 84, area.Bottom - 100, 48, 26), XpLook.White, Align.Left);
            string status = view.Phase == DurakPhase.Finished ? "Партия завершена" : view.Taking ? "Защищающийся берёт карты" : "Идёт партия";
            Paint.Text(c, f.Header, status, area.TakeBottom(30), XpLook.White, Align.Center);
        }

        private static void Chess(ICanvas c, XpFaces f, Chess.ChessView view, in Box area) {
            float size = MathF.Min(area.W - 160, area.H - 40), cell = size / 8;
            Box board = new Box(area.CenterX - size / 2, area.CenterY - size / 2, size, size);
            c.Image("Content/UI/Chess/board.png", board, XpLook.White);
            for (int rank = 0; rank < 8; rank++) {
                for (int file = 0; file < 8; file++) {
                    int square = rank * 8 + file;
                    Box box = new Box(board.X + file * cell, board.Y + (7 - rank) * cell, cell, cell);
                    int piece = view.Game.Squares[square];
                    if (piece != 0) { c.Image(ChessApp.Piece(piece), box.Inset(cell * 0.05f), XpLook.White); }
                }
            }
            Paint.Text(c, f.Bold, view.Names[1], new Box(board.X, board.Y - 24, board.W, 22), XpLook.Text, Align.Left);
            Paint.Text(c, f.Bold, view.Names[0], new Box(board.X, board.Bottom + 2, board.W, 22), XpLook.Text, Align.Left);
        }
    }
}
