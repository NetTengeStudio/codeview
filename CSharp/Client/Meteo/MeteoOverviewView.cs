using System;
using ScpItdMod.Ui;
using ScpItdMod.Xp;
using Draw = ScpItdMod.Ui.Paint;

namespace ScpItdMod.Meteo {
    public sealed partial class MeteoScreen {
        private static readonly string[] ChannelCodes = { "T1", "P1", "W1", "W2", "V1", "S1", "I1", "H1" };
        private static readonly string[] ChannelNames = { "Температура", "Давление, абс.", "Ветер", "Направление", "Дальность", "Снежный", "Обледенение", "Влажность" };
        private static readonly float[] ScaleMin = { -80, 500, 0, 0, 1, 0, 0, 0 };
        private static readonly float[] ScaleMax = { 20, 850, 40, 360, 5, 2, 100, 100 };
        private static readonly string[,] ScaleLabels = { { "−80", "−30", "+20" }, { "500", "675", "850" }, { "0", "20", "40" }, { "0", "180", "360" }, { "10", "1к", "100к" }, { "0", "1", "2" }, { "0", "50", "100" }, { "0", "50", "100" } };
        private readonly string[] readouts = new string[8];
        private bool readoutStale, readoutsEmpty = true;
        private string stationCaption = "Выбор станции", serverClockCaption = "Время сервера —";
        private readonly string[] stationCaptions = new string[MeteoWire.MaxStations], stationAges = new string[MeteoWire.MaxStations], stationShort = new string[MeteoWire.MaxStations];
        private int stationOffset;

        private void ClearReadings() {
            slopeCaption = Historical ? "dP/dt: нет оценки в пропуске" : "dP/dt: нет наблюдений";
            if (readoutsEmpty) { return; }
            Array.Fill(readouts, "—"); readoutsEmpty = true; readoutTime = double.NaN;
        }
        private void CacheReadings(in MeteoSample sample) {
            readoutStale = Stale; readoutsEmpty = false;
            readouts[0] = MeteoText.Signed(sample.Temperature) + " °C";
            readouts[1] = MeteoText.Number(sample.Pressure) + " гПа";
            readouts[2] = sample.WindValid ? MeteoText.Number(sample.Wind) + " м/с" : "НЕТ ДАННЫХ";
            float degrees = (sample.Direction * 180 / MathF.PI % 360 + 360) % 360;
            readouts[3] = sample.WindValid ? degrees.ToString("000", System.Globalization.CultureInfo.InvariantCulture) + "° " + MeteoPhysics.Compass(sample.Direction) : "—";
            readouts[4] = VisibilityCaption(sample.Visibility);
            readouts[5] = MeteoText.Number(sample.Snow, "0.00") + " м";
            readouts[6] = MeteoText.Number(sample.Ice * 100, "0") + " %";
            readouts[7] = sample.HumidityValid ? MeteoText.Number(sample.Humidity * 100, "0") + " %" : "НЕТ ДАННЫХ";
            traceReadouts[0] = MeteoText.Number(sample.Pressure); traceReadouts[1] = MeteoText.Signed(sample.Temperature);
            traceReadouts[2] = sample.WindValid ? MeteoText.Number(sample.Wind) : "—";
            traceReadouts[3] = MeteoText.Number(sample.Visibility, "0");
            cursorCaption = Clock(sample.Time) + (Historical ? " / архивный срез" : " / последнее наблюдение");
            if (Historical) { slopeCaption = plotData.PressureSlope(sample.Time, 60, out float historicSlope, out _) ? "dP/dt " + MeteoText.Signed(historicSlope) + " гПа/мин (архив)" : "dP/dt: недостаточно данных"; }
            else { slopeCaption = O.Forecast.Valid ? "dP/dt " + MeteoText.Signed(O.Forecast.PressureSlope) + " гПа/мин" : "dP/dt: недостаточно данных"; }
            if (sample.Wind > hodoMaximum) { hodoMaximum = MathF.Ceiling(sample.Wind / 10) * 10; CacheWindHistory(); }
        }
        private void CacheStations() {
            for (int i = 0; i < O.Stations.Count; i++) {
                MeteoStationInfo info = O.Stations[i];
                const string prefix = "Метеостанция ";
                stationShort[i] = info.Name.StartsWith(prefix, StringComparison.Ordinal) ? info.Name.Substring(prefix.Length) : info.Name;
                string flag = info.Kind == MeteoKind.Barograph ? " / ручной" : !info.Powered ? " / нет питания" : !info.Linked ? " / нет связи" : "";
                stationCaptions[i] = info.Name + flag;
                stationAges[i] = info.Kind == MeteoKind.Barograph ? "ручной" : info.Last <= 0 ? "—" : MeteoText.Duration((float)Math.Max(0, O.Now - info.Last));
            }
            stationCaption = StationName(station);
            serverClockCaption = "Время сервера " + clockText;
        }

