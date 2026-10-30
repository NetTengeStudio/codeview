using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using ScpItdMod.Core;
using ScpItdMod.Net;

namespace ScpItdMod.Pool {
    public static class PoolHost {
        private sealed class Entry {
            public Item Item;
            public Match Match;
            public uint Epoch, Sent;
            public ushort Dirty;
            public CueState Cue;
            public uint SentCue;
            public ulong AimOwner;
            public uint AimNonce, AimInputSequence;
            public byte Online;
            public readonly string[] Names = new string[2];
            public readonly double[] Seen = new double[2];
            public readonly Ball[] Previous = new Ball[16];
            public HashSet<Client> Viewers = new HashSet<Client>(), Next = new HashSet<Client>();
        }
        private sealed class Peer {
            public readonly RequestSessions Requests = new RequestSessions();
            public readonly PoolEndpoint Endpoint = new PoolEndpoint();

            public Gate Gate, AimGate;
            public double Seen;
        }
        private static readonly Dictionary<ushort, Entry> tables = new Dictionary<ushort, Entry>();
        private static readonly Dictionary<ulong, Peer> peers = new Dictionary<ulong, Peer>();
        private static readonly List<ushort> removed = new List<ushort>();
        private static readonly List<ulong> expired = new List<ulong>();
        private static readonly WireWriter writer = new WireWriter(PoolWire.MaxReply);
        private static double nextSend, nextPresence;
        private static long ticks, steps, bytes, packets;
        private static uint epoch = Random();
        public static void Attach(Harmony harmony) {
            ModNet.ReceiveSpan(PoolWire.Channel, PoolWire.MaxRequest, Receive);
            harmony.Patch(AccessTools.Method(typeof(GameServer), nameof(GameServer.Update)), postfix: ModuleHost.Bind(typeof(PoolHost), nameof(Tick)));
            ModCommands.Register("scpitd_poolstats", "server: pool physics and traffic", _ => ModLog.Message($"pool: {tables.Count} tables, {steps} steps, {(steps == 0 ? 0 : ticks * 1e6 / Stopwatch.Frequency / steps):F2} us/step, {packets} packets, {bytes} bytes"));
        }
        public static void Clear() { tables.Clear(); peers.Clear(); removed.Clear(); expired.Clear(); nextSend = nextPresence = 0; ticks = steps = bytes = packets = 0; }
        private static uint Random() => (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
        private static Entry Find(Item item) {
            if (!tables.TryGetValue(item.ID, out Entry entry) || !ReferenceEquals(entry.Item, item)) {
                if (++epoch == 0) { epoch++; }
                entry = new Entry { Item = item, Match = new Match(PoolModule.Layout), Epoch = epoch }; tables[item.ID] = entry;
                entry.Match.Physics.Balls.CopyTo(entry.Previous);
            }
            return entry;
        }
        private static void Receive(Client client, uint nonce, uint seq, ReadOnlySpan<byte> data) {
            if (!PoolWire.ReadRequest(data, out PoolCommand command) || !(Entity.FindEntityByID(command.Table) is Item table) || table.Removed || !table.HasTag(PoolModule.Tag)) { return; }
            ulong actor = Access.Id(client); if (actor == 0 || !PoolModule.Near(client.Character, table, 1800)) { return; }
            if (!peers.TryGetValue(actor, out Peer peer)) { peer = new Peer(); peers.Add(actor, peer); }
            double now = Timing.TotalTime;
            bool update = command.Op == PoolOp.Aim;
            if (!(update ? peer.AimGate.Take(now, 12, 24) : peer.Gate.Take(now, 12, 24))) { return; }
            if (!peer.Requests.Accept(client, nonce, seq, update, out bool connectionChanged)) { return; }
            if (connectionChanged) { peer.Endpoint.Reset(); }
            peer.Seen = now; Entry entry = Find(table); PoolError error = PoolError.Ok;
            if (command.Op == PoolOp.Aim) {
                if (PoolModule.CanUse(client.Character, table) && PoolWire.CanAim(entry.Match, actor, command, entry.Epoch) &&
                    (entry.AimOwner != actor || entry.AimNonce != nonce || (int)(seq - entry.AimInputSequence) > 0)) {
                    uint serial = entry.Cue.Sequence + 1; if (serial == 0) { serial++; }
                    entry.AimOwner = actor; entry.AimNonce = nonce; entry.AimInputSequence = seq;
                    entry.Cue = new CueState { Table = table.ID, Epoch = entry.Epoch, Revision = command.Revision, Sequence = serial,
                        Direction = command.Aim, Tip = command.Tip, Power = (command.Speed - 0.25f) / 9.75f };
                }
                return;
            }
            if (command.Op != PoolOp.Poll) {
                int previousSeat = entry.Match.Seat(actor);
                error = peer.Endpoint.Handle(entry.Match, actor, command, entry.Epoch, Random(), command.Op == PoolOp.Leave ? entry.Match.Seat(actor) >= 0 : PoolModule.CanUse(client.Character, table));
                if (command.Op == PoolOp.Leave && error == PoolError.Ok && previousSeat >= 0) { entry.Names[previousSeat] = ""; entry.Online &= (byte)~(1 << previousSeat); entry.Seen[previousSeat] = 0; }
                int seat = entry.Match.Seat(actor); if (seat >= 0) { string name = Name(client); if (entry.Names[seat] != name) { entry.Sent = 0; } entry.Names[seat] = name; entry.Seen[seat] = now; }
            }
            Send(entry, client, true, command.Token, error);
        }
        private static string Name(Client client) { string name = client.Character?.Info?.Name ?? client.Character?.Name ?? client.Name; return name.Length <= 48 ? name : name.Substring(0, 48); }
        private static void Send(Entry entry, Client client, bool full, uint ack = 0, PoolError error = PoolError.Ok) {
            PoolWire.Snapshot(writer, entry.Item.ID, entry.Epoch, entry.Match, entry.Match.Seat(Access.Id(client)), entry.Online, entry.Names[0], entry.Names[1], entry.Dirty, full, ack, error);
            ModNet.SendBytes(PoolWire.Channel, client.Connection, writer.Span, full ? DeliveryMethod.Reliable : DeliveryMethod.Unreliable);
            bytes += writer.Length; packets++;
        }
        public static void Tick(float deltaTime) {
            if (GameMain.Server == null || !float.IsFinite(deltaTime) || deltaTime < 0) { return; }
            double now = Timing.TotalTime; bool network = now >= nextSend, presence = now >= nextPresence;
            if (network) { nextSend = now + 0.05; }
            if (presence) { nextPresence = now + 1; }
            IReadOnlyList<Item> list = Tagged.Of(PoolModule.Registry);
            for (int i = 0; i < list.Count; i++) { Find(list[i]); }
            removed.Clear();
            foreach (Entry entry in tables.Values) {
                if (entry.Item.Removed || !entry.Item.HasTag(PoolModule.Tag)) { removed.Add(entry.Item.ID); continue; }
                Match match = entry.Match;
                if (match.Physics.Moving) {
                    long start = Stopwatch.GetTimestamp();
                    steps += match.AdvanceTime(deltaTime);
                    ticks += Stopwatch.GetTimestamp() - start;
                    for (int i = 0; i < 16; i++) { Ball b = match.Physics.Balls[i]; if (BallRotation.Changed(b, entry.Previous[i])) { entry.Dirty |= (ushort)(1 << i); } entry.Previous[i] = b; }
                }
                if (presence) { Presence(entry, now); }
                if (!network) { continue; }
                bool full = entry.Sent != match.Revision;
                var clients = GameMain.Server.ConnectedClients;
                for (int c = 0; c < clients.Count; c++) {
                    Client client = clients[c];
                    if (client.Connection == null || !PoolModule.Near(client.Character, entry.Item, 1800) && !Rec.CamHost.Observes(client, entry.Item)) { continue; }
                    entry.Next.Add(client);
                    if (full || !entry.Viewers.Contains(client)) { Send(entry, client, true); }
                    else if (match.Physics.Moving && entry.Dirty != 0) { Send(entry, client, false); }
                    if (entry.Cue.Revision == match.Revision && !match.Physics.Moving && (entry.SentCue != entry.Cue.Sequence || !entry.Viewers.Contains(client))) {
                        PoolWire.AimSnapshot(writer, entry.Cue);
                        ModNet.SendBytes(PoolWire.Channel, client.Connection, writer.Span, DeliveryMethod.Unreliable);
                        bytes += writer.Length; packets++;
                    }
                }
                HashSet<Client> old = entry.Viewers; entry.Viewers = entry.Next; entry.Next = old; entry.Next.Clear();
                entry.Sent = match.Revision;
                entry.SentCue = entry.Cue.Sequence;
                if (!match.Physics.Moving) { entry.Dirty = 0; }
            }
            for (int i = 0; i < removed.Count; i++) { tables.Remove(removed[i]); }
            if (presence) {
                expired.Clear(); foreach (var pair in peers) { if (now - pair.Value.Seen > 120) { expired.Add(pair.Key); } }
                for (int i = 0; i < expired.Count; i++) { peers.Remove(expired[i]); }
            }
        }
        private static void Presence(Entry entry, double now) {
            byte online = 0;
            for (int i = 0; i < 2; i++) {
                ulong account = entry.Match.Players[i]; if (account == 0) { entry.Names[i] = ""; continue; }
                var clients = GameMain.Server.ConnectedClients;
                for (int c = 0; c < clients.Count; c++) {
                    Client client = clients[c];
                    if (Access.Id(client) != account || !Rp.RpWorld.Conscious(client.Character) || !PoolModule.Near(client.Character, entry.Item, 160)) { continue; }
                    online |= (byte)(1 << i); entry.Seen[i] = now; string name = Name(client); if (entry.Names[i] != name) { entry.Sent = 0; } entry.Names[i] = name; break;
                }
                if (now - entry.Seen[i] >= 90) { entry.Match.Leave(account); entry.Names[i] = ""; }
            }
            if (online != entry.Online) { entry.Online = online; entry.Sent = 0; }
        }
    }
}
