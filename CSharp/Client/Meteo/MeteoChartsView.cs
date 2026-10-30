using System;
using ScpItdMod.Ui;
using ScpItdMod.Xp;
using Draw = ScpItdMod.Ui.Paint;

namespace ScpItdMod.Meteo {
    public sealed partial class MeteoScreen {
        private static readonly string[] TraceTitles = { "Давление, гПа", "Температура, °C", "Скорость ветра, м/с", "Видимость, м (лог. шкала)" };
        private static readonly int[] TimeTickSeconds = { 120, 300, 1200, 3600 };
        private readonly string[,] axisLabels = new string[4, 16];
        private readonly int[] axisCounts = new int[4];
        private readonly string[] timeLabels = new string[8];
        private readonly string[] traceReadouts = new string[4];
        private int timeTickCount, windEvent = -1, pressureEvent = -1;
        private string cursorCaption = "Последнее наблюдение", slopeCaption = "dP/dt: нет оценки", sampleCaption = "Ряд не получен";
        private float hodoMaximum = 30;

        private void CacheAxes() {
            for (int metric = 0; metric < 4; metric++) {
                MeteoAxis axis = plotData.Axis((MeteoMetric)metric);
                int count = Math.Min(16, Math.Max(0, (int)MathF.Round((axis.High - axis.Low) / axis.Step)) + 1);
                axisCounts[metric] = count;
                for (int i = 0; i < count; i++) {
                    float value = axis.Low + i * axis.Step;
                    axisLabels[metric, i] = MeteoText.Number(metric == 3 ? MathF.Pow(10, value) : value, axis.Step < 1 && metric != 3 ? "0.#" : "0");
                }
            }
            timeTickCount = Math.Min(timeLabels.Length, (int)(Spans[spanIndex] / TimeTickSeconds[spanIndex]) + 1);
            for (int i = 0; i < timeTickCount; i++) { timeLabels[i] = i == 0 ? "0" : "−" + MeteoText.Number(i * TimeTickSeconds[spanIndex] / 60f, "0"); }
            hodoMaximum = MathF.Max(30, MathF.Ceiling(plotData.Axis(MeteoMetric.Wind).High / 10) * 10);
            windEvent = pressureEvent = -1;
            for (int i = 1; i < plotData.Count; i++) {
                if (!plotData.Connects(i)) { continue; }
                ref readonly MeteoSample current = ref plotData.At(i);
                ref readonly MeteoSample prior = ref plotData.At(i - 1);
                if (current.WindValid && prior.WindValid && prior.Wind < 10 && current.Wind >= 10) { windEvent = i; }
                if (pressureEvent < 0 && (current.Pressure - prior.Pressure) * 60 / (current.Time - prior.Time) <= -0.3) { pressureEvent = i; }
            }
            CacheWindHistory();
            sampleCaption = plotData.Count + " отсчётов / шаг записи 10 с";
        }

        private void Charts(ICanvas c, XpFaces f, Box body, double now) => Recorder(c, f, body, true);

