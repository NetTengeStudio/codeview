using System;
using ScpItdMod.Ui;
using ScpItdMod.Xp;
using Draw = ScpItdMod.Ui.Paint;

namespace ScpItdMod.Meteo {
    public sealed partial class MeteoScreen {
        private static readonly string[] RackNames = { "Состояние", "Питание", "Накопители", "Занято блоков", "Записей принято", "Перезаписано", "Глубина архива", "Границы записи" };
        private static readonly string[] DeviceHeaders = { "Прибор", "Питание", "Линия", "Лёд", "Приём" };
        private readonly string[] rackValues = new string[8], stationIce = new string[MeteoWire.MaxStations];
        private readonly TextBlock[] journalBlocks = CreateBlocks(MeteoWire.MaxEntries), serviceRules = CreateBlocks(5);
        private readonly TextBlock bulletinHelp = new TextBlock();
        private float journalScroll;
        private string noteCounter = "", storageWarning = "";
        private int noteCount = -1;

        private static TextBlock[] CreateBlocks(int count) {
            var result = new TextBlock[count];
            for (int i = 0; i < count; i++) { result[i] = new TextBlock(); }
            return result;
        }

        private string StationName(ushort id) {
            for (int i = 0; i < O.Stations.Count; i++) { if (O.Stations[i].Id == id) { return O.Stations[i].Name; } }
            return id == 0 ? "Пульт" : "Неизвестный прибор";
        }

        private void CacheServiceText() {
            rackValues[0] = O.RackPresent ? "Стойка подключена" : "Нет связи со стойкой";
            rackValues[1] = O.RackPowered ? "Есть" : "Отключено";
            rackValues[2] = O.Disks + " из " + MeteoArchive.MaxDisks;
            rackValues[3] = O.Used + " / " + O.Capacity;
            rackValues[4] = O.Written.ToString(System.Globalization.CultureInfo.InvariantCulture);
            rackValues[5] = O.Overwritten.ToString(System.Globalization.CultureInfo.InvariantCulture);
            rackValues[6] = MeteoText.Duration((float)Math.Max(0, O.Newest - O.Oldest));
            rackValues[7] = O.Newest > 0 ? Clock(O.Oldest) + "–" + Clock(O.Newest) : "—";
            for (int i = 0; i < O.Stations.Count; i++) { stationIce[i] = MeteoText.Number(O.Stations[i].Ice * 100, "0") + "%"; }
            storageWarning = !O.RackPresent ? "Стойка недоступна. Проверьте линию." : !O.RackPowered ? "Запись и чтение остановлены: нет питания." : O.Capacity > 0 && O.Used > O.Capacity * .9f ? "Архив заполнен: старые записи вытесняются." : "Кольцевой архив. Старые записи вытесняются первыми.";
            bulletinHelp.Set("Бюллетень сохраняется в папку «Метео» этого компьютера. Откройте файл в блокноте, чтобы прочесть или распечатать сводку. Сервер использует последние наблюдения выбранной станции и проверяет их давность.");
            serviceRules[0].Set("Станция передаёт наблюдения каждые 10 секунд. При обрыве линии локальная очередь хранит до 30 записей; после восстановления связи накопленное досылается в архив.");
            serviceRules[1].Set("Данные хранятся на серверной стойке. Предел линии между стойкой и станцией или компьютером — 120 м. Барограф считывается вручную и не передаёт ряд автоматически.");
            serviceRules[2].Set("Базовый объём — " + MeteoArchive.BaseCapacity + " блоков. Каждый накопитель добавляет " + MeteoArchive.DiskBlocks + ". Извлечение накопителей уменьшает архив; старые записи вытесняются.");
            serviceRules[3].Set("Отключение питания сохраняет накопленный архив, но останавливает запись и чтение. Пропуски видны на метеограмме и снижают уверенность прогноза.");
            serviceRules[4].Set("Разрушение стойки уничтожает её архив. Бюллетени и журналы, сохранённые файлами на компьютере, остаются на его диске.");
        }

