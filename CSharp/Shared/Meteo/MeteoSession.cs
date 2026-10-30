using System;
using System.Collections.Generic;
using ScpItdMod.Net;

namespace ScpItdMod.Meteo {
    public enum MeteoAction : byte { None, Note, Bulletin, Alert }
    public enum MeteoActionState : byte { Idle, Pending, Succeeded, Rejected, Unconfirmed }

    public sealed class MeteoSession {
        public const double OverviewEvery = 1.5, JournalEvery = 6, SeriesEvery = 5, ActionTimeout = 12;
        private struct Query {
            public uint Token;
            private double next, sent;
            public bool Due(double now) => now >= next && (Token == 0 || now - sent >= ActionTimeout);
            public void Start(uint token, double now, double interval) { Token = token; sent = now; next = now + interval; }
            public bool Matches(uint token) => Token != 0 && token == Token;
            public void Complete() => Token = 0;
            public void Refresh() { next = 0; Token = 0; }
        }
        public MeteoOverview Overview { get; private set; } = new MeteoOverview();
        public MeteoSeries Series { get; private set; } = new MeteoSeries();
        public List<MeteoEntry> Journal { get; private set; } = new List<MeteoEntry>(MeteoWire.MaxEntries);
        private MeteoOverview incomingOverview = new MeteoOverview();
        private MeteoSeries incomingSeries = new MeteoSeries();
        private List<MeteoEntry> incomingJournal = new List<MeteoEntry>(MeteoWire.MaxEntries);
        public bool HasOverview { get; private set; }
        public bool HasSeries { get; private set; }
        public bool HasJournal { get; private set; }
        public MeteoError OverviewError { get; private set; }
        public MeteoError LastAck { get; private set; }
        public double OverviewAt { get; private set; }
        public double SeriesAt { get; private set; }
        public double JournalAt { get; private set; }
        public double AckAt { get; private set; }
        public uint SeriesSpan { get; private set; }
        public uint ReturnedSpan { get; private set; }
        public uint OverviewRevision { get; private set; }
        public uint SeriesRevision { get; private set; }
        public uint JournalRevision { get; private set; }
        public uint Epoch { get; private set; }
        public string AckLabel { get; private set; } = "";
        public ushort Computer { get; private set; }
        public MeteoAction Action { get; private set; }
        public MeteoActionState ActionState { get; private set; }
        public bool ActionPending => ActionState == MeteoActionState.Pending;
        public Action<ReadOnlyMemory<byte>> Send = _ => { };
        private uint token, ackToken;
        private Query overviewQuery, seriesQuery, journalQuery;
        private ushort requestedStation, overviewStation;
        private double actionSince, lastNow;
        private readonly WireWriter writer = new WireWriter(MeteoWire.MaxRequest);

        public void Begin(ushort computer) {
            Computer = computer; HasOverview = HasSeries = HasJournal = false;
            OverviewError = LastAck = MeteoError.Ok; AckLabel = ""; AckAt = 0;
            overviewQuery = seriesQuery = journalQuery = default; ackToken = 0;
            requestedStation = overviewStation = 0; lastNow = 0;
            OverviewAt = SeriesAt = JournalAt = 0;
            SeriesSpan = ReturnedSpan = OverviewRevision = SeriesRevision = JournalRevision = 0;
            Action = MeteoAction.None; ActionState = MeteoActionState.Idle;
            unchecked { Epoch++; }
        }

        public void End() { Computer = 0; HasOverview = HasSeries = HasJournal = false; overviewQuery = seriesQuery = journalQuery = default; ackToken = 0; ActionState = MeteoActionState.Idle; }
        private uint Token() { if (++token == 0) { token++; } return token; }

        public void Poll(double now, ushort station, uint span, bool wantSeries, bool wantJournal) {
            if (!double.IsFinite(now) || now < 0) throw new ArgumentOutOfRangeException(nameof(now));
            lastNow = now;
            if (Computer == 0) { return; }
            if (station != overviewStation) { overviewStation = station; overviewQuery.Refresh(); }
            if (ActionPending && now - actionSince >= ActionTimeout) {
                ackToken = 0; ActionState = MeteoActionState.Unconfirmed; LastAck = MeteoError.NoLink; AckAt = now;
                overviewQuery.Refresh(); journalQuery.Refresh();
            }
            if (overviewQuery.Due(now)) {
                overviewQuery.Start(Token(), now, OverviewEvery);
                MeteoWire.Request(writer, MeteoOp.Overview, overviewQuery.Token, Computer); writer.U16(station); Send(writer.Span.ToArray());
            }
            bool changed = span != SeriesSpan || requestedStation != Overview.Selected;
            if (wantSeries && (changed || seriesQuery.Due(now)) &&
                HasOverview && Overview.Selected != 0 && (station == 0 || station == Overview.Selected)) {
                if (changed) { HasSeries = false; ReturnedSpan = 0; SeriesRevision++; }
                seriesQuery.Start(Token(), now, SeriesEvery); SeriesSpan = span; requestedStation = Overview.Selected;
                MeteoWire.Request(writer, MeteoOp.Series, seriesQuery.Token, Computer); writer.U16(requestedStation); writer.U32(span); writer.U16(MeteoWire.MaxPoints); Send(writer.Span.ToArray());
            }
            if (wantJournal && journalQuery.Due(now)) {
                journalQuery.Start(Token(), now, JournalEvery);
                MeteoWire.Request(writer, MeteoOp.Journal, journalQuery.Token, Computer); Send(writer.Span.ToArray());
            }
        }

