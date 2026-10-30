using System;
using System.Collections.Generic;
using ScpItdMod.Motion;
using ScpItdMod.Paper;
using ScpItdMod.Pc;
using ScpItdMod.Ui;
using ScpItdMod.Weather;
using ScpItdMod.Xp;
using Draw = ScpItdMod.Ui.Paint;
using Vec = ScpItdMod.Core.Vec2;

namespace ScpItdMod.Meteo {
    public enum MeteoTab : byte { Overview, Charts, Forecast, Journal, Server }

    public sealed partial class MeteoScreen : IXpFullscreen, IXpTyping, IXpSessionLifecycle {
        private static readonly uint[] Spans = { 600, 1800, 7200, 14400 };
        private static readonly string[] SpanNames = { "10 мин", "30 мин", "2 часа", "4 часа" };
        private static readonly MotionTransition Instrument = MotionTransition.Spring(12, 1);
        private readonly MeteoSession session = MeteoClient.Session;
        private readonly UiState ui = new UiState();
        private readonly Dictionary<int, ControlMotion> controls = new Dictionary<int, ControlMotion>(96);
        private readonly MeteoPlotData plotData = new MeteoPlotData();
        private readonly RetroField noteField = new RetroField(MeteoArchive.EntryTextMax);
        private readonly RetroField alertField = new RetroField(MeteoWire.AlertMax, true);
        private MotionScope scope;
        private MotionHandle<float> temperature, pressure, visibility, snow, ice, confidence, fill, eta, cursor, alarmMark;
        private MotionHandle<Vec> windVector;
        private readonly MotionHandle<float>[] probability = new MotionHandle<float>[6];
        private MotionSequence alarmSequence;
        private MotionPlayback alarmPlayback;
        private MotionVisual stationMenu;
        private MeteoTab tab;
        private bool primed, done, selectorOpen, selectionPending, pendingDirect, stationOperational;
        private ushort station;
        private int spanIndex = 1, historyIndex = -1, alertLevel = 2;
        private uint seenOverview = uint.MaxValue, seenSeries = uint.MaxValue, seenEpoch;
        private double historyTime = double.NaN, statusAt, readoutTime = double.NaN, lastAckAt = double.NegativeInfinity;
        private double pendingTime;
        private long clockSecond = long.MinValue;
        private string status = "", clockText = "", ageText = "", sentNote;
        private Rgba statusTone = MeteoLook.Ink;
        private XpShell shell;
        private RetroField typing;
        private MeteoAlert previousAlert;

        public string Title => "Метеоконтроль / АМС-М";
        public string Icon => "meteo16";
        public InkEdit Edit => typing?.Edit;
        public bool Done => done;
        private MeteoOverview O => session.Overview;
        private bool Live => session.HasOverview && O.RackPresent && O.RackPowered;
        private bool HasStation => Live && O.Selected != 0 && O.Selected == station && O.HasLatest;
        private bool PlotReady => session.HasSeries && session.ReturnedSpan == Spans[spanIndex] && plotData.Station == station && plotData.Count > 0;
        private bool Historical => !double.IsNaN(historyTime);
        private bool HasReading => Live && station != 0 && O.Selected == station && (Historical ? PlotReady && historyIndex >= 0 : O.HasLatest);
        private ref readonly MeteoSample Reading { get { if (Historical && historyIndex >= 0) { return ref plotData.At(historyIndex); } return ref session.Overview.Latest; } }
        private bool SourceStale => !HasStation || shell.Now - session.OverviewAt > 6 || O.Now - O.Latest.Time > 30 || !stationOperational;
        private bool Stale => !Historical && SourceStale;