        private void Journal(ICanvas c, XpFaces f, Box b, double now) {
            Box list = Panel(c, f, new Box(b.X, b.Y, b.W, b.H - 105), "ЖУРНАЛ НАБЛЮДЕНИЙ И ОПОВЕЩЕНИЙ / СЕРВЕРНЫЙ АРХИВ");
            c.Fill(list.TakeTop(19), MeteoLook.Rule);
            Draw.Text(c, f.Bold, "Время", new Box(list.X + 5, list.Y, 62, 19), MeteoLook.Ink, Align.Left);
            Draw.Text(c, f.Bold, "Автор", new Box(list.X + 73, list.Y, 119, 19), MeteoLook.Ink, Align.Left);
            Draw.Text(c, f.Bold, "Прибор", new Box(list.X + 198, list.Y, 192, 19), MeteoLook.Ink, Align.Left);
            Draw.Text(c, f.Bold, "Наблюдение / сообщение", new Box(list.X + 397, list.Y, list.W - 402, 19), MeteoLook.Ink, Align.Left);
            Box area = new Box(list.X, list.Y + 20, list.W, list.H - 20);
            float textWidth = area.W - 402 - XpLook.ScrollW;
            float content = 0;
            for (int i = 0; i < session.Journal.Count; i++) { journalBlocks[i].Layout(f.Body, textWidth); content += MathF.Max(1, journalBlocks[i].Count) * 15 + 10; }
            Box rows = XpPaint.Scroll(c, ui, 500, area, content, ref journalScroll);
            c.PushClip(rows);
            float y = rows.Y - journalScroll;
            for (int i = session.Journal.Count - 1; i >= 0; i--) {
                MeteoEntry entry = session.Journal[i]; TextBlock text = journalBlocks[i];
                bool alert = entry.Text.StartsWith("ОПОВЕЩЕНИЕ", StringComparison.Ordinal);
                float height = MathF.Max(1, text.Count) * 15 + 10;
                if (y + height >= rows.Y && y < rows.Bottom) {
                    c.Fill(new Box(rows.X, y, rows.W, height), alert ? MeteoLook.WarnFill : i % 2 == 0 ? MeteoLook.Plot : MeteoLook.Zebra);
                    c.Fill(new Box(rows.X, y + height - 1, rows.W, 1), MeteoLook.Rule);
                    Draw.Text(c, f.Body, journalTimes[i] ?? "—", new Box(rows.X + 5, y + 5, 62, 15), MeteoLook.Ink, Align.Left);
                    Draw.Clipped(c, f.Body, entry.AuthorName, new Box(rows.X + 73, y + 5, 119, 15), MeteoLook.Ink, Align.Left);
                    Draw.Clipped(c, f.Body, journalStations[i] ?? "—", new Box(rows.X + 198, y + 5, 192, 15), MeteoLook.Muted, Align.Left);
                    text.Paint(c, f.Body, new Box(rows.X + 397, y + 5, textWidth, text.Height), alert ? MeteoLook.Alarm : MeteoLook.Ink);
                }
                y += height;
            }
            if (!session.HasJournal || session.Journal.Count == 0) { Draw.Text(c, f.Body, session.HasJournal ? "Журнал пуст. Занесите наблюдение в поле ниже." : "Ожидание журнала сервера", rows.TakeTop(42), MeteoLook.Muted, Align.Center); }
            c.PopClip();
            Box form = Panel(c, f, new Box(b.X, b.Bottom - 99, b.W, 99), "НОВАЯ ЗАПИСЬ / РУЧНЫЕ ПОКАЗАНИЯ И НАБЛЮДЕНИЯ");
            var field = new Box(form.X + 2, form.Y + 3, form.W - 207, 26);
            if (noteField.Paint(c, ui, 200, f.Body, field, ReferenceEquals(typing, noteField), now, "Барограф, состояние приборов, наблюдения за небом…")) { typing = noteField; fieldClicked = true; }
            if (noteCount != noteField.Text.Length) { noteCount = noteField.Text.Length; noteCounter = noteCount + " / " + MeteoArchive.EntryTextMax + " знаков"; }
            Draw.Text(c, f.Body, noteCounter, new Box(form.X + 3, field.Bottom + 7, 160, 18), MeteoLook.Muted, Align.Left);
            Draw.Text(c, f.Body, "Запись сохраняется после подтверждения сервера", new Box(form.X + 172, field.Bottom + 7, form.W - 380, 18), MeteoLook.Muted, Align.Left);
            if (Button(c, f, 201, new Box(form.Right - 199, field.Y, 197, 26), session.ActionPending ? "Ожидание сервера…" : "Записать (Enter)", !session.ActionPending && HasStation && noteField.Text.Length > 0, true)) { SubmitNote(); }
        }

