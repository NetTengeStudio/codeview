using System;
using System.Text;
using ScpItdMod.Ui;
using ScpItdMod.Weather;
using ScpItdMod.Xp;
using Draw = ScpItdMod.Ui.Paint;

namespace ScpItdMod.Meteo {
    public sealed partial class MeteoScreen {
        private static readonly string[] RegimeCaps = { "Ясно", "Снегопад", "Позёмок", "Метель", "Буран", "Прояснение" };
        private static readonly string[] AlertLevels = { "Наблюдение", "Предупреждение", "Шторм" };
        private static readonly string[] ExpectedNames = { "Температура", "Ветер", "Видимость" };
        private readonly string[] probabilityLabels = new string[6], expectedLabels = new string[3], etaLabels = new string[5];
        private readonly TextBlock[] basisBlocks = { new TextBlock(), new TextBlock(), new TextBlock(), new TextBlock(), new TextBlock() };
        private readonly TextBlock alertHelp = new TextBlock();
        private string coverageCaption = "—", confidenceCaption = "—", etaDetail = "—", lastAlertCaption = "Оповещений в этой смене не было.", cooldownCaption = "Передать всем";
        private float etaHorizon = 240;
        private bool alertSeeded, fieldClicked, alertCooling;

        private void CacheForecastDetails() {
            MeteoForecast fc = O.Forecast;
            for (int i = 0; i < 6; i++) { probabilityLabels[i] = fc.Valid ? MeteoText.Number(fc.Probability[i] * 100, "0") + "%" : "—"; }
            etaHorizon = fc.Valid ? MathF.Max(240, fc.EtaHigh * 1.15f) : 240;
            for (int i = 0; i < etaLabels.Length; i++) { etaLabels[i] = i == 0 ? "сейчас" : MeteoText.Duration(etaHorizon * i / 4); }
            confidenceCaption = fc.Valid ? MeteoText.Number(fc.Confidence * 100, "0") + "%" : "—";
            coverageCaption = fc.Valid ? fc.Samples + " отсчётов / непрерывность " + MeteoText.Number(fc.CoverageFraction * 100, "0") + "%" : "Непрерывного ряда недостаточно";
            etaDetail = fc.Valid ? "Центральная оценка " + MeteoText.Duration(fc.EtaMid) + " / интервал " + forecastRange : "Нужно накопить наблюдения";
            expectedLabels[0] = fc.Valid ? MeteoText.Signed(fc.TemperatureNext, "0") + " °C" : "—";
            expectedLabels[1] = fc.Valid ? MeteoText.Number(fc.WindNext, "0") + " м/с" : "—";
            expectedLabels[2] = fc.Valid ? VisibilityCaption(fc.VisibilityNext) : "—";
            basisBlocks[0].Set(!fc.Valid ? "Непрерывного ряда наблюдений недостаточно для оценки смены режима." : "Давление " + MeteoText.Trend(fc.Trend) + ": " + MeteoText.Signed(fc.PressureSlope) + " гПа/мин. Отклонение от нормы режима " + MeteoText.Signed(-fc.PressureDrop) + " гПа." + (fc.Precursor ? " Зафиксирован предвестник ухудшения." : " Предвестник не обнаружен."));
            basisBlocks[1].Set(fc.Valid ? "Режим «" + MeteoText.Regime(fc.Observed) + "» наблюдается " + MeteoText.Duration(fc.Age) + (fc.AgeExact ? ". Начало зафиксировано в архиве." : ". Начало не зафиксировано: оценка ограничена глубиной ряда или пропуском.") : "Проверьте станцию, питание стойки и линию связи.");
            basisBlocks[2].Set(coverageCaption);
            WeatherProfile profile = fc.Valid ? MeteoWorld.Config()?.Profiles[(int)fc.Observed] : null;
            if (profile != null) {
                var text = new StringBuilder(160); text.Append("Климатическая модель переходов: ");
                bool first = true;
                for (int i = 0; i < 6; i++) {
                    if (profile.Transitions[i] <= 0) { continue; }
                    if (!first) { text.Append("; "); } first = false;
                    text.Append(MeteoText.Regime((WeatherRegime)i)).Append(' ').Append(MeteoText.Number(profile.Transitions[i] * 100, "0")).Append('%');
                }
                basisBlocks[3].Set(text.ToString());
                basisBlocks[4].Set("Продолжительность режима по модели: " + MeteoText.Duration(profile.Minimum) + "–" + MeteoText.Duration(profile.Maximum) + ". Прогноз описывает вероятность, а не расписание погоды.");
            } else { basisBlocks[3].Set(""); basisBlocks[4].Set(""); }
            alertHelp.Set("Сообщение и сигнал получают все участники текущей смены. Сервер проверяет доступ и интервал между передачами; результат записывается в журнал.");
        }

