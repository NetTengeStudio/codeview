using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;
using ScpItdMod.Auth;
using ScpItdMod.Core;
using ScpItdMod.Net;
using ScpItdMod.Pc;
using ScpItdMod.Weather;

namespace ScpItdMod.Meteo {
    public static class MeteoHost {
        public const double SampleInterval = MeteoStationModel.SampleInterval;
        private const float SnowStake = 2.5f;
        public const float LinkRange = 12000, ReadRange = 260, DeiceRange = 190;
        private const int BufferLimit = 30;

        private sealed class Unit {
            public Item Item;
            public MeteoKind Kind;
            public MeteoStationModel Model;
            public double Next;
            public double Last;
            public double Advanced;
            public bool Linked;
            public readonly List<MeteoSample> Buffer = new List<MeteoSample>(BufferLimit);
            public readonly List<MeteoSample> Trace = new List<MeteoSample>(240);
            public bool Powered;
            public ServerRack Rack;
        }

        private sealed class Peer {
            public readonly RequestSessions Requests = new RequestSessions();
            public Gate Gate;
            public double NextNote;
        }

        private static readonly Dictionary<ushort, Unit> units = new Dictionary<ushort, Unit>();
        private static readonly Dictionary<ulong, Peer> peers = new Dictionary<ulong, Peer>();
        private static readonly List<ushort> gone = new List<ushort>();
        private static readonly List<MeteoSample> scratch = new List<MeteoSample>(MeteoWire.MaxPoints + 8);
        private static readonly List<MeteoGap> gaps = new List<MeteoGap>(MeteoWire.MaxGaps + 2);
        private static readonly WireWriter reply = new WireWriter(MeteoWire.MaxReply);
        private static readonly Identifier StationTag = MeteoWire.StationTag.ToIdentifier(), BarographTag = MeteoWire.BarographTag.ToIdentifier();
        private static MeteoForecaster forecaster;
        private static WeatherConfig forecasterConfig;
        private static int stationWatch = -1, barographWatch = -1;
        private static double alertAt = double.NegativeInfinity, nextHousekeeping;
        private static int bulletins;
        private static readonly Dictionary<(ushort, ushort), (double, MeteoForecast)> forecasts = new Dictionary<(ushort, ushort), (double, MeteoForecast)>();

        public static void Attach() {
            ServerRacks.Attach();
            stationWatch = Tagged.Watch(StationTag); barographWatch = Tagged.Watch(BarographTag);
            ModNet.ReceiveBytes(MeteoWire.Channel, MeteoWire.MaxRequest, OnRequest);
            ModCommands.Register("scpitd_meteo", "server: weather stations, racks and archives", _ => Report());
        }

        public static void RoundStart() { Clear(); ServerRacks.Clear(); }

        public static void Clear() {
            units.Clear(); peers.Clear(); forecasts.Clear(); gone.Clear();
            alertAt = double.NegativeInfinity; nextHousekeeping = 0; bulletins = 0; forecaster = null; forecasterConfig = null;
            scratch.Clear(); gaps.Clear();
        }

        private static void Report() {
            ModLog.Message($"meteo stations {units.Count}, racks {ServerRacks.Count}");
            foreach (var pair in ServerRacks.All) ModLog.Message($"  rack {pair.Key} disks {pair.Value.Disks} used {pair.Value.Archive.Used}/{pair.Value.Archive.Capacity} written {pair.Value.Archive.Written} overwritten {pair.Value.Archive.Overwritten}");
        }

