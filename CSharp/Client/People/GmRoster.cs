using System;
using Barotrauma;
using ScpItdMod.Core;
using ScpItdMod.Ui;

namespace ScpItdMod.People {
    public sealed class GmRoster : RetroWindow {
        private const int RefreshId = 10;
        private const int ListId = 20;
        private const int RowId = 1000;
        private const double AutoRefresh = 5.0;

        private static GmRoster instance;

        private float scroll;
        private float detailScroll;
        private int picked = -1;
        private int seen = -1;
        private double requested;
        private bool waiting;

        public override string Title => "ГМ: ПЕРСОНАЖИ РАУНДА";

        public static void Attach() {
            ModCommands.Register("scpitd_roster", "gm: opens the roster of this round with real names, descriptions and notes", _ => Toggle());
            ModCommands.Register("scpitd_who", "lists the real names this client knows (a gm knows everyone)", _ => Who());
        }

        public static void Toggle() {
            instance ??= new GmRoster();
            if (instance.IsOpen) {
                instance.Close();
                return;
            }
            if (GameMain.Client == null) {
                ModLog.Message("the roster works on a server");
                return;
            }
            instance.Open();
        }

        protected override void Opened() {
            Rect = new Box(GameMain.GraphicsWidth * 0.18f, GameMain.GraphicsHeight * 0.14f, GameMain.GraphicsWidth * 0.64f, GameMain.GraphicsHeight * 0.66f);
            Request();
        }

        private void Request() {
            PeopleClient.RequestRoster();
            requested = Timing.TotalTime;
            waiting = true;
        }

        public override void Tick(float dt) {
            if (seen != PeopleClient.RosterRevision) {
                seen = PeopleClient.RosterRevision;
                waiting = false;
                if (picked >= PeopleClient.Roster.Count) { picked = PeopleClient.Roster.Count - 1; }
            }
            if (Timing.TotalTime - requested > AutoRefresh) { Request(); }
        }

        public override void Body(ICanvas c, UiState ui, RetroFaces f, Box body) {
            float row = Retro.Px(c, Retro.RowPx);
            float pad = Retro.Px(c, Retro.PadPx);
            var top = body.TakeTop(Retro.Px(c, Retro.ButtonPx));
            string head = PeopleClient.RosterDenied ? "Нет прав ГМ: сервер отказал." :
                waiting && PeopleClient.Roster.Count == 0 ? "Запрос к серверу…" : $"{PeopleClient.Roster.Count} персонажей";
            Paint.Clipped(c, f.Body, head, top.CutRight(Retro.Px(c, 130.0f)), PeopleClient.RosterDenied ? Retro.Bad : Retro.Ink, Align.Left);
            if (Retro.Button(c, ui, f, RefreshId, top.TakeRight(Retro.Px(c, 120.0f)), "Обновить", !waiting, 1.0f)) { Request(); }
            body = body.CutTop(top.H + pad);

            var listBox = body.TakeLeft(MathF.Round(body.W * 0.46f));
            var detail = body.CutLeft(listBox.W + pad);
            c.Fill(listBox, Retro.PlateSolid);
            Paint.Frame(c, listBox, Retro.Px(c, 1.0f), Retro.Rule);
            var cols = listBox.TakeTop(row);
            Paint.Text(c, f.Small, "Позывной / имя", cols.Inset(pad, 0, pad, 0), Retro.Muted, Align.Left);
            Paint.Text(c, f.Small, "Аккаунт", cols.Inset(pad, 0, pad, 0), Retro.Muted, Align.Right);
            Box rows = Retro.List(c, ui, ListId, listBox.CutTop(row), PeopleClient.Roster.Count, row, ref scroll, out int first, out int last);
            c.PushClip(rows);
            for (int i = first; i <= last; i++) {
                RosterEntry e = PeopleClient.Roster[i];
                string label = e.Real.Length > 0 && e.Real != e.Callsign ? e.Callsign + " — " + e.Real : e.Callsign;
                if (Retro.Row(c, ui, f, RowId + i, Retro.RowBox(rows, i, row, scroll), label, e.Account, i == picked, true, 1.0f, e.Alive ? (Rgba?)null : Retro.Muted)) {
                    picked = i;
                    detailScroll = 0.0f;
                }
            }
            c.PopClip();

            c.Fill(detail, Retro.PlateSolid);
            Paint.Frame(c, detail, Retro.Px(c, 1.0f), Retro.Rule);
            var inner = detail.Inset(pad);
            if (picked < 0 || picked >= PeopleClient.Roster.Count) {
                Paint.Text(c, f.Body, "Выберите персонажа слева.", inner.TakeTop(row), Retro.Muted, Align.Left);
                return;
            }
            RosterEntry s = PeopleClient.Roster[picked];
            float y = 0.0f;
            void Field(string k, string v) {
                var line = new Box(inner.X, inner.Y + y, inner.W, row);
                Paint.Text(c, f.Small, k, line, Retro.Muted, Align.Left);
                Paint.Clipped(c, f.Body, v, line.CutLeft(Retro.Px(c, 110.0f)), Retro.Bright, Align.Left);
                y += row;
            }
            Field("Позывной", s.Callsign);
            Field("Имя", s.Real.Length == 0 ? "—" : s.Real);
            Field("Роль", s.Job.Length == 0 ? "—" : s.Job);
            Field("Аккаунт", s.Account);
            Field("Профиль", $"#{s.Profile}, версия при появлении {s.Version}");
            Field("Состояние", s.Alive ? "жив" : "мёртв или удалён");
            y += pad;
            Paint.Text(c, f.Small, "Как выглядит (видят все)", new Box(inner.X, inner.Y + y, inner.W, row), Retro.Muted, Align.Left);
            y += row;
            var text = new Box(inner.X, inner.Y + y, inner.W, inner.H - y);
            c.PushClip(text);
            float used = Retro.Paragraph(c, f.Body, s.Look.Length == 0 ? "(не заполнено)" : s.Look, text, Retro.Ink, detailScroll);
            var notesAt = new Box(text.X, text.Y + used + pad - detailScroll, text.W, row);
            Paint.Text(c, f.Small, "Личные заметки (игрок и ГМ)", notesAt, Retro.Muted, Align.Left);
            float notes = Retro.Paragraph(c, f.Body, s.Notes.Length == 0 ? "(нет)" : s.Notes,
                new Box(text.X, notesAt.Bottom, text.W, text.Bottom - notesAt.Bottom + detailScroll), Retro.Ink);
            c.PopClip();
            float total = used + pad + row + notes;
            if (ui.Over(ListId + 2, text) && ui.In.Wheel != 0.0f) { detailScroll -= ui.In.Wheel * 0.25f; }
            detailScroll = Math.Clamp(detailScroll, 0.0f, MathF.Max(0.0f, total - text.H));
        }

        private static void Who() {
            if (PeopleClient.Names.Count == 0) {
                ModLog.Message("no known names");
                return;
            }
            var text = new System.Text.StringBuilder();
            foreach (var pair in PeopleClient.Names) {
                string shown = Entity.FindEntityByID(pair.Key) is Character ch ? ch.Name : "#" + pair.Key;
                text.Append(shown).Append(" = ").Append(pair.Value).Append('\n');
            }
            ModLog.Message(text.ToString().TrimEnd());
        }
    }
}