        private void Overview(ICanvas c, XpFaces f, Box body, double now) {
            float mainH = body.H - 112, leftW = 306, rightW = 244;
            var left = new Box(body.X, body.Y, leftW, mainH);
            var recorder = new Box(left.Right + 6, body.Y, body.W - leftW - rightW - 12, mainH);
            var right = new Box(recorder.Right + 6, body.Y, rightW, mainH);
            InstrumentLedger(c, f, left);
            Recorder(c, f, recorder, false);
            WindAnalysis(c, f, new Box(right.X, right.Y, right.W, 279));
            Operations(c, f, new Box(right.X, right.Y + 285, right.W, right.H - 285), now);
            WatchJournal(c, f, new Box(body.X, left.Bottom + 6, body.W, 106), now);
        }

        private void InstrumentLedger(ICanvas c, XpFaces f, in Box box) {
            Box inner = Panel(c, f, box, "ИЗМЕРИТЕЛЬНЫЙ КОНТУР");
            Draw.Text(c, f.Body, "Канал / параметр", new Box(inner.X + 2, inner.Y, 111, 16), MeteoLook.Ink, Align.Left);
            Draw.Text(c, f.Body, "Значение", new Box(inner.X + 117, inner.Y, 98, 16), MeteoLook.Ink, Align.Left);
            Draw.Text(c, f.Body, "Качество", new Box(inner.X + 218, inner.Y, 74, 16), MeteoLook.Ink, Align.Left);
            float rowsTop = inner.Y + 19, rowH = 42;
            for (int i = 0; i < 8; i++) {
                Box row = new Box(inner.X, rowsTop + i * rowH, inner.W, rowH);
                c.Fill(row, i % 2 == 0 ? MeteoLook.Plot : MeteoLook.Zebra);
                c.Fill(new Box(row.X, row.Bottom - 1, row.W, 1), MeteoLook.Rule);
                c.Fill(new Box(row.X + 110, row.Y, 1, row.H), MeteoLook.Rule);
                c.Fill(new Box(row.X + 215, row.Y, 1, row.H), MeteoLook.Rule);
                Draw.Text(c, f.Body, ChannelCodes[i], new Box(row.X + 2, row.Y + 3, 22, 17), MeteoLook.Ink, Align.Left);
                Draw.Clipped(c, f.Body, ChannelNames[i], new Box(row.X + 27, row.Y + 3, 82, 17), MeteoLook.Ink, Align.Left);
                if (i == 2 || i == 4 || i == 5) { Draw.Text(c, f.Body, i == 2 ? "(скорость)" : i == 4 ? "видимости" : "покров", new Box(row.X + 27, row.Y + 17, 82, 15), MeteoLook.Ink, Align.Left); }
                bool invalid = !HasReading || i == 7 && !Reading.HumidityValid || (i == 2 || i == 3) && !Reading.WindValid;
                string quality = !HasReading ? "Нет записи" : invalid ? "Недост." : Stale ? "Устарело" : i == 4 ? "Достов." : "Норма";
                Rgba tone = invalid ? MeteoLook.Alarm : Stale ? MeteoLook.Warn : MeteoLook.Pressure;
                Draw.Clipped(c, invalid ? f.Body : f.Bold, HasReading ? readouts[i] ?? "—" : "—", new Box(row.X + 116, row.Y + 1, 98, 20), tone, Align.Left);
                Draw.Clipped(c, f.Body, quality, new Box(row.X + 220, row.Y + 3, 64, 17), invalid || Stale ? MeteoLook.Warn : MeteoLook.Ink, Align.Left);
                c.Fill(new Box(row.Right - 8, row.Y + 7, 6, 6), invalid ? MeteoLook.Alarm : Stale ? MeteoLook.Warn : MeteoLook.Good);
                if (invalid && i == 7 && HasReading) { Draw.Clipped(c, f.Body, "Ниже диапазона −40 °C", new Box(row.X + 116, row.Y + 23, 174, 16), MeteoLook.Muted, Align.Left); }
                else if (invalid && (i == 2 || i == 3) && HasReading) { Draw.Clipped(c, f.Body, "Проверьте анемометр", new Box(row.X + 116, row.Y + 23, 174, 16), MeteoLook.Alarm, Align.Left); }
                else if (!invalid) { InstrumentScale(c, f, i, new Box(row.X + 117, row.Y + 24, 94, 16)); }
            }
            float stationY = rowsTop + 8 * rowH + 5;
            StationLedger(c, f, new Box(inner.X, stationY, inner.W, 76));
            LinkDiagram(c, f, new Box(inner.X, stationY + 81, inner.W, inner.Bottom - stationY - 81));
        }