        public static void Step(float dt) {
            if (stationWatch < 0 || !WeatherService.Active) { return; }
            double now = WeatherService.Clock.Seconds;
            double wall = Timing.TotalTime;
            if (wall >= nextHousekeeping) { nextHousekeeping = wall + 1; Housekeeping(); }
            WeatherState state = WeatherService.Current; WeatherConfig config = WeatherService.Config;
            foreach (var pair in units) {
                Unit u = pair.Value;
                Item item = u.Item;
                float elapsed = (float)Math.Max(0, now - u.Advanced); u.Advanced = now;
                if (item.Removed || item.ParentInventory != null) { u.Powered = false; u.Linked = false; continue; }
                u.Powered = u.Kind == MeteoKind.Barograph || SwipeWorld.Powered(item);
                u.Linked = u.Powered && u.Kind == MeteoKind.Station && u.Rack?.Online == true;
                if (WeatherService.Clock.Frozen) continue;
                float exposure = WeatherService.Underfoot(item.WorldPosition, SnowStake, out float depth, out _);
                u.Model.Advance(state, exposure, u.Powered && u.Kind == MeteoKind.Station, elapsed);
                if (now < u.Next) continue;
                u.Next = MeteoStationModel.NextSample(now, u.Next);
                if (!u.Powered) continue;
                MeteoSample sample = u.Model.Read(now, pair.Key, state, config, exposure, depth);
                u.Last = now;
                if (u.Kind == MeteoKind.Barograph) {
                    if (u.Trace.Count >= 240) { u.Trace.RemoveAt(0); }
                    u.Trace.Add(sample); continue;
                }
                Deliver(u, sample);
            }
        }

        private static void Deliver(Unit u, in MeteoSample sample) {
            ServerRack rack = RackFor(u.Item);
            u.Rack = rack; u.Linked = rack != null;
            if (rack == null) {
                if (u.Buffer.Count >= BufferLimit) { u.Buffer.RemoveAt(0); }
                u.Buffer.Add(sample); return;
            }
            for (int i = 0; i < u.Buffer.Count; i++) { rack.Archive.Add(u.Buffer[i]); }
            u.Buffer.Clear();
            rack.Archive.Add(sample);
        }

        private static void Housekeeping() {
            SyncTagged(stationWatch, MeteoKind.Station); SyncTagged(barographWatch, MeteoKind.Barograph);
            gone.Clear();
            foreach (var pair in units) if (pair.Value.Item.Removed || !pair.Value.Item.HasTag(pair.Value.Kind == MeteoKind.Station ? StationTag : BarographTag)) { gone.Add(pair.Key); }
            for (int i = 0; i < gone.Count; i++) { units.Remove(gone[i]); }
            foreach (var pair in units) pair.Value.Rack = pair.Value.Kind == MeteoKind.Station ? RackFor(pair.Value.Item) : null;
        }

        private static void SyncTagged(int watch, MeteoKind kind) {
            IReadOnlyList<Item> list = Tagged.Of(watch);
            for (int i = 0; i < list.Count; i++) {
                Item item = list[i];
                if (!units.TryGetValue(item.ID, out Unit unit) || !ReferenceEquals(unit.Item, item) || unit.Kind != kind) {
                    double now = WeatherService.Clock.Seconds;
                    units[item.ID] = new Unit { Item = item, Kind = kind, Next = now, Advanced = now, Model = new MeteoStationModel((uint)item.ID * 2654435761u ^ WeatherService.Director.Seed) };
                }
            }
        }

        private static ServerRack RackFor(Item from, bool requirePower = true) => ServerRacks.Nearest(from, requirePower);

        public static void ReadInstrument(Item item, Character who) {
            if (who == null || !WeatherService.Active || !units.TryGetValue(item.ID, out Unit u) || Vector2.DistanceSquared(who.WorldPosition, item.WorldPosition) > ReadRange * ReadRange) { return; }
            Client client = ClientOf(who);
            if (client == null) { return; }
            string text;
            if (u.Kind == MeteoKind.Barograph) {
                if (u.Trace.Count < 3) { text = "Барограф: лента ещё чистая."; }
                else {
                    MeteoSample latest = u.Trace[^1];
                    int back = Math.Min(u.Trace.Count - 1, 18); MeteoSample old = u.Trace[u.Trace.Count - 1 - back];
                    float dt = (float)Math.Max(1, latest.Time - old.Time); float slope = (latest.Pressure - old.Pressure) / dt * 60;
                    text = "Барограф: " + MeteoText.Number(MeteoPhysics.Quantize(latest.Pressure, .5f)) + " гПа, " + MeteoText.Trend(MeteoForecaster.TrendOf(slope)) + " (" + MeteoText.Signed(slope) + " гПа/мин за " + MeteoText.Duration((float)dt) + ").";
                }
            } else if (!u.Powered || u.Last == 0) { text = "Метеостанция не работает: " + (u.Powered ? "нет данных." : "нет питания."); }
            else {
                MeteoSample s = LastOf(u);
                text = "АМС: " + MeteoText.Signed(s.Temperature) + " °C, ветер " + (s.WindValid ? MeteoPhysics.Compass(s.Direction) + " " + MeteoText.Number(s.Wind) + " м/с" : "-") + ", " + MeteoText.Number(s.Pressure) + " гПа, видимость " + MeteoText.Number(s.Visibility, "0") + " м, лёд " + MeteoText.Number(s.Ice * 100, "0") + "%.";
            }
            SendReading(client, text);
        }

