using System;
using ScpItdMod.Ui;
using ScpItdMod.Weather;
using ScpItdMod.Xp;
using Draw = ScpItdMod.Ui.Paint;

namespace ScpItdMod.Meteo {
    public sealed partial class MeteoScreen {
        private string forecastRegime = "НЕТ ОЦЕНКИ", forecastRange = "—", forecastCenter = "—", observedRegime = "—", forecastBasis = "Нужен непрерывный ряд", currentRegimeCaption = "Сейчас: нет оценки";
        private string chillCaption = "Ветро-холод: нет оценки", hazardCaption = "Ожидание наблюдений", guidance = "Проверьте связь и питание приборов.";
        private readonly string[] journalTimes = new string[MeteoWire.MaxEntries], journalStations = new string[MeteoWire.MaxEntries];
        private uint seenJournal = uint.MaxValue;

        private void CacheOperationText() {
            MeteoForecast forecast = O.Forecast;
            forecastRegime = forecast.Valid ? MeteoText.Regime(forecast.Likely).ToUpperInvariant() : "НЕТ ОЦЕНКИ";
            observedRegime = forecast.Valid ? MeteoText.Regime(forecast.Observed) : "—";
            currentRegimeCaption = "Сейчас: " + observedRegime;
            forecastRange = forecast.Valid ? MeteoText.Number(forecast.EtaLow, "0") + "–" + MeteoText.Number(forecast.EtaHigh, "0") + " с" : "—";
            forecastCenter = forecast.Valid ? MeteoText.Number(forecast.EtaMid, "0") + " с" : "—";
            forecastBasis = !forecast.Valid ? "Нужен непрерывный ряд" : forecast.Precursor ? "Быстрое падение давления" : "Ряд и климатическая модель";
            chillCaption = O.HasLatest && O.Latest.WindValid ? "Ветро-холод: " + MeteoText.Signed(O.Latest.WindChill, "0") + " °C" : "Ветро-холод: нет оценки";
            hazardCaption = !O.HasLatest ? "Нет свежего наблюдения" : O.Latest.Visibility < 50 ? "Видимость ниже 50 м" : O.Latest.Visibility < 1000 ? "Видимость ограничена" : "Видимость в пределах нормы";
            guidance = !O.HasLatest ? "Проверьте связь и питание." : forecast.Valid && forecast.Alert >= MeteoAlert.Warning ? "Наружные работы прекратить.\nПодготовить сводку для базы." : O.Latest.Ice >= MeteoStationModel.FoulIce ? "Проверить обогрев и очистить приборы." : "Продолжать наблюдение.\nЗаписывать изменения обстановки.";
        }
        private void CacheJournal() {
            seenJournal = session.JournalRevision;
            for (int i = 0; i < session.Journal.Count; i++) {
                MeteoEntry entry = session.Journal[i]; journalTimes[i] = Clock(entry.Time); journalStations[i] = StationName(entry.Station); journalBlocks[i].Set(entry.Text);
            }
        }

