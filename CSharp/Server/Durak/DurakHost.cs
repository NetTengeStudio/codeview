using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using ScpItdMod.Auth;
using ScpItdMod.Core;
using ScpItdMod.Net;
using ScpItdMod.Pc;

namespace ScpItdMod.Durak {
    public static class DurakHost {
        private sealed class Peer {
            public Client Client;
            public uint SentDirectory, SentRoom, SentRevision, SentRoster, SentFeed, SentGame;
            public int SentMe = -2;
            public readonly DurakEndpoint Endpoint = new DurakEndpoint();
            public ushort Computer;
            public readonly RequestSessions Requests = new RequestSessions();
            public Gate Gate;
            public double Seen;
        }

        private static readonly DurakLobby lobby = new DurakLobby(Shuffle);
        private static readonly Dictionary<ulong, Peer> peers = new Dictionary<ulong, Peer>();
        private static readonly List<ulong> expired = new List<ulong>(DurakLobby.MaxMembers);
        private static readonly WireWriter writer = new WireWriter(DurakWire.MaxReply);
        private static double nextTick;

        public static void Attach() => ModNet.ReceiveBytes(DurakWire.Channel, DurakWire.MaxRequest, Receive);

        public static void Clear() { peers.Clear(); lobby.Clear(); expired.Clear(); nextTick = 0; }

        public static uint Room(ulong actor) => lobby.Member(actor)?.Room ?? 0;
        public static uint PublicRevision(uint room) => lobby.Room(room)?.Revision ?? 0;
        public static void Public(WireWriter target, uint room, uint revision) =>
            DurakWire.Reply(target, lobby, null, DisplayWire.Session, revision, 0, DurakError.Ok, Timing.TotalTime, false, true, publicRoom: room);

        private static void Shuffle(byte[] deck) {
            for (int i = deck.Length - 1; i > 0; i--) {
                int j = RandomNumberGenerator.GetInt32(i + 1);
                byte card = deck[i]; deck[i] = deck[j]; deck[j] = card;
            }
        }

        private static bool Available(Client client, ushort id) {
            return PcStations.Use(client, id) == PcError.Ok;
        }

        private static void Receive(Client client, uint nonce, uint seq, byte[] payload) {
            if (!DurakWire.ReadRequest(payload, out DurakCommand command)) { return; }
            ulong account = Access.Id(client);
            if (account == 0) { return; }
            double now = Timing.TotalTime;
            if (!peers.TryGetValue(account, out Peer peer)) {
                if (peers.Count >= DurakLobby.MaxMembers || !Available(client, command.Computer)) { return; }
                peer = new Peer(); peers.Add(account, peer);
            }
            if (!peer.Gate.Take(now, 6, 12)) { return; }
            if (!peer.Requests.Accept(client, nonce, seq, out bool connectionChanged)) { return; }
            if (connectionChanged) { peer.Endpoint.Reset(); }
            uint previous = peer.Endpoint.Session;
            if (!peer.Endpoint.Open(command.Session)) { return; }
            if (previous != command.Session) { peer.SentDirectory = peer.SentRevision = peer.SentRoom = peer.SentRoster = peer.SentFeed = peer.SentGame = 0; peer.SentMe = -2; }
            peer.Client = client; peer.Computer = command.Computer;
            if (command.Op != DurakOp.Leave && !Available(client, command.Computer)) {
                lobby.Offline(account);
                Send(peer, lobby.Member(account), command.Token, DurakError.Unavailable, now, false, false);
                return;
            }
            Character character = client.Character;
            if (!lobby.Touch(account, character?.Info?.Name ?? character?.Name ?? client.Name, now)) { return; }
            peer.Seen = now;
            bool repeated = peer.Endpoint.Repeated(command.Token);
            DurakError error = peer.Endpoint.Handle(lobby, account, command, now);
            DurakMember member = lobby.Member(account);
            DurakRoom room = lobby.Room(member.Room);
            bool directory = member.Room == 0 && (peer.SentDirectory != lobby.DirectoryRevision || command.Op != DurakOp.Poll);
            bool full = repeated || command.Op == DurakOp.Poll && command.Arg == 1 || error == DurakError.Stale;
            bool state = full || peer.SentRoom != member.Room || room != null && peer.SentRevision != room.Revision || command.Op != DurakOp.Poll;
            Send(peer, member, command.Token, error, now, directory, state, full);
        }

        private static void Send(Peer peer, DurakMember member, uint token, DurakError error, double now, bool directory, bool state, bool full = false) {
            DurakRoom room = lobby.Room(member?.Room ?? 0);
            DurakParts parts = DurakParts.All;
            if (!full && room != null && peer.SentRoom == room.Id) {
                parts = DurakParts.None;
                if (peer.SentRoster != room.RosterRevision) { parts |= DurakParts.Roster; }
                if (peer.SentFeed != room.FeedRevision) { parts |= DurakParts.Feed; }
                if (peer.SentGame != (room.Game?.Revision ?? 0) || peer.SentMe != member.Seat) { parts |= DurakParts.Game; }
            }
            DurakWire.Reply(writer, lobby, member, peer.Endpoint.Session, peer.Endpoint.NextReply(), token, error, now, directory, state, parts);
            ModNet.SendBytes(DurakWire.Channel, peer.Client.Connection, writer.Span, DeliveryMethod.Reliable);
            if (directory) { peer.SentDirectory = lobby.DirectoryRevision; }
            if (state) {
                peer.SentRoom = member?.Room ?? 0; peer.SentRevision = room?.Revision ?? 0;
                peer.SentRoster = room?.RosterRevision ?? 0; peer.SentFeed = room?.FeedRevision ?? 0; peer.SentGame = room?.Game?.Revision ?? 0; peer.SentMe = member?.Seat ?? -2;
            }
        }

        public static void Tick() {
            double now = Timing.TotalTime;
            if (now < nextTick) { return; }
            nextTick = now + 0.25;
            lobby.Tick(now);
            expired.Clear();
            foreach (var entry in peers) {
                Peer peer = entry.Value;
                if (now - peer.Seen > DurakLobby.ReconnectSeconds) { expired.Add(entry.Key); continue; }
                DurakMember member = lobby.Member(entry.Key);
                if (member == null || !member.Online) { continue; }
                if (!Available(peer.Client, peer.Computer)) { lobby.Offline(entry.Key); continue; }
                DurakRoom room = lobby.Room(member.Room);
                bool directory = member.Room == 0 && peer.SentDirectory != lobby.DirectoryRevision;
                bool state = peer.SentRoom != member.Room || room != null && peer.SentRevision != room.Revision;
                if (directory || state) { Send(peer, member, 0, DurakError.Ok, now, directory, state); }
            }
            for (int i = 0; i < expired.Count; i++) { peers.Remove(expired[i]); }
        }
    }
}