        private static MeteoSample LastOf(Unit u) {
            ServerRack r = RackFor(u.Item, false);
            if (r != null && r.Archive.Latest(u.Item.ID, out MeteoSample s) && WeatherService.Clock.Seconds - s.Time < 60) { return s; }
            return u.Buffer.Count > 0 ? u.Buffer[^1] : default;
        }

        public static void Deice(Item tool, Character who, Vector2 target, float dt) {
            if (who == null || !WeatherService.Active) { return; }
            foreach (var pair in units) {
                Unit u = pair.Value;
                if (u.Kind != MeteoKind.Station || u.Item.Removed) { continue; }
                if (Vector2.DistanceSquared(u.Item.WorldPosition, target) > DeiceRange * DeiceRange || Vector2.DistanceSquared(u.Item.WorldPosition, who.WorldPosition) > (DeiceRange + 90) * (DeiceRange + 90)) { continue; }
                float before = u.Model.Ice; u.Model.Deice(MeteoStationModel.DeiceAmount(Math.Min(dt, .1f)));
                if (before > .05f && u.Model.Ice <= .05f && ClientOf(who) is Client c) { SendReading(c, "Датчики станции очищены от льда."); }
                return;
            }
        }

        private static Client ClientOf(Character who) {
            if (GameMain.Server == null) { return null; }
            var clients = GameMain.Server.ConnectedClients;
            for (int i = 0; i < clients.Count; i++) { if (ReferenceEquals(clients[i].Character, who)) { return clients[i]; } }
            return null;
        }

        private static void SendReading(Client client, string text) {
            if (client?.Connection == null) { return; }
            reply.Clear(); MeteoWire.Header(reply, MeteoReply.Reading, 0, MeteoError.Ok); reply.Text(text);
            ModNet.SendBytes(MeteoWire.Channel, client.Connection, reply.Span, DeliveryMethod.Reliable);
        }