        public bool Command(XpShell s, char control) {
            if (control != '\r' && control != '\n') { return false; }
            if (ReferenceEquals(typing, noteField)) { SubmitNote(); return true; }
            if (ReferenceEquals(typing, alertField)) { SubmitAlert(); return true; }
            return false;
        }
        public bool Back() {
            if (selectorOpen) { SetSelector(false); return true; }
            if (typing != null) { typing = null; return true; }
            if (Historical) { SelectTime(double.NaN, false); return true; }
            if (tab != MeteoTab.Overview) { SwitchTab(MeteoTab.Overview); return true; }
            return false;
        }
        public bool WantsClose(XpShell s, XpWindow self) => true;
        public void Paint(ICanvas c, UiState state, XpFaces f, Box client, XpShell s, XpWindow self) { }
        public void Closed(XpShell s) { ReleaseMotion(); session.End(); done = true; }
        public void OnSessionSuspending(XpShell s) { ReleaseMotion(); typing = null; }
        public void OnSessionResumed(XpShell s) { if (session.Computer != s.Pc.Computer) { session.Begin(s.Pc.Computer); } else { session.Refresh(); } primed = false; }
        public void Start(XpShell s) { shell = s; done = false; session.Begin(s.Pc.Computer); CreateMotion(); }

        private void ReleaseMotion() {
            alarmPlayback?.Cancel(); alarmPlayback = null;
            stationMenu?.Dispose(); stationMenu = null;
            scope?.Dispose(); scope = null; controls.Clear();
        }
        private void CreateMotion() {
            ReleaseMotion(); scope = MotionClient.Runtime.Scope();
            temperature = scope.Scalar(); pressure = scope.Scalar(); visibility = scope.Scalar();
            snow = scope.Scalar(); ice = scope.Scalar(); confidence = scope.Scalar(); fill = scope.Scalar(); eta = scope.Scalar();
            windVector = scope.Vector(); cursor = scope.Scalar(1); alarmMark = scope.Scalar(1);
            for (int i = 0; i < probability.Length; i++) { probability[i] = scope.Scalar(); }
            alarmSequence = new MotionSequenceBuilder().To(alarmMark, 0.35f, MotionTransition.Tween(0.16f))
                .To(alarmMark, 1f, MotionTransition.Tween(0.3f), 0.16).Compile();
            stationMenu = new MotionVisual(MotionClient.Runtime, new Box(94, 76, 290, 160), true);
            primed = false; selectorOpen = false; previousAlert = MeteoAlert.None; seenOverview = seenSeries = uint.MaxValue;
        }
        private void SwitchTab(MeteoTab next) {
            if (next == tab) { return; }
            tab = next; typing = null; SetSelector(false); MeteoClient.Click();
        }
        private void SetStatus(string text, Rgba tone) { status = text; statusAt = shell?.Now ?? 0; statusTone = tone; }
        private void SelectStation(ushort next) {
            if (station == next) { SetSelector(false); return; }
            station = next; primed = false; historyTime = double.NaN; historyIndex = -1;
            stationOperational = false; stationCaption = StationName(next);
            plotData.Clear(); seenOverview = seenSeries = uint.MaxValue; readoutTime = double.NaN;
            session.Refresh(); SetSelector(false);
        }
        private void SelectTime(double time, bool direct) {
            pendingTime = time; pendingDirect = direct; selectionPending = true;
        }
        private void CommitSelection() {
            double time = pendingTime; bool direct = pendingDirect; selectionPending = false;
            historyTime = time; historyIndex = Historical && PlotReady ? plotData.Nearest(time) : -1;
            float ratio = !Historical || !PlotReady ? 1 : (float)Math.Clamp((time - plotData.From) / (plotData.To - plotData.From), 0, 1);
            if (direct) { cursor.Snap(ratio); } else { cursor.To(ratio, MotionPresets.Layout); }
            readoutTime = double.NaN;
        }
        private bool StationOperational() {
            for (int i = 0; i < O.Stations.Count; i++) { if (O.Stations[i].Id == station) { return O.Stations[i].Powered && O.Stations[i].Linked; } }
            return false;
        }