        private static string VisibilityCaption(float metres) => metres >= 1000 ? MeteoText.Number(metres / 1000, "0.#") + " км" : MeteoText.Number(metres, "0") + " м";

        private void CacheAlertClock() {
            double elapsed = O.Now - O.LastAlert;
            alertCooling = O.LastAlert > 0 && double.IsFinite(elapsed) && elapsed < 45;
            cooldownCaption = alertCooling ? "Повтор через " + (int)Math.Ceiling(45 - elapsed) + " с" : "Передать всем";
            lastAlertCaption = O.LastAlert > 0 && double.IsFinite(elapsed) ? "Последняя передача: " + MeteoText.Duration((float)Math.Max(0, elapsed)) + " назад." : "Оповещений в этой смене не было.";
        }

        private void Forecast(ICanvas c, XpFaces f, Box b, double now) {
            float width = (b.W - 6) * .5f, top = 279;
            Probabilities(c, f, new Box(b.X, b.Y, width, top), O.Forecast);
            Timeline(c, f, new Box(b.X + width + 6, b.Y, width, top), O.Forecast);
            Basis(c, f, new Box(b.X, b.Y + top + 6, width, b.H - top - 6), O.Forecast);
            AlertPanel(c, f, new Box(b.X + width + 6, b.Y + top + 6, width, b.H - top - 6), O.Forecast, now);
        }

        private static Rgba RegimeTone(WeatherRegime regime) {
            int severity = WeatherDirector.Severity(regime);
            return severity >= 4 ? MeteoLook.Alarm : severity >= 3 ? MeteoLook.Warn : severity >= 1 ? MeteoLook.Pressure : MeteoLook.Good;
        }

        private void Probabilities(ICanvas c, XpFaces f, in Box box, in MeteoForecast fc) {
            Box inner = Panel(c, f, box, "ВЕРОЯТНОСТЬ СЛЕДУЮЩЕГО РЕЖИМА");
            Draw.Clipped(c, f.Body, currentRegimeCaption, new Box(inner.X + 3, inner.Y, inner.W - 6, 19), MeteoLook.Ink, Align.Left);
            for (int i = 0; i < 6; i++) {
                bool observed = fc.Valid && i == (int)fc.Observed, likely = fc.Valid && i == (int)fc.Likely;
                var row = new Box(inner.X, inner.Y + 24 + i * 30, inner.W, 28);
                c.Fill(row, observed ? MeteoLook.Highlight : i % 2 == 0 ? MeteoLook.Plot : MeteoLook.Zebra);
                Draw.Text(c, observed ? f.Bold : f.Body, RegimeCaps[i], new Box(row.X + 5, row.Y, 85, row.H), MeteoLook.Ink, Align.Left);
                var bar = new Box(row.X + 93, row.Y + 7, row.W - 263, 14);
                c.Fill(bar, MeteoLook.Surface); Draw.Frame(c, bar, 1, MeteoLook.Axis);
                if (fc.Valid) { c.Fill(new Box(bar.X + 1, bar.Y + 1, (bar.W - 2) * Math.Clamp(probability[i].Value, 0, 1), bar.H - 2), RegimeTone((WeatherRegime)i)); }
                Draw.Text(c, f.Bold, probabilityLabels[i] ?? "—", new Box(bar.Right + 6, row.Y, 40, row.H), MeteoLook.Ink, Align.Right);
                Draw.Text(c, f.Body, observed ? "текущий" : likely ? "вероятнее всего" : "", new Box(bar.Right + 54, row.Y, 112, row.H), MeteoLook.Muted, Align.Left);
            }
            Draw.Clipped(c, f.Body, SourceStale ? "Архивная оценка: приём устарел" : coverageCaption, new Box(inner.X + 3, inner.Bottom - 23, inner.W - 6, 20), SourceStale ? MeteoLook.Warn : MeteoLook.Muted, Align.Left);
        }