        private static void OnRequest(Client sender, uint nonce, uint seq, byte[] payload) {
            Character me = sender.Character;
            if (me == null || me.IsDead || me.Removed) { return; }
            ulong account = Access.Id(sender);
            if (!peers.TryGetValue(account, out Peer peer)) { peer = new Peer(); peers[account] = peer; }
            double wall = Timing.TotalTime;
            if (!peer.Gate.Take(wall, 10, 20) || !peer.Requests.Accept(sender, nonce, seq, out _)) { return; }
            var r = new WireReader(payload);
            if (!MeteoWire.ReadRequest(ref r, out MeteoOp op, out uint token, out ushort computerId)) { return; }
            if (!WeatherService.Active) { Reply(sender, MeteoReply.Ack, token, MeteoError.Invalid); return; }
            if (!(Entity.FindEntityByID(computerId) is Item computer) || computer.Removed || !PcWorld.CanUse(me, computer)) { Reply(sender, MeteoReply.Ack, token, MeteoError.Far); return; }
            if (!SwipeWorld.Powered(computer)) { Reply(sender, MeteoReply.Ack, token, MeteoError.NoPower); return; }
            ServerRack rack = RackFor(computer);
            double now = WeatherService.Clock.Seconds;
            switch (op) {
                case MeteoOp.Overview: {
                    ushort wanted = r.U16(); if (r.Failed || !r.End) { return; }
                    reply.Clear(); MeteoWire.Header(reply, MeteoReply.Overview, token, rack == null ? MeteoError.NoLink : MeteoError.Ok);
                    MeteoWire.Write(reply, BuildOverview(computer, rack, wanted, now));
                    Send(sender); return;
                }
                case MeteoOp.Series: {
                    ushort station = r.U16(); uint span = r.U32(); int points = r.U16(); if (r.Failed || !r.End || rack == null) { if (rack == null) { Reply(sender, MeteoReply.Ack, token, MeteoError.NoLink); } return; }
                    span = Math.Clamp(span, 60u, 14400u); points = Math.Clamp(points, 8, MeteoWire.MaxPoints);
                    var series = new MeteoSeries { Station = station, From = Math.Max(0, now - span), To = now };
                    rack.Archive.Decimate(station, series.From, series.To, points, scratch); series.Samples.AddRange(scratch);
                    rack.Archive.Gaps(station, series.From, series.To, SampleInterval * 3.5, gaps);
                    for (int i = Math.Max(0, gaps.Count - MeteoWire.MaxGaps); i < gaps.Count; i++) { series.Gaps.Add(gaps[i]); }
                    reply.Clear(); MeteoWire.Header(reply, MeteoReply.Series, token, MeteoError.Ok); MeteoWire.Write(reply, series);
                    Send(sender); return;
                }
                case MeteoOp.Journal: {
                    if (!r.End) { return; }
                    if (rack == null) { Reply(sender, MeteoReply.Ack, token, MeteoError.NoLink); return; }
                    reply.Clear(); MeteoWire.Header(reply, MeteoReply.Journal, token, MeteoError.Ok); MeteoWire.Write(reply, rack.Archive.Entries, MeteoWire.MaxEntries);
                    Send(sender); return;
                }
                case MeteoOp.Note: {
                    ushort station = r.U16(); string text = r.Text(MeteoArchive.EntryTextMax * 4); if (r.Failed || !r.End) { return; }
                    if (rack == null) { Reply(sender, MeteoReply.Ack, token, MeteoError.NoLink); return; }
                    text = text.Trim();
                    if (text.Length == 0 || text.Length > MeteoArchive.EntryTextMax) { Reply(sender, MeteoReply.Ack, token, MeteoError.Invalid); return; }
                    if (wall < peer.NextNote) { Reply(sender, MeteoReply.Ack, token, MeteoError.Rate); return; }
                    peer.NextNote = wall + 3;
                    bool written = rack.Archive.AddEntry(new MeteoEntry(now, account, me.Info?.Name ?? me.Name, station, text));
                    Reply(sender, MeteoReply.Ack, token, written ? MeteoError.Ok : MeteoError.Invalid); return;
                }
                case MeteoOp.Bulletin: {
                    ushort station = r.U16(); if (r.Failed || !r.End) { return; }
                    if (rack == null) { Reply(sender, MeteoReply.Ack, token, MeteoError.NoLink); return; }
                    if (!rack.Archive.Latest(station, out MeteoSample latest) || now - latest.Time > 120) { Reply(sender, MeteoReply.Ack, token, MeteoError.Invalid); return; }
                    MeteoOverview o = BuildOverview(computer, rack, station, now);
                    string name = units.TryGetValue(station, out Unit u) ? u.Item.Name : "станция";
                    int number = ++bulletins;
                    string body = MeteoText.Bulletin(number, WeatherService.Director.Day(now), name, latest, o.Forecast, o);
                    string file = "Бюллетень " + MeteoText.Clock(WeatherService.Director.Day(now)).Replace(':', '-') + ".txt";
                    PcError saved = PcHost.Publish(computer, "Метео", file, body, wall);
                    Reply(sender, MeteoReply.Ack, token, saved == PcError.Ok ? MeteoError.Ok : saved == PcError.Full ? MeteoError.Full : MeteoError.Invalid); return;
                }
                case MeteoOp.Alert: {
                    byte level = r.U8(); string text = r.Text(MeteoWire.AlertMax * 4); if (r.Failed || !r.End) { return; }
                    if (rack == null) { Reply(sender, MeteoReply.Ack, token, MeteoError.NoLink); return; }
                    text = text.Trim();
                    if (level < 1 || level > 3 || text.Length == 0 || text.Length > MeteoWire.AlertMax) { Reply(sender, MeteoReply.Ack, token, MeteoError.Invalid); return; }
                    if (now - alertAt < 45) { Reply(sender, MeteoReply.Ack, token, MeteoError.Busy); return; }
                    alertAt = now;
                    rack.Archive.AddEntry(new MeteoEntry(now, account, me.Info?.Name ?? me.Name, 0, "ОПОВЕЩЕНИЕ (" + level + "): " + text));
                    Broadcast(level, text, me.Info?.Name ?? me.Name);
                    Reply(sender, MeteoReply.Ack, token, MeteoError.Ok); return;
                }
            }
        }