        public void Frame(ICanvas c, XpFaces f, in UiInput input, XpShell s) {
            shell = s;
            if (scope == null || !scope.Alive) { CreateMotion(); }
            if (session.Computer == 0 && s.Pc.Computer != 0) { session.Begin(s.Pc.Computer); }
            if (MotionClient.Runtime.ReducedMotion && alarmPlayback?.Active == true) { alarmPlayback.Cancel(true); alarmMark.Snap(1); }
            ui.Begin(input);
            session.Poll(s.Now, station, Spans[spanIndex], tab != MeteoTab.Server, tab == MeteoTab.Overview || tab == MeteoTab.Journal);
            Absorb(s.Now);
            c.Fill(new Box(0, 0, MeteoLook.W, MeteoLook.H), MeteoLook.Page);
            Chrome(c, f, s.Now);
            var page = new Box(7, 86, MeteoLook.W - 14, MeteoLook.H - 114);
            fieldClicked = false;
            UiInput bodyInput = ui.In;
            if (selectorOpen || stationMenu.Opacity.Value > .01f) { ui.In = UiInput.None; }
            c.PushClip(page);
            Content(c, f, page, s.Now);
            c.PopClip();
            ui.In = bodyInput;
            if (selectorOpen || stationMenu.Opacity.Value > 0.01f) { StationSelector(c, f); }
            if (input.Pressed && !fieldClicked) { typing = null; }
            StatusBar(c, f, new Box(0, MeteoLook.H - 22, MeteoLook.W, 22), s.Now);
            AckToast(s.Now);
            if (input.Released) { ui.Active = 0; }
        }

