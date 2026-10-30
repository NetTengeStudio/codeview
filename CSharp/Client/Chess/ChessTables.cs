using System;
using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ScpItdMod.Core;
using ScpItdMod.Pc;
using ScpItdMod.Xp;
using Vec2 = ScpItdMod.Core.Vec2;
using Plane = ScpItdMod.Core.Plane;

namespace ScpItdMod.Chess {
    public static class ChessTables {
        private sealed class View {
            public Item Item;
            public Rectangle Plane;
            public Vector2 PieceOrigin;
            public float PieceScale;
            public readonly DisplayView State = new DisplayView();
        }
        private static readonly Dictionary<ushort, View> views = new Dictionary<ushort, View>();
        private static readonly List<ushort> removed = new List<ushort>();
        private static ChessSession session;
        private static Item current;
        private static GUIFrame frame;
        private static GUICustomComponent board;
        private static GUITextBlock status, players;
        private static GUIButton ready, start, draw, decline, claim, resign, swap, white, black;
        private static readonly GUIButton[] promote = new GUIButton[4];
        private static readonly string[] promotionNames = { "Ферзь", "Ладья", "Слон", "Конь" };
        private static readonly byte[] promotions = { Board.Queen, Board.Rook, Board.Bishop, Board.Knight };
        private static uint revision;
        private static int selected = -1, promotion = -1;
        private static ulong legal;
        public static bool Open => current != null;

