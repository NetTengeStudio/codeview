using System;
using System.Collections.Generic;
using ScpItdMod.Rec;
using ScpItdMod.Ui;

namespace ScpItdMod.Xp {
    public interface ICamFeed {
        IReadOnlyList<CamInfo> Cameras { get; }
        CamInfo Selected { get; }
        Playback View { get; }
        bool Listed { get; }
        CamDeny Deny { get; }
        bool InGap { get; }
        bool VoiceAllowed { get; }
        void AllowVoice(bool allowed);
        double ServerNow { get; }
        void Begin();
        void End();
        void Select(ushort id);
        void Control(ushort id, bool record);
        void Picture(ICanvas c, in Box viewport, CamInfo cam);
        string Caption(double position);
    }

    public sealed class Cameras : IXpApp, IXpSized, IXpSessionLifecycle {
        private static readonly Rgba Rec = Rgba.Hex(0xE02020);
        private static readonly Rgba Osd = Rgba.Hex(0xE8F0E8);
        private float listScroll;
        // Memoised text: rebuilt on value change only, so per-frame painting does not allocate strings.
        private (CamDeny, bool, bool, int, bool, ViewState, bool, double, double, float) statusKey;
        private bool statusCached;
        private string statusText = "", statusArchive = "";
        // Keyed by value, so one shared entry stays correct for every open camera app.
        private static float speedLabelOf = float.NaN;
        private static string speedLabel = "";
        private bool started;
        private bool seeking;
        private bool restoreFeed;
        private ushort resumeCamera;
        private uint resumeEpoch;
        private double resumePosition;
        private int resumeSpeed;
        private bool resumeLive;
        private bool resumePlaying;

        public string Title => "Камеры наблюдения";

        public string Icon => "camera16";

        public void Size(XpWindow w) {
            w.Rect = new Box(0, 0, 820, 560);
            w.MinW = 218 + 336 + 16 + XpLook.Border * 2;
            w.MinH = XpLook.TitleH + XpLook.Border + XpLook.StatusH + 16 + 240;
        }

        public static string Deny(CamDeny d) => d switch {
            CamDeny.None => "",
            CamDeny.NoTerminal => "Станция не подключена к системе наблюдения.",
            CamDeny.TooFar => "Вы отошли от станции.",
            CamDeny.NoPower => "Нет питания.",
            CamDeny.NoRights => "Недостаточно допуска для этой камеры.",
            CamDeny.NoCamera => "Камера не найдена.",
            CamDeny.Busy => "Система занята.",
            _ => "Недопустимый запрос."
        };

        public static readonly string[] HowTo = {
            "Нет доступных камер.",
            "Проверьте подключение",
            "и свой уровень допуска."
        };

        public static string Clock(double seconds) {
            if (!double.IsFinite(seconds) || seconds < 0) { return "--:--:--"; }
            int s = (int)seconds;
            return $"{s / 3600:00}:{s / 60 % 60:00}:{s % 60:00}";
        }

        private static string StateOf(CamInfo cam) => cam.Lost ? "Потеряна" : !cam.Powered ? "Нет питания" : cam.Recording ? "Запись" : "Без записи";

        public void Paint(ICanvas c, UiState ui, XpFaces f, Box client, XpShell shell, XpWindow self) {
            if (!(shell.Cams is ICamFeed feed)) {
                c.Fill(client, XpLook.Surface);
                XpPaint.Label(c, f, "Система наблюдения недоступна на этой станции.", client.Inset(12).TakeTop(16));
                return;
            }
            if (!started) {
                started = true;
                feed.Begin();
            }
            if (restoreFeed && feed.Listed) { RestoreFeed(feed); }
            c.Fill(client, XpLook.Surface);
            var status = client.TakeBottom(XpLook.StatusH);
            var body = client.CutBottom(XpLook.StatusH).Inset(8);
            var left = body.TakeLeft(210);
            var right = body.CutLeft(218);
            CameraList(c, ui, f, left, feed);
            Viewer(c, ui, f, right, feed, shell);
            CamInfo sel = feed.Selected;
            // The status line is rebuilt only when a value it shows changes; otherwise each frame would allocate new strings.
            var key = (feed.Deny, feed.Listed, sel != null, feed.Cameras.Count, feed.View.Live, feed.View.State(feed.InGap), sel != null && feed.View.HasRange, feed.View.Oldest, feed.View.Newest, feed.View.Speed);
            if (!statusCached || key != statusKey) {
                statusKey = key; statusCached = true;
                statusText = feed.Deny != CamDeny.None ? Deny(feed.Deny) : !feed.Listed ? "Подключение к системе наблюдения..." : sel == null ? $"Камер: {feed.Cameras.Count}" : Describe(feed);
                statusArchive = sel != null && feed.View.HasRange ? $"Архив: {Clock(feed.View.Oldest)} – {Clock(feed.View.Newest)}" : "";
            }
            XpPaint.Status(c, f, status, statusText, statusArchive);
        }