        private void Absorb(double now) {
            if (seenEpoch != session.Epoch) {
                seenEpoch = session.Epoch; station = 0; plotData.Clear(); historyTime = double.NaN; historyIndex = -1;
                primed = false; selectionPending = false; seenOverview = seenSeries = uint.MaxValue; lastAckAt = double.NegativeInfinity; sentNote = null;
            }
            if (!session.HasOverview) { return; }
            if (station == 0) { station = O.Selected; }
            if (session.HasSeries && session.Series.Station == station && session.ReturnedSpan == Spans[spanIndex] && seenSeries != session.SeriesRevision) {
                seenSeries = session.SeriesRevision;
                plotData.Load(session.Series); CacheAxes();
                if (Historical && !selectionPending) { pendingTime = historyTime; pendingDirect = ui.Active == 450 && ui.In.Down; selectionPending = true; }
            }
            if (selectionPending) { CommitSelection(); }
            bool changed = seenOverview != session.OverviewRevision;
            seenOverview = session.OverviewRevision;
            if (changed) { stationOperational = StationOperational(); CacheOperationText(); CacheForecastDetails(); CacheServiceText(); }
            long second = (long)Math.Floor(now);
            if (clockSecond != second || changed) {
                clockSecond = second; clockText = Clock(O.Now); CacheStations(); CacheAlertClock();
                ageText = HasStation ? "ПРИЁМ: " + MeteoText.Duration((float)Math.Max(0, O.Now - O.Latest.Time)) : "ПРИЁМА НЕТ";
            }
            if (session.HasJournal && (seenJournal != session.JournalRevision || changed)) { CacheJournal(); }
            if (!HasReading) { ClearReadings(); }
            else if (changed || readoutTime != Reading.Time || readoutStale != Stale) {
                ref readonly MeteoSample sample = ref Reading;
                readoutTime = sample.Time; CacheReadings(sample);
                Retarget(temperature, sample.Temperature);
                Retarget(pressure, sample.Pressure); Retarget(visibility, MathF.Log10(MathF.Max(1, sample.Visibility)));
                Retarget(snow, sample.Snow); Retarget(ice, sample.Ice);
                Vec vector = sample.WindValid ? MeteoPlotMath.Velocity(sample.Wind, sample.Direction) : Vec.Zero;
                if (!primed || !sample.WindValid) { windVector.Snap(vector); } else { windVector.To(vector, Instrument); }
            }
            MeteoForecast forecast = O.Forecast;
            Retarget(confidence, forecast.Valid ? forecast.Confidence : 0);
            Retarget(fill, O.Capacity > 0 ? O.Used / (float)O.Capacity : 0);
            Retarget(eta, forecast.Valid ? forecast.EtaMid : 0);
            for (int i = 0; i < 6; i++) { Retarget(probability[i], forecast.Valid ? forecast.Probability[i] : 0); }
            if (forecast.Valid && forecast.Alert != previousAlert) {
                previousAlert = forecast.Alert;
                alarmPlayback?.Cancel();
                if (forecast.Alert >= MeteoAlert.Warning && !MotionClient.Runtime.ReducedMotion) { alarmPlayback = scope.Play(alarmSequence); }
                else { alarmMark.Snap(1); }
            }
            primed = true;
        }
        private void Retarget(MotionHandle<float> handle, float value) { if (!primed) { handle.Snap(value); } else { handle.To(value, Instrument); } }
        private double StationDay(double time) => MeteoWorld.Active() ? MeteoWorld.Day(Math.Max(0, time)) : O.Day;
        private string Clock(double time) => MeteoText.Clock(StationDay(time));
        private void AckToast(double now) {
            if (session.AckAt <= lastAckAt || session.ActionState == MeteoActionState.Pending || session.AckAt <= 0) { return; }
            lastAckAt = session.AckAt;
            if (session.Action == MeteoAction.Note && session.ActionState == MeteoActionState.Succeeded && noteField.Text == sentNote) { noteField.Clear(); }
            sentNote = null;
            SetStatus(session.ActionState == MeteoActionState.Unconfirmed ? "Подтверждение не получено. Проверьте журнал перед повтором." : session.AckLabel + ": " + MeteoSession.Explain(session.LastAck), session.LastAck == MeteoError.Ok ? MeteoLook.Ink : MeteoLook.Warn);
        }
        private void Content(ICanvas c, XpFaces f, Box body, double now) {
            if (tab == MeteoTab.Server && session.HasOverview) { ServerView(c, f, body, now); return; }
            if (!Live || O.Selected == 0) {
                Unavailable(c, f, body, now);
                return;
            }
            switch (tab) {
                case MeteoTab.Overview: Overview(c, f, body, now); break;
                case MeteoTab.Charts: Charts(c, f, body, now); break;
                case MeteoTab.Forecast: Forecast(c, f, body, now); break;
                case MeteoTab.Journal: Journal(c, f, body, now); break;
                case MeteoTab.Server: ServerView(c, f, body, now); break;
            }
        }
        private void Unavailable(ICanvas c, XpFaces f, in Box body, double now) {
            string title, detail;
            if (!session.HasOverview) { title = session.OverviewError == MeteoError.Invalid ? "Метеоданные недоступны" : "Ожидание ответа сервера"; detail = session.OverviewError == MeteoError.Invalid ? "Атмосферная модель доступна на карте «Восток-11»." : "Подключение к архиву наблюдений. Проверьте линию и стойку СХ-4."; }
            else if (!O.RackPresent) { title = "Нет связи со стойкой СХ-4"; detail = "Проверьте серверную стойку, питание и линию связи в пределах 120 м."; }
            else if (!O.RackPowered) { title = "Сервер обесточен"; detail = "Архив сохранён на накопителях. Запись и чтение остановлены."; }
            else { title = "Нет подключённой станции"; detail = "Установите АМС-М снаружи и проверьте питание и линию станции."; }
            Box box = Panel(c, f, new Box(body.X + 85, body.Y + 170, body.W - 170, 115), "СОСТОЯНИЕ ПОДКЛЮЧЕНИЯ");
            Draw.Text(c, f.Header, title, new Box(box.X + 14, box.Y + 12, box.W - 28, 24), MeteoLook.Ink, Align.Left);
            Draw.Clipped(c, f.Body, detail, new Box(box.X + 14, box.Y + 46, box.W - 28, 32), MeteoLook.Muted, Align.Left);
        }
        private void SubmitNote() {
            string text = noteField.Text.Trim();
            if (text.Length == 0 || !HasStation || session.ActionPending) { return; }
            if (session.Note(station, text)) { sentNote = noteField.Text; SetStatus("Запись отправлена, ожидание подтверждения", MeteoLook.Ink); }
        }
        private void SubmitAlert() {
            if (!Live || session.ActionPending || alertCooling) { return; }
            string text = alertField.Text.Trim();
            if (text.Length == 0) { SetStatus("Введите текст оповещения", MeteoLook.Warn); return; }
            if (session.Alert((byte)alertLevel, text)) { SetStatus("Оповещение отправлено, ожидание подтверждения", MeteoLook.Ink); }
        }
    }
}
