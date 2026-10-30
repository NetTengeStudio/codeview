using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using ScpItdMod.Auth;
using ScpItdMod.Core;
using ScpItdMod.Net;
using ScpItdMod.Pc;

namespace ScpItdMod.Chess {
    public static class ChessHost {
        private sealed class Peer {
            public Client Client;
            public ushort Computer;
            public uint Sequence, Directory, Room, Revision;
            public readonly ChessEndpoint Endpoint = new ChessEndpoint();
            public readonly RequestSessions Requests = new RequestSessions();
            public Gate Gate;
            public double Seen;
        }
        private static readonly ChessLobby lobby = new ChessLobby();
        private static readonly Dictionary<ushort, Item> tables = new Dictionary<ushort, Item>();
        private static readonly Dictionary<ulong, Peer> peers = new Dictionary<ulong, Peer>();
        private static readonly List<ulong> expired = new List<ulong>(ChessLobby.MaxMembers);
        private static readonly WireWriter writer = new WireWriter(ChessWire.MaxReply);
        private static double nextTick;
        public static void Attach() => ModNet.ReceiveBytes(ChessWire.Channel, ChessWire.MaxRequest, Receive);
        public static void Clear() { peers.Clear(); lobby.Clear(); tables.Clear(); expired.Clear(); nextTick = 0; }
        public static uint Room(ulong actor) => lobby.Member(actor)?.Room ?? 0;
        public static uint PublicRevision(uint room) => lobby.Room(room)?.Revision ?? 0;
        public static void Public(WireWriter target, uint room, uint revision) =>
            ChessWire.Reply(target, lobby, 0, DisplayWire.Session, revision, 0, ChessError.Ok, false, true, publicRoom: room);
        private static bool Available(Client client, ushort id) {
            if (Entity.FindEntityByID(id) is Item item && ChessWorld.IsBoard(item)) { TableRoom(item); return ChessWorld.CanUse(client?.Character, item); }
            return PcStations.Use(client, id) == PcError.Ok;
        }
        public static uint TableRoom(Item item) {
            if (!tables.TryGetValue(item.ID, out Item previous) || !ReferenceEquals(previous, item)) {
                lobby.RemoveTable(item.ID); tables[item.ID] = item;
            }
            return lobby.Table(item.ID)?.Id ?? 0;
        }
        public static void RemoveTable(Item item) {
            if (tables.TryGetValue(item.ID, out Item previous) && ReferenceEquals(previous, item)) { tables.Remove(item.ID); lobby.RemoveTable(item.ID); }
        }
        private static void Receive(Client client, uint nonce, uint sequence, byte[] data) {
            if (!ChessWire.ReadRequest(data, out ChessCommand command)) { return; }
            ulong actor = Access.Id(client); double now = Timing.TotalTime;
            if (!peers.TryGetValue(actor, out Peer peer)) {
                if (actor == 0 || peers.Count >= ChessLobby.MaxMembers || !Available(client, command.Computer)) { return; }
                peer = new Peer(); peers.Add(actor, peer);
            }
            if (!peer.Gate.Take(now, 6, 12)) { return; }
            if (!peer.Requests.Accept(client, nonce, sequence, out bool connectionChanged)) { return; }
            if (connectionChanged) { peer.Endpoint.Reset(); }
            uint previous = peer.Endpoint.Session;
            if (!peer.Endpoint.Open(command.Session)) { return; }
            if (previous != command.Session) { peer.Room = peer.Revision = peer.Directory = 0; }
            peer.Client = client; peer.Computer = command.Computer; peer.Seen = now;
            bool leaving = command.Op == ChessOp.Leave && lobby.Member(actor) != null;
            if (!leaving && !Available(client, command.Computer)) { lobby.Offline(actor); Send(actor, peer, command.Token, ChessError.Unavailable, true); return; }
            if (!lobby.Touch(actor, client.Character?.Info?.Name ?? client.Character?.Name ?? client.Name, now)) { return; }
            bool table = Entity.FindEntityByID(command.Computer) is Item item && ChessWorld.IsBoard(item);
            ChessError error = ChessError.Ok;
            if (table && command.Op == ChessOp.Poll) { error = lobby.JoinTable(actor, command.Computer, now); }
            ChessRoom target = lobby.Room(command.Op == ChessOp.Join ? command.Room : lobby.Member(actor)?.Room ?? 0);
            if (!leaving && error == ChessError.Ok && (table && command.Op is ChessOp.Create or ChessOp.Join || target != null && target.Table != 0 && target.Table != command.Computer)) { error = ChessError.Unavailable; }
            if (error == ChessError.Ok) { error = peer.Endpoint.Handle(lobby, actor, command, now); }
            Send(actor, peer, command.Token, error, command.Op == ChessOp.Poll || error != ChessError.Ok);
        }
        private static void Send(ulong actor, Peer peer, uint ack, ChessError error, bool force) {
            ChessRoom room = lobby.Room(lobby.Member(actor)?.Room ?? 0);
            bool directory = room == null && (force || peer.Directory != lobby.Directory);
            bool state = force || peer.Room != (room?.Id ?? 0) || peer.Revision != (room?.Revision ?? 0);
            ChessWire.Reply(writer, lobby, actor, peer.Endpoint.Session, ++peer.Sequence, ack, error, directory, state);
            ModNet.SendBytes(ChessWire.Channel, peer.Client.Connection, writer.Span, DeliveryMethod.Reliable);
            peer.Room = room?.Id ?? 0; peer.Revision = room?.Revision ?? 0; if (directory) { peer.Directory = lobby.Directory; }
        }
        public static void Tick() {
            double now = Timing.TotalTime; if (now < nextTick) { return; } nextTick = now + 0.25;
            lobby.Tick(now); expired.Clear();
            foreach (var pair in peers) {
                Peer peer = pair.Value;
                if (now - peer.Seen >= ChessLobby.Reconnect) { expired.Add(pair.Key); continue; }
                ChessMember member = lobby.Member(pair.Key); if (member == null || !member.Online) { continue; }
                if (!Available(peer.Client, peer.Computer)) { lobby.Offline(pair.Key); continue; }
                ChessRoom room = lobby.Room(member.Room);
                if (peer.Room != member.Room || room != null && peer.Revision != room.Revision || room == null && peer.Directory != lobby.Directory) { Send(pair.Key, peer, 0, ChessError.Ok, false); }
            }
            for (int i = 0; i < expired.Count; i++) { peers.Remove(expired[i]); }
        }
    }
}