        private void Timeline(ICanvas c, XpFaces f, in Box box, in MeteoForecast fc) {
            Box inner = Panel(c, f, box, "СМЕНА РЕЖИМА / РАСЧЁТНЫЙ ИНТЕРВАЛ");
            Draw.Clipped(c, f.Header, fc.Valid ? forecastRegime : "НЕТ ОЦЕНКИ", new Box(inner.X + 4, inner.Y, inner.W - 8, 25), fc.Valid ? RegimeTone(fc.Likely) : MeteoLook.Muted, Align.Left);
            Draw.Clipped(c, f.Body, etaDetail, new Box(inner.X + 4, inner.Y + 29, inner.W - 8, 19), MeteoLook.Ink, Align.Left);
            var ruler = new Box(inner.X + 8, inner.Y + 67, inner.W - 16, 17);
            c.Fill(ruler, MeteoLook.Plot); Draw.Frame(c, ruler, 1, MeteoLook.Axis);
            if (fc.Valid) {
                float x = ruler.X + Math.Clamp(fc.EtaLow / etaHorizon, 0, 1) * ruler.W;
                float end = ruler.X + Math.Clamp(fc.EtaHigh / etaHorizon, 0, 1) * ruler.W;
                c.Fill(new Box(x, ruler.Y + 2, MathF.Max(2, end - x), ruler.H - 4), MeteoLook.WarnFill);
                Draw.Frame(c, new Box(x, ruler.Y + 2, MathF.Max(2, end - x), ruler.H - 4), 1, RegimeTone(fc.Likely));
                float mark = ruler.X + Math.Clamp(eta.Value / etaHorizon, 0, 1) * ruler.W;
                c.Fill(new Box(MathF.Round(mark) - 1, ruler.Y - 4, 3, ruler.H + 8), MeteoLook.Ink);
            }
            for (int i = 0; i < 5; i++) {
                float x = ruler.X + i * ruler.W / 4;
                c.Fill(new Box(MathF.Round(x), ruler.Bottom, 1, 4), MeteoLook.Axis);
                Draw.Text(c, f.Body, etaLabels[i] ?? "—", new Box(x - (i == 0 ? 0 : i == 4 ? 60 : 30), ruler.Bottom + 5, 60, 16), MeteoLook.Muted, i == 0 ? Align.Left : i == 4 ? Align.Right : Align.Center);
            }
            Draw.Text(c, f.Bold, "Ожидаемые средние после перехода", new Box(inner.X + 4, inner.Y + 119, inner.W - 8, 18), MeteoLook.Ink, Align.Left);
            for (int i = 0; i < 3; i++) {
                float x = inner.X + 4 + i * (inner.W - 8) / 3;
                Draw.Text(c, f.Body, ExpectedNames[i], new Box(x, inner.Y + 143, 145, 17), MeteoLook.Muted, Align.Left);
                Draw.Text(c, f.Bold, expectedLabels[i] ?? "—", new Box(x, inner.Y + 163, 145, 20), MeteoLook.Ink, Align.Left);
            }
            Draw.Text(c, f.Body, "Уверенность", new Box(inner.X + 4, inner.Bottom - 37, 88, 17), MeteoLook.Ink, Align.Left);
            XpPaint.Progress(c, new Box(inner.X + 97, inner.Bottom - 35, inner.W - 147, 13), Math.Clamp(confidence.Value, 0, 1));
            Draw.Text(c, f.Bold, confidenceCaption, new Box(inner.Right - 44, inner.Bottom - 37, 40, 17), MeteoLook.Ink, Align.Right);
            Draw.Clipped(c, f.Body, forecastBasis, new Box(inner.X + 4, inner.Bottom - 17, inner.W - 8, 16), MeteoLook.Muted, Align.Left);
        }