        private void Recorder(ICanvas c, XpFaces f, in Box box, bool expanded) {
            Box inner = Panel(c, f, box, "СОВМЕЩЁННАЯ МЕТЕОГРАММА");
            float bottomBand = expanded ? 128 : 114;
            Box traces = new Box(inner.X, inner.Y, inner.W, inner.H - bottomBand);
            if (!PlotReady) {
                Draw.Text(c, f.Body, session.HasSeries ? "Нет записей станции за этот период" : "Ожидание архива наблюдений", traces, MeteoLook.Muted, Align.Center);
                return;
            }
            float rowH = (traces.H - 15) * .25f;
            float x0 = inner.X + 38, width = inner.W - 84;
            var hit = new Box(x0, traces.Y + 15, width, traces.H - 15);
            if (!selectorOpen && typing == null) {
                bool over = ui.Over(450, hit);
                if (over && ui.In.Pressed) { ui.Active = 450; }
                if (ui.Active == 450 && ui.In.Down) {
                    double selected = plotData.From + Math.Clamp((ui.In.X - hit.X) / hit.W, 0, 1) * (plotData.To - plotData.From);
                    SelectTime(selected, true);
                }
                if (ui.In.Up || ui.In.DownKey) {
                    int at = Historical ? historyIndex : plotData.Count - 1;
                    at = Math.Clamp(at + (ui.In.Up ? -1 : 1), 0, plotData.Count - 1);
                    SelectTime(plotData.At(at).Time, false);
                }
            }
            for (int metric = 0; metric < 4; metric++) {
                Box track = new Box(traces.X, traces.Y + metric * rowH, traces.W, rowH);
                Draw.Text(c, f.Bold, TraceTitles[metric], track.TakeTop(14), MeteoLook.Ink, Align.Left);
                DrawTrace(c, f, new Box(x0, track.Y + 17, width, rowH - 21), (MeteoMetric)metric);
            }
            float timeY = traces.Y + rowH * 4 - 1;
            for (int i = 0; i < timeTickCount; i++) {
                float fraction = 1 - (float)(i * TimeTickSeconds[spanIndex] / (plotData.To - plotData.From));
                if (fraction < 0) { continue; }
                float x = x0 + fraction * width;
                Draw.Text(c, f.Body, timeLabels[i], new Box(x - 19, timeY, 38, 14), MeteoLook.Ink, Align.Center);
            }
            Draw.Text(c, f.Body, "мин", new Box(x0 + width + 19, timeY, 26, 14), MeteoLook.Muted, Align.Right);
            RegimeRibbon(c, f, new Box(inner.X, traces.Bottom + 3, inner.W, 16));
            float eventsY = traces.Bottom + 21;
            EventStrip(c, f, new Box(inner.X, eventsY, inner.W, expanded ? 47 : 38), x0, width);
            float controlsY = eventsY + (expanded ? 51 : 42);
            if (Button(c, f, 451, new Box(inner.X, controlsY, 81, 19), "Последнее", Historical)) { SelectTime(double.NaN, false); }
            Draw.Clipped(c, f.Body, Historical && historyIndex < 0 ? "Пропуск: записи нет" : cursorCaption, new Box(inner.X + 87, controlsY, inner.W - 89, 19), Historical ? MeteoLook.Pressure : MeteoLook.Muted, Align.Left);
            float statsY = controlsY + 24;
            c.Fill(new Box(inner.X, statsY - 2, inner.W, 1), MeteoLook.Rule);
            Draw.Clipped(c, f.Body, slopeCaption, new Box(inner.X, statsY, inner.W * .59f, 16), O.Forecast.Valid && O.Forecast.Precursor ? MeteoLook.Alarm : MeteoLook.Ink, Align.Left);
            Draw.Clipped(c, f.Body, expanded ? sampleCaption : "Окно оценки 60 с", new Box(inner.X + inner.W * .59f, statsY, inner.W * .41f, 16), MeteoLook.Muted, Align.Right);
            if (expanded) { Draw.Clipped(c, f.Body, "↑ / ↓ — соседний отсчёт; щелчок и перетаскивание — общий срез четырёх каналов", new Box(inner.X, statsY + 20, inner.W, 18), MeteoLook.Muted, Align.Left); }
        }

        private void DrawTrace(ICanvas c, XpFaces f, in Box plot, MeteoMetric metric) {
            MeteoAxis axis = plotData.Axis(metric);
            c.Fill(plot, MeteoLook.Plot);
            int ticks = axisCounts[(int)metric];
            int stride = Math.Max(1, (int)MathF.Ceiling((ticks - 1) * 13 / plot.H));
            for (int i = 0; i < ticks; i++) {
                float y = plot.Bottom - axis.Fraction(axis.Low + i * axis.Step) * plot.H;
                c.Fill(new Box(plot.X, MathF.Round(y), plot.W, 1), MeteoLook.Grid);
                if (i % stride == 0) { Draw.Text(c, f.Body, axisLabels[(int)metric, i], new Box(plot.X - 38, y - 7, 34, 14), MeteoLook.Muted, Align.Right); }
            }
            for (int i = 0; i < timeTickCount; i++) {
                float x = plot.Right - i * TimeTickSeconds[spanIndex] / (float)(plotData.To - plotData.From) * plot.W;
                if (x >= plot.X) { Scope.DotsVertical(c, MathF.Round(x), plot.Y, plot.Bottom, MeteoLook.Grid, 1, 3); }
            }
            c.Fill(new Box(plot.X, plot.Y, 1, plot.H), MeteoLook.Axis);
            c.Fill(new Box(plot.X, plot.Bottom, plot.W, 1), MeteoLook.Axis);
            c.PushClip(plot.Inset(-1));
            for (int i = 0; i < plotData.GapCount; i++) {
                MeteoGap gap = plotData.Gap(i);
                float from = plot.X + (float)((gap.From - plotData.From) / (plotData.To - plotData.From)) * plot.W;
                float to = plot.X + (float)((gap.To - plotData.From) / (plotData.To - plotData.From)) * plot.W;
                Scope.Hatch(c, new Box(from, plot.Y, to - from, plot.H), MeteoLook.Axis, 5);
            }
            float px = 0, py = 0; bool previous = false;
            for (int i = 0; i < plotData.Count; i++) {
                float value = plotData.Value(metric, i);
                if (!float.IsFinite(value)) { previous = false; continue; }
                float x = plot.X + (float)((plotData.At(i).Time - plotData.From) / (plotData.To - plotData.From)) * plot.W;
                float y = plot.Bottom - axis.Fraction(value) * plot.H;
                if (previous && plotData.Connects(i)) { Scope.Line(c, px, py, x, y, MeteoLook.Pressure); }
                px = x; py = y; previous = true;
            }
            float cx = plot.X + Math.Clamp(cursor.Value, 0, 1) * plot.W;
            Scope.DotsVertical(c, MathF.Round(cx), plot.Y, plot.Bottom, MeteoLook.Chrome, 3, 2);
            if (Historical && historyIndex >= 0) {
                float value = plotData.Value(metric, historyIndex);
                if (float.IsFinite(value)) { Scope.Cross(c, cx, plot.Bottom - axis.Fraction(value) * plot.H, 3, MeteoLook.Ink); }
            }
            c.PopClip();
            float lastValue = HasReading ? MeteoPlotMath.Value(Reading, metric) : float.NaN;
            if (float.IsFinite(lastValue)) {
                float y = Math.Clamp(plot.Bottom - axis.Fraction(lastValue) * plot.H - 8, plot.Y, plot.Bottom - 16);
                var flag = new Box(plot.Right + 4, y, 40, 16);
                c.Fill(flag, MeteoLook.Highlight); Draw.Frame(c, flag, 1, MeteoLook.Pressure);
                Draw.Clipped(c, f.Bold, traceReadouts[(int)metric] ?? "—", flag.Inset(1), MeteoLook.Pressure, Align.Center);
            }
        }