        private void Operations(ICanvas c, XpFaces f, in Box box, double now) {
            MeteoForecast forecast = O.Forecast;
            bool valid = HasStation && forecast.Valid && !SourceStale;
            bool warning = valid && forecast.Alert >= MeteoAlert.Warning;
            string heading = warning ? "ШТОРМОВОЕ ПРЕДУПРЕЖДЕНИЕ" : "ОПЕРАТИВНЫЙ ПРОГНОЗ";
            Box inner = Panel(c, f, box, heading, warning);
            Rgba tone = warning ? MeteoLook.Alarm : MeteoLook.Ink;
            Draw.Text(c, f.Header, valid ? forecastRegime : "НЕТ СВЕЖЕЙ ОЦЕНКИ", new Box(inner.X + 4, inner.Y, inner.W - 8, 25), tone, Align.Left);
            Draw.Clipped(c, f.Body, Historical ? "По последнему приёму / расчёт" : "Расчётная оценка", new Box(inner.X + 4, inner.Y + 26, inner.W - 8, 16), MeteoLook.Muted, Align.Left);
            Draw.Text(c, f.Bold, valid ? forecastRange : "—", new Box(inner.X + 4, inner.Y + 44, inner.W - 8, 19), tone, Align.Left);
            Box ruler = new Box(inner.X + 8, inner.Y + 71, inner.W - 16, 9);
            c.Fill(ruler, MeteoLook.Plot); Draw.Frame(c, ruler, 1, MeteoLook.Axis);
            if (valid) {
                float horizon = MathF.Max(60, forecast.EtaHigh * 1.12f);
                float low = Math.Clamp(forecast.EtaLow / horizon, 0, 1), high = Math.Clamp(forecast.EtaHigh / horizon, low, 1);
                c.Fill(new Box(ruler.X + low * ruler.W, ruler.Y + 1, (high - low) * ruler.W, ruler.H - 2), Rgba.Mix(MeteoLook.AlarmFill, MeteoLook.Alarm, Math.Clamp(alarmMark.Value, 0, 1) * .2f));
                float marker = ruler.X + Math.Clamp(eta.Value / horizon, low, high) * ruler.W;
                c.Fill(new Box(MathF.Round(marker) - 1, ruler.Y - 3, 3, ruler.H + 6), tone);
            }
            Draw.Text(c, f.Body, "0 с", new Box(ruler.X, ruler.Bottom + 4, 35, 14), MeteoLook.Muted, Align.Left);
            Draw.Text(c, f.Bold, valid ? forecastCenter : "—", new Box(ruler.X + 40, ruler.Bottom + 4, ruler.W - 80, 14), tone, Align.Center);
            Draw.Clipped(c, f.Body, valid ? currentRegimeCaption : "Сейчас: нет свежей оценки", new Box(inner.X + 4, inner.Y + 103, inner.W - 8, 17), MeteoLook.Ink, Align.Left);
            Draw.Clipped(c, f.Body, valid ? forecastBasis : "Проверьте давность и непрерывность", new Box(inner.X + 4, inner.Y + 122, inner.W - 8, 16), MeteoLook.Muted, Align.Left);
            var hazard = new Box(inner.X + 2, inner.Y + 145, inner.W - 4, 37);
            c.Fill(hazard, warning ? MeteoLook.AlarmFill : MeteoLook.Plot);
            Draw.Clipped(c, f.Bold, !SourceStale ? chillCaption : "Сведения устарели", new Box(hazard.X + 5, hazard.Y + 2, hazard.W - 10, 16), MeteoLook.Ink, Align.Left);
            Draw.Clipped(c, f.Bold, !SourceStale ? hazardCaption : "Проверьте линию и давность", new Box(hazard.X + 5, hazard.Y + 18, hazard.W - 10, 16), !SourceStale && O.Latest.Visibility < 50 ? MeteoLook.Alarm : MeteoLook.Ink, Align.Left);
            float buttonY = inner.Bottom - 38;
            if (Button(c, f, 470, new Box(inner.X + 1, buttonY, 112, 22), "Подготовить сводку", HasStation && !SourceStale && !session.ActionPending)) {
                if (session.Bulletin(station)) { SetStatus("Сводка запрошена. Ожидание подтверждения сохранения в «Метео».", MeteoLook.Ink); }
            }
            if (Button(c, f, 471, new Box(inner.X + 117, buttonY, inner.W - 118, 22), "Оповестить базу…", HasStation && !session.ActionPending)) {
                if (alertField.Text.Length == 0) { alertField.Load(Suggest(forecast)); }
                alertLevel = Math.Clamp((int)forecast.Alert, 1, 3); SwitchTab(MeteoTab.Forecast);
            }
            string action = session.ActionPending ? "Ожидание подтверждения…" : session.ActionState == MeteoActionState.Unconfirmed ? "Подтверждение не получено" :
                session.ActionState == MeteoActionState.Rejected ? MeteoSession.Explain(session.LastAck) :
                O.LastAlert > 0 && double.IsFinite(O.LastAlert) ? "Оповещение передано" : "Оповещение не отправлено";
            Draw.Clipped(c, f.Body, action, new Box(inner.X + 2, buttonY + 24, inner.W - 4, 16), session.ActionPending ? MeteoLook.Ink : MeteoLook.Alarm, Align.Right);
        }