        private void ServerView(ICanvas c, XpFaces f, Box b, double now) {
            const float left = 371, top = 292;
            Box rack = Panel(c, f, new Box(b.X, b.Y, left, top), O.RackPresent ? O.RackName : "СТОЙКА НЕДОСТУПНА");
            for (int i = 0; i < RackNames.Length; i++) {
                float y = rack.Y + i * 23;
                c.Fill(new Box(rack.X, y, rack.W, 23), i % 2 == 0 ? MeteoLook.Plot : MeteoLook.Zebra);
                Draw.Text(c, f.Body, RackNames[i], new Box(rack.X + 4, y, 119, 23), MeteoLook.Muted, Align.Left);
                Draw.Clipped(c, f.Bold, rackValues[i] ?? "—", new Box(rack.X + 127, y, rack.W - 131, 23), i < 2 && !Live ? MeteoLook.Alarm : MeteoLook.Ink, Align.Left);
            }
            XpPaint.Progress(c, new Box(rack.X + 4, rack.Y + 199, rack.W - 8, 16), Math.Clamp(fill.Value, 0, 1));
            Draw.Clipped(c, f.Body, storageWarning, new Box(rack.X + 4, rack.Y + 226, rack.W - 8, 25), Live ? MeteoLook.Muted : MeteoLook.Alarm, Align.Left);
            Box devices = Panel(c, f, new Box(b.X + left + 6, b.Y, b.W - left - 6, top), "ПРИБОРЫ НА ЛИНИИ / КАЧЕСТВО ПРИЁМА");
            ReadOnlySpan<float> widths = stackalloc float[] { devices.W - 300, 75, 72, 58, 95 };
            float x = devices.X;
            c.Fill(devices.TakeTop(20), MeteoLook.Rule);
            for (int i = 0; i < 5; i++) { Draw.Text(c, f.Bold, DeviceHeaders[i], new Box(x + 5, devices.Y, widths[i] - 8, 20), MeteoLook.Ink, Align.Left); x += widths[i]; }
            for (int i = 0; i < O.Stations.Count; i++) {
                MeteoStationInfo info = O.Stations[i]; bool manual = info.Kind == MeteoKind.Barograph;
                float y = devices.Y + 21 + i * 19; x = devices.X;
                c.Fill(new Box(x, y, devices.W, 19), info.Id == station ? MeteoLook.Highlight : i % 2 == 0 ? MeteoLook.Plot : MeteoLook.Zebra);
                Draw.Clipped(c, f.Body, info.Name, new Box(x + 5, y, widths[0] - 8, 19), MeteoLook.Ink, Align.Left); x += widths[0];
                Draw.Text(c, f.Body, manual ? "механика" : info.Powered ? "есть" : "нет", new Box(x + 5, y, widths[1] - 8, 19), !manual && !info.Powered ? MeteoLook.Alarm : MeteoLook.Ink, Align.Left); x += widths[1];
                Draw.Text(c, f.Body, manual ? "—" : info.Linked ? "есть" : "нет", new Box(x + 5, y, widths[2] - 8, 19), !manual && !info.Linked ? MeteoLook.Warn : MeteoLook.Ink, Align.Left); x += widths[2];
                Draw.Text(c, f.Body, manual ? "—" : stationIce[i] ?? "—", new Box(x + 5, y, widths[3] - 8, 19), MeteoLook.Ink, Align.Left); x += widths[3];
                Draw.Text(c, f.Body, stationAges[i] ?? "—", new Box(x + 5, y, widths[4] - 8, 19), MeteoLook.Muted, Align.Left);
            }
            if (O.Stations.Count == 0) { Draw.Text(c, f.Body, "Приборов в зоне стойки нет", new Box(devices.X, devices.Y + 30, devices.W, 24), MeteoLook.Muted, Align.Center); }
            Box tools = Panel(c, f, new Box(b.X, b.Y + top + 6, left, b.H - top - 6), "СВОДКА / СОХРАНЕНИЕ НА ДИСК");
            bulletinHelp.Paint(c, f.Body, new Box(tools.X + 4, tools.Y + 5, tools.W - 8, 100), MeteoLook.Ink);
            Draw.Text(c, f.Bold, "Источник сводки", new Box(tools.X + 4, tools.Y + 115, tools.W - 8, 18), MeteoLook.Ink, Align.Left);
            Draw.Clipped(c, f.Body, stationCaption, new Box(tools.X + 4, tools.Y + 138, tools.W - 8, 22), MeteoLook.Ink, Align.Left);
            Draw.Clipped(c, f.Body, ageText, new Box(tools.X + 4, tools.Y + 165, tools.W - 8, 18), SourceStale ? MeteoLook.Alarm : MeteoLook.Muted, Align.Left);
            if (Button(c, f, 210, new Box(tools.X + 4, tools.Y + 201, tools.W - 8, 27), session.ActionPending ? "Ожидание сервера…" : "Бюллетень в папку «Метео»", HasStation && !SourceStale && !session.ActionPending, true)) {
                if (session.Bulletin(station)) { SetStatus("Запрошено сохранение бюллетеня. Ожидание сервера.", MeteoLook.Ink); }
            }
            if (Button(c, f, 211, new Box(tools.X + 4, tools.Y + 237, tools.W - 8, 24), "Обновить состояние линии")) { session.Refresh(); }
            Draw.Clipped(c, f.Body, "Сервер подтверждает результат каждой операции", new Box(tools.X + 4, tools.Bottom - 25, tools.W - 8, 20), MeteoLook.Muted, Align.Left);
            Box rules = Panel(c, f, new Box(b.X + left + 6, b.Y + top + 6, b.W - left - 6, b.H - top - 6), "ХРАНЕНИЕ, ЛИНИЯ И ВОССТАНОВЛЕНИЕ ДАННЫХ");
            float fy = rules.Y + 6;
            for (int i = 0; i < serviceRules.Length; i++) {
                TextBlock text = serviceRules[i]; text.Layout(f.Body, rules.W - 28);
                c.Fill(new Box(rules.X + 5, fy + 4, 6, 6), MeteoLook.Pressure);
                text.Paint(c, f.Body, new Box(rules.X + 20, fy, rules.W - 28, text.Height), MeteoLook.Ink);
                fy += text.Height + 18;
            }
        }
    }
}