        private static string Describe(ICamFeed feed) => feed.View.Live ? "Прямой эфир" : feed.View.State(feed.InGap) switch {
            ViewState.Loading => "Загрузка архива...",
            ViewState.Gap => "В этот промежуток камера не записывала.",
            ViewState.Playing => $"Воспроизведение x{feed.View.Speed:0.##}",
            ViewState.End => "Конец записи.",
            ViewState.Paused => "Пауза.",
            _ => ""
        };

        private void CameraList(ICanvas c, UiState ui, XpFaces f, in Box area, ICamFeed feed) {
            XpPaint.Group(c, f, area, "Камеры");
            var list = new Box(area.X + 8, area.Y + 18, area.W - 16, area.H - 84);
            XpPaint.Sunken(c, list, XpLook.White);
            Box rows = XpPaint.Scroll(c, ui, 300, list.Inset(1), feed.Cameras.Count * XpLook.RowH, ref listScroll);
            c.PushClip(rows);
            CamInfo sel = feed.Selected;
            for (int i = 0; i < feed.Cameras.Count; i++) {
                CamInfo cam = feed.Cameras[i];
                var row = new Box(rows.X, rows.Y + i * XpLook.RowH - listScroll, rows.W, XpLook.RowH);
                if (row.Bottom <= rows.Y || row.Y >= rows.Bottom) { continue; }
                if (XpPaint.Row(c, ui, f, 400 + i, row, "camera16", cam.Name, StateOf(cam), sel != null && sel.Item == cam.Item, !cam.Lost, rows)) { feed.Select(cam.Item); }
            }
            c.PopClip();
            if (feed.Listed && feed.Cameras.Count == 0 && feed.Deny == CamDeny.None) {
                float y = rows.Y + 6;
                foreach (string line in HowTo) {
                    Ui.Paint.Clipped(c, f.Body, line, new Box(rows.X + 6, y, rows.W - 12, 14), XpLook.Shadow, Align.Left);
                    y += 14;
                }
            }
            var consent = new Box(area.X + 8, area.Bottom - 32, area.W - 16, XpLook.ButtonH);
            if (XpPaint.Button(c, ui, f, 61, consent, feed.VoiceAllowed ? "Запись моего голоса: да" : "Запись моего голоса: нет", true)) { feed.AllowVoice(!feed.VoiceAllowed); }
            if (sel == null) { return; }
            var toggle = new Box(area.X + 8, area.Bottom - 62, area.W - 16, XpLook.ButtonH);
            if (XpPaint.Button(c, ui, f, 60, toggle, sel.Recording ? "Остановить запись" : "Начать запись", sel.Control && !sel.Lost)) { feed.Control(sel.Item, !sel.Recording); }
        }