        private void EventStrip(ICanvas c, XpFaces f, in Box box, float x0, float width) {
            c.Fill(box.TakeTop(15), MeteoLook.Rule);
            Draw.Text(c, f.Bold, "События по измерениям", box.TakeTop(15).Inset(3, 0, 0, 0), MeteoLook.Ink, Align.Left);
            float y = box.Y + 19;
            Scope.Dots(c, x0, y, x0 + width, MeteoLook.Axis, 1, 3);
            bool gapPresent = plotData.GapCount > 0;
            int measured = windEvent >= 0 ? windEvent : pressureEvent;
            double gapTime = gapPresent ? plotData.Gap(plotData.GapCount - 1).From : plotData.From;
            double measuredTime = measured >= 0 ? plotData.At(measured).Time : plotData.To;
            float gapLabel = Math.Clamp(EventX(x0, width, gapTime) - 50, x0, x0 + width - 100);
            float measuredLabel = Math.Clamp(EventX(x0, width, measuredTime) - 50, x0, x0 + width - 100);
            if (gapPresent && measured >= 0) {
                float first = Math.Clamp(MathF.Min(gapLabel, measuredLabel), x0, x0 + width - 204);
                float second = Math.Clamp(MathF.Max(gapLabel, measuredLabel), first + 104, x0 + width - 100);
                if (gapTime <= measuredTime) { gapLabel = first; measuredLabel = second; }
                else { measuredLabel = first; gapLabel = second; }
            }
            if (gapPresent) { EventMark(c, f, x0, width, y, gapTime, "Пропуск записи", gapLabel); }
            if (measured >= 0) { EventMark(c, f, x0, width, y, measuredTime, windEvent >= 0 ? "Ветер ≥ 10 м/с" : "Падение давления", measuredLabel); }
        }
        private void RegimeRibbon(ICanvas c, XpFaces f, in Box box) {
            bool valid = O.Forecast.Valid;
            c.Fill(box, MeteoLook.Highlight); Draw.Frame(c, box, 1, MeteoLook.Axis);
            float split = box.X + box.W * .5f;
            c.Fill(new Box(split, box.Y + 1, box.W * .5f - 1, box.H - 2), valid && O.Forecast.Alert >= MeteoAlert.Warning ? MeteoLook.WarnFill : MeteoLook.Zebra);
            Draw.Clipped(c, f.Body, valid ? observedRegime : "Нет оценки режима", new Box(box.X + 5, box.Y, box.W * .5f - 10, box.H), MeteoLook.Ink, Align.Center);
            Draw.Clipped(c, f.Bold, valid ? forecastRegime : "НЕТ ПРОГНОЗА", new Box(split + 5, box.Y, box.W * .5f - 10, box.H), valid ? RegimeTone(O.Forecast.Likely) : MeteoLook.Muted, Align.Center);
            Scope.Arrow(c, split - 10, box.CenterY, split + 10, box.CenterY, MeteoLook.Ink, 4);
        }
        private float EventX(float x0, float width, double time) => Math.Clamp(x0 + (float)((time - plotData.From) / (plotData.To - plotData.From)) * width, x0, x0 + width);
        private void EventMark(ICanvas c, XpFaces f, float x0, float width, float y, double time, string label, float labelX) {
            float x = EventX(x0, width, time);
            c.Fill(new Box(MathF.Round(x), y - 3, 2, 8), MeteoLook.Alarm);
            Scope.Line(c, x, y + 4, Math.Clamp(x, labelX + 4, labelX + 96), y + 4, MeteoLook.Axis);
            Draw.Clipped(c, f.Body, label, new Box(labelX, y + 5, 100, 14), MeteoLook.Ink, Align.Left);
        }
    }
}