        private void WatchJournal(ICanvas c, XpFaces f, in Box box, double now) {
            Box inner = Panel(c, f, box, "ЖУРНАЛ НАБЛЮДЕНИЙ И СОБЫТИЙ");
            Box list = new Box(inner.X, inner.Y, inner.W - 286, inner.H);
            Box memo = new Box(list.Right + 7, inner.Y, 279, inner.H);
            c.Fill(list.TakeTop(15), MeteoLook.Rule);
            Draw.Text(c, f.Body, "Время", new Box(list.X + 2, list.Y, 54, 15), MeteoLook.Ink, Align.Left);
            Draw.Text(c, f.Body, "Станция", new Box(list.X + 61, list.Y, 113, 15), MeteoLook.Ink, Align.Left);
            Draw.Text(c, f.Body, "Источник / сообщение", new Box(list.X + 180, list.Y, list.W - 182, 15), MeteoLook.Ink, Align.Left);
            if (!session.HasJournal) { Draw.Text(c, f.Body, "Ожидание журнала сервера", new Box(list.X + 3, list.Y + 20, list.W - 6, 18), MeteoLook.Muted, Align.Left); }
            for (int row = 0; row < 4 && row < session.Journal.Count; row++) {
                int index = session.Journal.Count - 1 - row; MeteoEntry entry = session.Journal[index];
                float y = list.Y + 16 + row * 14;
                c.Fill(new Box(list.X, y, list.W, 14), row % 2 == 0 ? MeteoLook.Plot : MeteoLook.Zebra);
                Draw.Text(c, f.Body, journalTimes[index] ?? "—", new Box(list.X + 2, y, 56, 14), MeteoLook.Ink, Align.Left);
                Draw.Clipped(c, f.Body, journalStations[index] ?? "—", new Box(list.X + 61, y, 113, 14), MeteoLook.Ink, Align.Left);
                Draw.Clipped(c, f.Body, entry.Text, new Box(list.X + 180, y, list.W - 182, 14), entry.Text.StartsWith("ОПОВЕЩЕНИЕ", StringComparison.Ordinal) ? MeteoLook.Alarm : MeteoLook.Ink, Align.Left);
            }
            if (Button(c, f, 480, new Box(box.Right - 78, box.Y + 3, 69, 17), "Журнал…")) { SwitchTab(MeteoTab.Journal); }
            Draw.Text(c, f.Bold, "Сменное задание / памятка", memo.TakeTop(15), MeteoLook.Ink, Align.Left);
            int split = guidance.IndexOf('\n');
            if (split < 0) { Draw.Clipped(c, f.Body, guidance, new Box(memo.X + 2, memo.Y + 17, memo.W - 4, 28), MeteoLook.Ink, Align.Left); }
            else {
                Draw.Text(c, f.Body, guidance.AsSpan(0, split), new Box(memo.X + 2, memo.Y + 17, memo.W - 4, 14), MeteoLook.Ink, Align.Left);
                Draw.Text(c, f.Body, guidance.AsSpan(split + 1), new Box(memo.X + 2, memo.Y + 31, memo.W - 4, 14), MeteoLook.Ink, Align.Left);
            }
            var note = new Box(memo.X, memo.Bottom - 24, memo.W - 59, 23);
            if (noteField.Paint(c, ui, 481, f.Body, note, ReferenceEquals(typing, noteField), now, "Заметка смены…")) { typing = noteField; fieldClicked = true; }
            if (Button(c, f, 482, new Box(note.Right + 4, note.Y, 55, note.H), "Записать", !session.ActionPending && noteField.Text.Length > 0)) { SubmitNote(); }
        }
    }
}