        private void Viewer(ICanvas c, UiState ui, XpFaces f, in Box area, ICamFeed feed, XpShell shell) {
            CamInfo cam = feed.Selected;
            float vw = area.W;
            float vh = MathF.Min(area.H - (34 + XpLook.ButtonH * 2 + 4), MathF.Round(vw * 0.75f));
            var screen = new Box(area.X, area.Y, vw, vh);
            c.Fill(screen, XpLook.Black);
            Ui.Paint.Frame(c, screen, 1, XpLook.Shadow);
            Box view = screen.Inset(2);
            if (cam == null) {
                Ui.Paint.Text(c, f.Body, feed.Listed ? "Выберите камеру в списке." : "Нет сигнала", view, Osd, Align.Center);
                return;
            }
            Playback pb = feed.View;
            ViewState state = pb.State(feed.InGap);
            if (!pb.Live && (state == ViewState.Gap || state == ViewState.Idle)) {
                Noise(c, view, shell.Now);
                Ui.Paint.Text(c, f.Header, state == ViewState.Gap ? "НЕТ ЗАПИСИ" : "НЕТ СИГНАЛА", view, Osd, Align.Center);
            } else {
                feed.Picture(c, view, cam);
                if (!pb.Live && state == ViewState.Loading) { Ui.Paint.Text(c, f.Header, "ЗАГРУЗКА", view, Osd, Align.Center); }
            }
            Ui.Paint.Text(c, f.Body, cam.Name, new Box(view.X + 8, view.Y + 6, view.W - 16, 14), Osd, Align.Left);
            bool live = cam.Recording && cam.Powered && !cam.Lost && pb.Live;
            if (live && ((int)(shell.Now * 2) & 1) == 0) {
                c.Fill(new Box(view.Right - 52, view.Y + 9, 8, 8), Rec);
                Ui.Paint.Text(c, f.Body, "REC", new Box(view.Right - 40, view.Y + 6, 34, 14), Rec, Align.Left);
            }
            Ui.Paint.Text(c, f.Body, "Т+" + Clock(pb.Live ? feed.ServerNow : pb.Position), new Box(view.X + 8, view.Bottom - 20, 160, 14), Osd, Align.Left);
            string caption = state == ViewState.Playing || state == ViewState.Paused ? feed.Caption(pb.Position) : null;
            if (!string.IsNullOrEmpty(caption)) {
                float w = MathF.Min(view.W - 40, Ui.Paint.Measure(f.Body, caption) + 16);
                var cb = new Box(MathF.Round(view.CenterX - w * 0.5f), view.Bottom - 46, w, 18);
                c.Fill(cb, new Rgba(0, 0, 0, 170));
                Ui.Paint.Clipped(c, f.Body, caption, cb.Inset(8, 0, 8, 0), XpLook.White, Align.Center);
            }
            Timeline(c, ui, f, new Box(area.X, screen.Bottom + 6, area.W, 22), pb);
            Controls(c, ui, f, new Box(area.X, screen.Bottom + 34, area.W, XpLook.ButtonH), pb, cam);
        }

        private static void Noise(ICanvas c, in Box b, double now) {
            uint seed = (uint)(now * 24) * 2654435761u;
            for (int y = 0; y < (int)b.H; y += 4) {
                seed = seed * 1664525u + 1013904223u;
                int g = 24 + (int)(seed >> 26);
                c.Fill(new Box(b.X, b.Y + y, b.W, 4), new Rgba(g, g, g));
            }
        }

        private void Timeline(ICanvas c, UiState ui, XpFaces f, in Box b, Playback pb) {
            XpPaint.Sunken(c, b, Rgba.Hex(0x1E2A1E));
            if (!pb.HasRange) { return; }
            double span = Math.Max(1.0, pb.Newest - pb.Oldest);
            Box inner = b.Inset(2);
            IReadOnlyList<(double From, double To)> known = pb.Known;
            for (int i = 0; i < known.Count; i++) {
                float x0 = inner.X + (float)((Math.Max(known[i].From, pb.Oldest) - pb.Oldest) / span) * inner.W;
                float x1 = inner.X + (float)((Math.Min(known[i].To, pb.Newest) - pb.Oldest) / span) * inner.W;
                if (x1 > x0) { c.Fill(new Box(x0, inner.Y + inner.H - 4, x1 - x0, 4), Rgba.Hex(0x4FAF4F)); }
            }
            for (int m = 0; m <= 10; m++) {
                float x = MathF.Round(inner.X + inner.W * m / 10f);
                c.Fill(new Box(x, inner.Y, 1, 5), Rgba.Hex(0x6C8A6C));
            }
            float px = inner.X + (float)((pb.Position - pb.Oldest) / span) * inner.W;
            c.Fill(new Box(MathF.Round(px) - 1, inner.Y, 3, inner.H), XpLook.White);
            if (ui.Over(70, b) && ui.In.Pressed) {
                seeking = true;
                ui.Active = 70;
            }
            if (seeking && ui.In.Down) {
                float k = Math.Clamp((ui.In.X - inner.X) / MathF.Max(1, inner.W), 0, 1);
                pb.Seek(pb.Oldest + k * span);
            }
            if (!ui.In.Down) { seeking = false; }
        }