        private void InstrumentScale(ICanvas c, XpFaces f, int channel, in Box box) {
            float value = channel switch {
                0 => temperature.Value, 1 => pressure.Value, 2 => windVector.Value.Length(),
                3 => (MathF.Atan2(-windVector.Value.X, windVector.Value.Y) * 180 / MathF.PI + 360) % 360,
                4 => visibility.Value, 5 => snow.Value, 6 => ice.Value * 100, _ => Reading.Humidity * 100
            };
            c.Fill(new Box(box.X, box.Y + 1, box.W, 1), MeteoLook.Axis);
            for (int i = 0; i <= 4; i++) { c.Fill(new Box(MathF.Round(box.X + box.W * i / 4), box.Y - 1, 1, 5), MeteoLook.Axis); }
            float fraction = Math.Clamp((value - ScaleMin[channel]) / (ScaleMax[channel] - ScaleMin[channel]), 0, 1);
            float x = box.X + fraction * box.W;
            c.Fill(new Box(MathF.Round(x) - 1, box.Y - 3, 3, 6), MeteoLook.Pressure);
            for (int i = 0; i < 3; i++) {
                Box label = new Box(box.X + i * box.W * .5f - (i == 0 ? 0 : i == 1 ? 18 : 36), box.Y + 5, 36, 12);
                Draw.Text(c, f.Body, ScaleLabels[channel, i], label, MeteoLook.Muted, i == 0 ? Align.Left : i == 1 ? Align.Center : Align.Right);
            }
        }

        private void StationLedger(ICanvas c, XpFaces f, in Box box) {
            c.Fill(box.TakeTop(17), MeteoLook.Rule);
            Draw.Text(c, f.Bold, "Состояние станций", box.TakeTop(17).Inset(3, 0, 0, 0), MeteoLook.Ink, Align.Left);
            if (Button(c, f, 400, new Box(box.Right - 39, box.Y + 1, 18, 15), "‹", stationOffset > 0)) { stationOffset--; }
            if (Button(c, f, 401, new Box(box.Right - 19, box.Y + 1, 18, 15), "›", stationOffset + 2 < O.Stations.Count)) { stationOffset++; }
            Draw.Text(c, f.Body, "Станция", new Box(box.X + 3, box.Y + 18, 113, 15), MeteoLook.Ink, Align.Left);
            Draw.Text(c, f.Body, "Связь / обогрев", new Box(box.X + 116, box.Y + 18, 107, 15), MeteoLook.Ink, Align.Left);
            Draw.Text(c, f.Body, "Приём", new Box(box.Right - 67, box.Y + 18, 64, 15), MeteoLook.Ink, Align.Right);
            for (int row = 0; row < 2 && row + stationOffset < O.Stations.Count; row++) {
                int index = row + stationOffset; MeteoStationInfo info = O.Stations[index];
                Box line = new Box(box.X, box.Y + 34 + row * 19, box.W, 19);
                c.Fill(line, info.Id == station ? MeteoLook.Plot : MeteoLook.Surface);
                Draw.Clipped(c, f.Body, stationShort[index] ?? info.Name, line.TakeLeft(115).Inset(3, 0, 1, 0), MeteoLook.Ink, Align.Left);
                string state = info.Kind == MeteoKind.Barograph ? "ручной" : !info.Powered ? "Нет питания" : !info.Linked ? "Нет связи" : info.Heated ? "Есть / вкл." : "Есть / выкл.";
                Draw.Clipped(c, f.Body, state, new Box(line.X + 116, line.Y, 111, line.H), info.Powered ? MeteoLook.Ink : MeteoLook.Alarm, Align.Left);
                Draw.Text(c, f.Body, stationAges[index] ?? "—", new Box(line.Right - 65, line.Y, 62, line.H), MeteoLook.Ink, Align.Right);
                if (!selectorOpen && info.Kind == MeteoKind.Station && ui.Click(410 + index, line)) { SelectStation(info.Id); }
            }
        }

        private void LinkDiagram(ICanvas c, XpFaces f, in Box box) {
            if (box.H < 44) { return; }
            Draw.Text(c, f.Body, "Линия связи и архив", box.TakeTop(16), MeteoLook.Ink, Align.Left);
            Box sensor = new Box(box.X + 3, box.Y + 22, 92, Math.Min(40, box.H - 24));
            Box rack = new Box(box.Right - 116, sensor.Y, 112, sensor.H);
            Draw.Frame(c, sensor, 1, MeteoLook.Axis); Draw.Frame(c, rack, 1, MeteoLook.Axis);
            Draw.Text(c, f.Bold, "АМС-М", sensor.TakeTop(20), MeteoLook.Ink, Align.Center);
            Draw.Text(c, f.Body, stationOperational ? "линия исправна" : "проверьте линию", new Box(sensor.X, sensor.Y + 18, sensor.W, 18), stationOperational ? MeteoLook.Muted : MeteoLook.Alarm, Align.Center);
            Draw.Text(c, f.Bold, "АРХИВ СХ-4", rack.TakeTop(20), MeteoLook.Ink, Align.Center);
            Draw.Text(c, f.Body, O.RackPowered ? "запись включена" : "нет питания", new Box(rack.X, rack.Y + 18, rack.W, 18), O.RackPowered ? MeteoLook.Muted : MeteoLook.Alarm, Align.Center);
            float y = sensor.CenterY;
            Scope.Dots(c, sensor.Right + 2, y, rack.X - 3, MeteoLook.Axis, 2, 2);
            Scope.Arrow(c, rack.X - 13, y, rack.X - 3, y, stationOperational ? MeteoLook.Pressure : MeteoLook.Alarm, 5);
        }

    }
}