        public static void Attach(ChessSession connection) => session = connection;
        public static void Receive(Item item, ReadOnlySpan<byte> data) {
            if (!views.TryGetValue(item.ID, out View view) || !ReferenceEquals(view.Item, item)) {
                var config = item.Prefab.ConfigElement;
                view = new View { Item = item, Plane = config.GetAttributeRect("scpitdboard", Rectangle.Empty),
                    PieceOrigin = config.GetAttributeVector2("scpitdpieceorigin", Vector2.Zero), PieceScale = config.GetAttributeFloat("scpitdpiecescale", 0) }; views[item.ID] = view;
            }
            view.State.Apply(data);
        }
        public static void Control(Character actor) {
            if (!ReferenceEquals(actor, Character.Controlled)) { return; }
            Item item = actor.SelectedItem;
            if (ChessWorld.CanUse(actor, item)) {
                if (!ReferenceEquals(current, item)) { Begin(item); }
                actor.ClearInput(InputType.Use); actor.ClearInput(InputType.Shoot); actor.ClearInput(InputType.Aim);
            } else if (current != null) { Close(false); }
            if (current == null) { return; }
            session.Tick(Timing.TotalTime);
            Input(); Refresh();
        }
        public static void Gui() => frame?.AddToGUIUpdateList(order: 3);
        public static bool Escape() { if (current == null) { return false; } Close(true); return true; }
        public static void Release() { Close(false); views.Clear(); removed.Clear(); }
        public static void Sweep() {
            removed.Clear();
            foreach (var pair in views) { if (!ChessWorld.IsBoard(pair.Value.Item)) { removed.Add(pair.Key); } }
            for (int i = 0; i < removed.Count; i++) { views.Remove(removed[i]); }
        }
        private static RectTransform Rect(GUIComponent parent, int x, int y, int w, int h) => new RectTransform(new Point(w, h), parent.RectTransform) { AbsoluteOffset = new Point(x, y) };
        private static GUIButton Button(int x, int y, string label, Action action) => new GUIButton(Rect(frame, x, y, 196, 28), label) { OnClicked = (_, _) => { action(); return true; } };
        private static void Begin(Item item) {
            Close(false);
            if (GameMain.Client == null) { Rp.RpClient.Say("Шахматы работают на сервере.", Color.LightGray); return; }
            current = item; selected = promotion = -1; legal = 0; revision = uint.MaxValue;
            session.Begin(item.ID, Timing.TotalTime);
            int w = Math.Min(930, GameMain.GraphicsWidth - 32), h = Math.Min(720, GameMain.GraphicsHeight - 32), x = w - 214;
            frame = new GUIFrame(new RectTransform(new Point(w, h), GUI.Canvas, Anchor.Center));
            new GUITextBlock(Rect(frame, 18, 10, w - 80, 28), "Шахматы", font: GUIStyle.SubHeadingFont);
            new GUIButton(Rect(frame, w - 52, 10, 34, 28), "×") { OnClicked = (_, _) => { Close(true); return true; } };
            players = new GUITextBlock(Rect(frame, 18, 44, w - 36, 28), "", font: GUIStyle.SmallFont);
            board = new GUICustomComponent(Rect(frame, 18, 80, w - 250, h - 134), DrawBoard) { CanBeFocused = true, HoverCursor = CursorState.Hand };
            ready = Button(x, 82, "Готов", () => Command(ChessOp.Ready));
            start = Button(x, 118, "Начать партию", () => Command(ChessOp.Start));
            white = Button(x, 82, "Играть белыми", () => Command(ChessOp.Sit, 0));
            black = Button(x, 118, "Играть чёрными", () => Command(ChessOp.Sit, 1));
            swap = Button(x, 154, "Поменяться цветом", () => Command(ChessOp.Swap));
            draw = Button(x, 200, "Предложить ничью", () => Command(ChessOp.Draw));
            decline = Button(x, 236, "Отказаться от ничьей", () => Command(ChessOp.Decline));
            claim = Button(x, 272, "Заявить ничью", () => Command(ChessOp.Claim, move: new Move(255, 0)));
            resign = Button(x, 318, "Сдаться", () => Confirm("Сдаться в этой партии?", () => Command(ChessOp.Resign)));
            Button(x, h - 102, "Покинуть партию", () => Confirm("Покинуть партию? Во время игры это означает сдачу.", () => { Command(ChessOp.Leave); Close(true); }));
            new GUITextBlock(Rect(frame, x, h - 64, 196, 44), "ЛКМ — фигура и клетка.\nEsc — отойти от доски.", font: GUIStyle.SmallFont, wrap: true);
            for (int i = 0; i < 4; i++) { int index = i; promote[i] = Button(x, 380 + i * 32, promotionNames[i], () => Play(new Move(selected, promotion, promotions[index]))); promote[i].Visible = false; }
            status = new GUITextBlock(Rect(frame, 18, h - 38, w - 36, 28), "Подключение", font: GUIStyle.SmallFont);
        }
        private static void Close(bool deselect) {
            Item was = current; current = null;
            if (was != null) { session?.End(); }
            frame?.RectTransform.Parent?.GUIComponent?.RemoveChild(frame); frame = null; board = null; status = players = null;
            selected = promotion = -1; legal = 0;
            if (deselect && ReferenceEquals(Character.Controlled?.SelectedItem, was)) { Character.Controlled.EmulateInput(InputType.Deselect); Character.Controlled.SelectedItem = null; }
        }
        private static void Confirm(string text, Action action) {
            var box = new GUIMessageBox("Шахматы", text, buttons: new LocalizedString[] { "Да", "Нет" });
            box.Buttons[0].OnClicked = (_, _) => { action(); box.Close(); return true; };
            box.Buttons[1].OnClicked = (_, _) => { box.Close(); return true; };
        }
        private static void Command(ChessOp op, byte arg = 0, Move move = default) {
            if (current != null && session.Connected && !session.Busy) { session.Command(op, arg, move: move); }
        }
        private static Plane Fit(Rectangle area) {
            float size = MathF.Min(area.Width, area.Height);
            return new Plane(new Vec2(area.Center.X - size / 2, area.Center.Y - size / 2), new Vec2(size, 0), new Vec2(0, size));
        }
        private static void Input() {
            ChessView view = session.View;
            if (view?.Game == null || !session.Connected || session.Busy || promotion >= 0 || view.Me != view.Game.Turn || view.Game.Result != ChessResult.Playing ||
                GUI.InputBlockingMenuOpen || GUI.MouseOn != board || !PlayerInput.PrimaryMouseButtonClicked()) { return; }
            Vector2 mouse = PlayerInput.MousePosition; int square = ChessGrid.Square(Fit(board.Rect), new Vec2(mouse.X, mouse.Y), view.Me == 1);
            if (square < 0) { return; }
            if ((legal & (1UL << square)) != 0 && selected >= 0) {
                if (Math.Abs(view.Game.Squares[selected]) == Board.Pawn && (square >> 3) is 0 or 7) { promotion = square; revision = uint.MaxValue; }
                else { Play(new Move(selected, square)); }
                return;
            }
            int piece = view.Game.Squares[square]; selected = piece != 0 && (piece > 0 ? 0 : 1) == view.Me ? square : -1; legal = 0;
            if (selected >= 0) { Span<Move> moves = stackalloc Move[Board.MaxMoves]; int count = view.Game.Generate(moves); for (int i = 0; i < count; i++) { if (moves[i].From == selected) { legal |= 1UL << moves[i].To; } } }
        }
        private static void Play(Move move) {
            ChessView view = session.View; bool mayClaim = false;
            if (view != null) { for (int i = 0; i < view.Claims.Length; i++) { if (view.Claims[i].Equals(move)) { mayClaim = true; break; } } }
            if (mayClaim) {
                var box = new GUIMessageBox("Шахматы", "Этот ход даёт право заявить ничью.", buttons: new LocalizedString[] { "Сделать ход", "Заявить ничью", "Отмена" });
                box.Buttons[0].OnClicked = (_, _) => { Command(ChessOp.Move, move: move); box.Close(); return true; };
                box.Buttons[1].OnClicked = (_, _) => { Command(ChessOp.Claim, move: move); box.Close(); return true; };
                box.Buttons[2].OnClicked = (_, _) => { box.Close(); return true; };
            } else { Command(ChessOp.Move, move: move); }
            selected = promotion = -1; legal = 0; revision = uint.MaxValue;
        }
        private static void Refresh() {
            if (revision == session.Revision) { return; } revision = session.Revision;
            ChessView view = session.View; bool seated = view?.Me >= 0, playing = view?.Game?.Result == ChessResult.Playing, available = session.Connected && !session.Busy;
            ready.Visible = seated && !playing; ready.Enabled = available; ready.Selected = seated && view.Ready[view.Me];
            start.Visible = seated && !playing && view.Owner; start.Enabled = available && view != null && view.Ready[0] && view.Ready[1] && view.Online[0] && view.Online[1];
            white.Visible = view != null && !seated && !playing && !view.Present[0]; white.Enabled = available;
            black.Visible = view != null && !seated && !playing && !view.Present[1]; black.Enabled = available;
            swap.Visible = seated && !playing && view.Owner; swap.Enabled = available;
            draw.Visible = seated && playing; draw.Enabled = available; draw.Text = seated && view.Offer == 1 - view.Me ? "Принять ничью" : "Предложить ничью";
            decline.Visible = seated && playing && view.Offer == 1 - view.Me; decline.Enabled = available;
            claim.Visible = seated && playing && view.Claim && view.Me == view.Game.Turn; claim.Enabled = available;
            resign.Visible = seated && playing; resign.Enabled = available;
            if (view?.Game?.Turn != view?.Me || !playing) { selected = promotion = -1; legal = 0; }
            for (int i = 0; i < 4; i++) { promote[i].Visible = promotion >= 0; promote[i].Enabled = available; }
            players.Text = view == null ? "" : "Белые: " + (view.Present[0] ? view.Names[0] : "место свободно") + "    Чёрные: " + (view.Present[1] ? view.Names[1] : "место свободно");
            status.Text = session.Error != ChessError.Ok ? ChessWire.Explain(session.Error) : !session.Connected ? "Подключение к серверу" : session.Busy ? "Ожидание ответа" :
                view?.Game == null ? "Займите место и подтвердите готовность" : !playing ? "Партия завершена" : view.Game.Turn == 0 ? "Ход белых" : "Ход чёрных";
        }
        private static void DrawBoard(SpriteBatch batch, GUICustomComponent component) {
            ChessView view = session.View; Plane plane = Fit(component.Rect); float size = plane.X.Length();
            Texture2D texture = TextureStore.File("Content/UI/Chess/board.png");
            Vector2 top = new Vector2(plane.Origin.X, plane.Origin.Y);
            if (texture != null) { batch.Draw(texture, new Rectangle((int)top.X, (int)top.Y, (int)size, (int)size), new Rectangle(0, 0, 512, 512), Color.White); }
            if (view?.Game == null) { return; }
            bool flipped = view.Me == 1;
            for (int square = 0; square < 64; square++) {
                Vec2 at = ChessGrid.Cell(plane, square, flipped); float cell = size / 8;
                var rect = new Rectangle((int)(at.X - cell / 2), (int)(at.Y - cell / 2), (int)cell, (int)cell);
                if (square == selected) { GUI.DrawRectangle(batch, rect, Color.Goldenrod * 0.45f, true); }
                if ((legal & (1UL << square)) != 0) { GUI.DrawRectangle(batch, rect, Color.LightGreen, false, thickness: 2); }
                if (square == view.Game.Last.From || square == view.Game.Last.To) { GUI.DrawRectangle(batch, rect, Color.Goldenrod, false, thickness: 1); }
                int piece = view.Game.Squares[square]; if (piece == 0) { continue; }
                Texture2D art = TextureStore.File(ChessApp.Piece(piece)); if (art != null) { batch.Draw(art, rect, Color.White); }
            }
        }
        public static void DrawItem(Item item, SpriteBatch batch, float? overrideDepth) {
            if (!ChessWorld.IsBoard(item) || item.IsHidden || item.ParentInventory != null || !views.TryGetValue(item.ID, out View view) || view.State.Chess?.Game == null) { return; }
            Sprite sprite = item.Prefab.Sprite; Vector2 anchor = ItemPose.DrawPosition(item); float angle = ItemPose.DrawAngle(item), cosine = MathF.Cos(angle), sine = MathF.Sin(angle);
            Rectangle rect = view.Plane;
            if (rect.Width <= 0 || rect.Height <= 0 || view.PieceScale <= 0) { return; }
            float size = rect.Width * item.Scale, height = rect.Height * item.Scale;
            Vec2 center = new Vec2(anchor.X, -anchor.Y), x = new Vec2(cosine, sine), y = new Vec2(-sine, cosine);
            Vec2 origin = center + x * (rect.X - sprite.Origin.X) * item.Scale + y * (rect.Y - sprite.Origin.Y) * item.Scale;
            Plane plane = new Plane(origin, x * size, y * height);
            float depth = MathF.Max(0, (overrideDepth ?? item.GetDrawDepth()) - 0.0001f);
            ReadOnlySpan<sbyte> squares = view.State.Chess.Game.Squares;
            for (int square = 63; square >= 0; square--) {
                int piece = squares[square]; if (piece == 0) { continue; }
                Texture2D art = TextureStore.File(ChessApp.WorldPiece(piece)); if (art == null) { continue; }
                Vec2 at = ChessGrid.Cell(plane, square);
                batch.Draw(art, new Vector2(at.X, at.Y), null, Color.White, angle, view.PieceOrigin, view.PieceScale * item.Scale, SpriteEffects.None, depth - (7 - (square >> 3)) * 0.000001f);
            }
        }
    }
}