        private void Basis(ICanvas c, XpFaces f, in Box box, in MeteoForecast fc) {
            Box inner = Panel(c, f, box, "ОСНОВАНИЯ И ОГРАНИЧЕНИЯ ОЦЕНКИ");
            float y = inner.Y + 5;
            for (int i = 0; i < basisBlocks.Length; i++) {
                TextBlock block = basisBlocks[i]; block.Layout(f.Body, inner.W - 26);
                if (block.Count == 0) { continue; }
                c.Fill(new Box(inner.X + 5, y + 4, 6, 6), i == 0 && fc.Precursor ? MeteoLook.Alarm : i == 2 && fc.CoverageFraction < .8f ? MeteoLook.Warn : MeteoLook.Pressure);
                block.Paint(c, f.Body, new Box(inner.X + 20, y, inner.W - 26, MathF.Min(block.Height, inner.Bottom - y)), MeteoLook.Ink);
                y += block.Height + 16;
            }
        }

        private string Suggest(in MeteoForecast fc) {
            if (!fc.Valid) { return "Метеослужба: данных для прогноза недостаточно. Проверьте приборы и следите за сводками."; }
            string when = fc.EtaMid < 60 ? "в ближайшую минуту" : "через " + MeteoText.Duration(fc.EtaMid);
            return fc.Alert >= MeteoAlert.Warning ? "Ожидается " + MeteoText.Regime(fc.Likely) + " " + when + ". Закройте наружные работы, вернитесь в укрытие." : "Возможна смена погоды: " + MeteoText.Regime(fc.Likely) + " " + when + ". Следите за сводками.";
        }

        private void AlertPanel(ICanvas c, XpFaces f, in Box box, in MeteoForecast fc, double now) {
            Box inner = Panel(c, f, box, "ОПОВЕЩЕНИЕ ПЕРСОНАЛА БАЗЫ");
            Draw.Text(c, f.Bold, "Уровень передачи", new Box(inner.X + 4, inner.Y + 2, inner.W - 8, 18), MeteoLook.Ink, Align.Left);
            float width = (inner.W - 12) / 3;
            for (int i = 0; i < 3; i++) {
                if (Toggle(c, f, 180 + i, new Box(inner.X + 2 + i * (width + 4), inner.Y + 25, width, 24), AlertLevels[i], alertLevel == i + 1)) { alertLevel = i + 1; }
            }
            bool focused = ReferenceEquals(typing, alertField);
            if (alertField.Text.Length == 0 && !focused && !alertSeeded) { alertField.Load(Suggest(fc)); alertSeeded = true; }
            Draw.Text(c, f.Body, "Текст сообщения / до 160 знаков", new Box(inner.X + 4, inner.Y + 60, inner.W - 8, 18), MeteoLook.Muted, Align.Left);
            var field = new Box(inner.X + 2, inner.Y + 83, inner.W - 4, 86);
            if (alertField.Paint(c, ui, 190, f.Body, field, focused, now, "Текст оповещения")) { typing = alertField; fieldClicked = true; }
            if (Button(c, f, 191, new Box(inner.X + 2, field.Bottom + 9, 151, 24), "Подставить прогноз", !session.ActionPending)) { alertField.Load(Suggest(fc)); }
            if (Button(c, f, 192, new Box(inner.Right - 177, field.Bottom + 9, 175, 24), session.ActionPending ? "Ожидание сервера…" : cooldownCaption, Live && !session.ActionPending && !alertCooling && alertField.Text.Length > 0, true)) { SubmitAlert(); }
            Draw.Clipped(c, f.Body, lastAlertCaption, new Box(inner.X + 4, field.Bottom + 41, inner.W - 8, 18), MeteoLook.Ink, Align.Left);
            alertHelp.Paint(c, f.Body, new Box(inner.X + 4, field.Bottom + 68, inner.W - 8, 54), MeteoLook.Muted);
            Draw.Clipped(c, f.Bold, session.ActionPending ? "Передача ожидает подтверждения" : session.ActionState == MeteoActionState.Unconfirmed ? "Нет подтверждения: проверьте журнал перед повтором" : session.ActionState == MeteoActionState.Rejected ? MeteoSession.Explain(session.LastAck) : "Enter — передать; Esc — выйти из поля", new Box(inner.X + 4, inner.Bottom - 21, inner.W - 8, 19), session.ActionState == MeteoActionState.Unconfirmed || session.ActionState == MeteoActionState.Rejected ? MeteoLook.Alarm : MeteoLook.Muted, Align.Left);
        }
    }
}