        public void Refresh() { overviewQuery.Refresh(); seriesQuery.Refresh(); journalQuery.Refresh(); }

        private bool Prepare(MeteoAction action, MeteoOp op, string label) {
            if (Computer == 0 || ActionPending) { return false; }
            ackToken = Token(); Action = action; ActionState = MeteoActionState.Pending; actionSince = lastNow;
            AckLabel = label; AckAt = 0;
            MeteoWire.Request(writer, op, ackToken, Computer);
            return true;
        }

        public bool Note(ushort station, string text) {
            if (!Prepare(MeteoAction.Note, MeteoOp.Note, "Запись")) { return false; }
            writer.U16(station); writer.Text(text); Send(writer.Span.ToArray()); return true;
        }
        public bool Bulletin(ushort station) {
            if (!Prepare(MeteoAction.Bulletin, MeteoOp.Bulletin, "Бюллетень")) { return false; }
            writer.U16(station); Send(writer.Span.ToArray()); return true;
        }
        public bool Alert(byte level, string text) {
            if (!Prepare(MeteoAction.Alert, MeteoOp.Alert, "Оповещение")) { return false; }
            writer.U8(level); writer.Text(text); Send(writer.Span.ToArray()); return true;
        }

        public bool Apply(MeteoReply kind, uint replyToken, MeteoError error, ref WireReader reader, double now) {
            if (!double.IsFinite(now) || now < 0) throw new ArgumentOutOfRangeException(nameof(now));
            if (Computer == 0) { return false; }
            switch (kind) {
                case MeteoReply.Overview:
                    if (!overviewQuery.Matches(replyToken) || error is not (MeteoError.Ok or MeteoError.NoLink)) { return false; }
                    if (!MeteoWire.Read(ref reader, incomingOverview)) { return false; }
                    if (Overview.Selected != incomingOverview.Selected || error != MeteoError.Ok) { HasSeries = false; ReturnedSpan = 0; SeriesRevision++; seriesQuery.Refresh(); }
                    (Overview, incomingOverview) = (incomingOverview, Overview);
                    overviewQuery.Complete(); OverviewError = error; HasOverview = true; OverviewAt = now; OverviewRevision++;
                    return true;
                case MeteoReply.Series:
                    if (!seriesQuery.Matches(replyToken) || error != MeteoError.Ok) { return false; }
                    if (!MeteoWire.Read(ref reader, incomingSeries) || incomingSeries.Station != requestedStation) { return false; }
                    (Series, incomingSeries) = (incomingSeries, Series);
                    seriesQuery.Complete(); HasSeries = true; SeriesAt = now; ReturnedSpan = SeriesSpan; SeriesRevision++;
                    return true;
                case MeteoReply.Journal:
                    if (!journalQuery.Matches(replyToken) || error != MeteoError.Ok) { return false; }
                    if (!MeteoWire.Read(ref reader, incomingJournal)) { return false; }
                    (Journal, incomingJournal) = (incomingJournal, Journal);
                    journalQuery.Complete(); HasJournal = true; JournalAt = now; JournalRevision++;
                    return true;
                case MeteoReply.Ack:
                    if (!reader.End || reader.Failed) { return false; }
                    if (replyToken == ackToken && ackToken != 0) {
                        LastAck = error; AckAt = now; ActionState = error == MeteoError.Ok ? MeteoActionState.Succeeded : MeteoActionState.Rejected;
                        if (error == MeteoError.Ok) { journalQuery.Refresh(); overviewQuery.Refresh(); }
                        ackToken = 0; return true;
                    }
                    if (overviewQuery.Matches(replyToken)) { overviewQuery.Complete(); OverviewError = error; return true; }
                    if (seriesQuery.Matches(replyToken)) { seriesQuery.Complete(); HasSeries = false; ReturnedSpan = 0; SeriesRevision++; return true; }
                    if (journalQuery.Matches(replyToken)) { journalQuery.Complete(); HasJournal = false; JournalRevision++; return true; }
                    return false;
            }
            return false;
        }

        public static string Explain(MeteoError error) => error switch {
            MeteoError.Ok => "Выполнено", MeteoError.NoPower => "Нет питания", MeteoError.NoLink => "Нет связи с сервером",
            MeteoError.Far => "Слишком далеко от компьютера", MeteoError.Denied => "Доступ запрещён", MeteoError.Invalid => "Данные отклонены",
            MeteoError.Full => "Хранилище сервера заполнено", MeteoError.Rate => "Слишком часто", MeteoError.Busy => "Оповещение уже передано, повторите позже", _ => "Ошибка"
        };
    }
}