        private static void Controls(ICanvas c, UiState ui, XpFaces f, in Box row, Playback pb, CamInfo cam) {
            float x = row.X;
            float w = (row.W - 16) / 6;
            if (XpPaint.Button(c, ui, f, 80, new Box(x, row.Y, w, row.H), "−30 с", pb.HasRange)) { pb.Step(-30); }
            x += w + 4;
            if (XpPaint.Button(c, ui, f, 81, new Box(x, row.Y, w, row.H), "−5 с", pb.HasRange)) { pb.Step(-5); }
            x += w + 4;
            if (XpPaint.Button(c, ui, f, 82, new Box(x, row.Y, w * 2, row.H), pb.Playing ? "Пауза" : "Воспроизвести", pb.HasRange, true)) { pb.Toggle(); }
            x += w * 2 + 4;
            if (XpPaint.Button(c, ui, f, 83, new Box(x, row.Y, w, row.H), "+5 с", pb.HasRange)) { pb.Step(5); }
            x += w + 4;
            if (XpPaint.Button(c, ui, f, 84, new Box(x, row.Y, w, row.H), "+30 с", pb.HasRange)) { pb.Step(30); }
            float y = row.Bottom + 4;
            if (XpPaint.Button(c, ui, f, 85, new Box(row.X, y, 24, row.H), "−", pb.HasRange)) { pb.Slower(); }
            if (pb.Speed != speedLabelOf) { speedLabelOf = pb.Speed; speedLabel = $"x{pb.Speed:0.##}"; }
            Ui.Paint.Text(c, f.Body, speedLabel, new Box(row.X + 26, y, 40, row.H), XpLook.Text, Align.Center);
            if (XpPaint.Button(c, ui, f, 86, new Box(row.X + 68, y, 24, row.H), "+", pb.HasRange)) { pb.Faster(); }
            if (XpPaint.Button(c, ui, f, 87, new Box(row.Right - 80, y, 80, row.H), "В эфир", pb.HasRange && !cam.Lost)) {
                pb.GoLive();
            }
        }

        public bool WantsClose(XpShell shell, XpWindow self) => true;

        public void OnSessionSuspending(XpShell shell) {
            started = false;
            if (shell.Cams is ICamFeed feed) {
                CamInfo selected = feed.Selected;
                Playback view = feed.View;
                resumeCamera = selected?.Item ?? 0;
                resumeEpoch = selected?.Epoch ?? 0;
                resumePosition = view.Position;
                resumeSpeed = view.SpeedIndex;
                resumeLive = view.Live;
                resumePlaying = view.Playing;
                restoreFeed = resumeCamera != 0;
                feed.End();
            }
        }

        public void OnSessionResumed(XpShell shell) => started = false;

        private void RestoreFeed(ICamFeed feed) {
            CamInfo camera = null;
            for (int i = 0; i < feed.Cameras.Count; i++) {
                if (feed.Cameras[i].Item == resumeCamera) { camera = feed.Cameras[i]; break; }
            }
            restoreFeed = false;
            if (camera == null) { return; }
            feed.Select(resumeCamera);
            Playback view = feed.View;
            if (camera.Epoch != resumeEpoch) { return; }
            if (resumeLive) { view.GoLive(); }
            else { view.Seek(resumePosition); }
            while (view.SpeedIndex < resumeSpeed) { view.Faster(); }
            while (view.SpeedIndex > resumeSpeed) { view.Slower(); }
            view.SetPlaying(resumePlaying);
        }

        public void Closed(XpShell shell) {
            if (shell.Cams is ICamFeed feed) { feed.End(); }
        }
    }
}