        private static void Broadcast(byte level, string text, string from) {
            reply.Clear(); MeteoWire.Header(reply, MeteoReply.Alert, 0, MeteoError.Ok); reply.U8(level); reply.Text(from); reply.Text(text);
            var clients = GameMain.Server.ConnectedClients;
            for (int i = 0; i < clients.Count; i++) {
                Client c = clients[i];
                if (c.Connection == null || c.Character == null || c.Character.Removed) { continue; }
                ModNet.SendBytes(MeteoWire.Channel, c.Connection, reply.Span, DeliveryMethod.Reliable);
            }
        }

        private static MeteoOverview BuildOverview(Item terminal, ServerRack rack, ushort wanted, double now) {
            var o = new MeteoOverview { Now = now, Day = WeatherService.Director.Day(now), Frozen = WeatherService.Clock.Frozen, LastAlert = alertAt };
            if (rack == null) { return o; }
            o.RackPresent = true; o.RackPowered = SwipeWorld.Powered(rack.Item); o.RackName = rack.Item.Name;
            MeteoArchive a = rack.Archive;
            o.Capacity = a.Capacity; o.Used = a.Used; o.Disks = rack.Disks; o.Written = (uint)Math.Min(a.Written, uint.MaxValue); o.Overwritten = (uint)Math.Min(a.Overwritten, uint.MaxValue); o.Oldest = a.Oldest; o.Newest = a.Newest;
            ushort firstStation = 0;
            foreach (var pair in units) {
                Unit u = pair.Value;
                if (o.Stations.Count >= MeteoWire.MaxStations) { break; }
                if (Vector2.DistanceSquared(u.Item.WorldPosition, rack.Item.WorldPosition) > LinkRange * LinkRange * 4) { continue; }
                o.Stations.Add(new MeteoStationInfo { Id = pair.Key, Name = u.Item.Name, Kind = u.Kind, Powered = u.Powered, Linked = u.Linked, Heated = u.Powered && u.Kind == MeteoKind.Station, Last = u.Last, Ice = u.Model.Ice });
                if (firstStation == 0 && u.Kind == MeteoKind.Station) { firstStation = pair.Key; }
            }
            ushort selected = wanted != 0 && units.ContainsKey(wanted) ? wanted : firstStation;
            o.Selected = selected;
            if (selected == 0) { return o; }
            o.HasLatest = a.Latest(selected, out o.Latest);
            if (WeatherService.Config != forecasterConfig || forecaster == null) { forecasterConfig = WeatherService.Config; forecaster = new MeteoForecaster(forecasterConfig); }
            var key = (rack.Item.ID, selected);
            if (forecasts.TryGetValue(key, out var cached) && now - cached.Item1 < 4) { o.Forecast = cached.Item2; }
            else { o.Forecast = forecaster.Evaluate(a, selected, now); forecasts[key] = (now, o.Forecast); }
            return o;
        }

        private static void Reply(Client client, MeteoReply kind, uint token, MeteoError error) {
            reply.Clear(); MeteoWire.Header(reply, kind, token, error);
            Send(client);
        }

        private static void Send(Client client) => ModNet.SendBytes(MeteoWire.Channel, client.Connection, reply.Span, DeliveryMethod.Reliable);
    }
}
