using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using ScpItdMod.Core;
using ScpItdMod.Net;
using ScpItdMod.Weather;
using Microsoft.Xna.Framework;

namespace ScpItdMod.Thermal {
    internal static class ThermalNetwork {
#if CLIENT
        private static Character owner;
        private static ThermalReading local;
        private static bool have;
        private static double received;
        private static double nextProbe;
        private static uint token = unchecked((uint)Environment.TickCount);
        private static readonly WireWriter request = new WireWriter(11);
        private static readonly WireWriter mapWriter = new WireWriter(ThermalFieldWire.PacketSize);
        private static readonly ThermalFieldReplica map = new ThermalFieldReplica();
        private static double capturedAt = double.NegativeInfinity;
        private static uint mapSequence;
        private static bool mapChanged;
        internal static ThermalFieldReplica Map => map;
        internal static bool MapRequested { get; private set; }
        internal static void RequestMap(bool enabled) { MapRequested = enabled; mapChanged = true; nextProbe = 0; capturedAt = double.NegativeInfinity; if (!enabled) map.Reset(); }
        private static void Follow() {
            Character c = Character.Controlled;
            if (ReferenceEquals(owner, c)) return;
            owner = c; have = false; nextProbe = 0; token++;
            map.Reset(); capturedAt = double.NegativeInfinity;
        }
        private static void Receive(ReadOnlySpan<byte> payload) {
            Follow();
            Character c = Character.Controlled;
            if (!WeatherService.Active || !WeatherService.Synchronized || c == null || c.Removed || c.IsDead) return;
            if (ThermalWire.Read(payload, WeatherService.Epoch, token, c.ID, local.Sequence, have, out ThermalReading reading)) { local = reading; have = true; received = Timing.TotalTime; }
        }
        internal static bool TryLocal(out ThermalReading reading) {
            Character c = Character.Controlled;
            if (WeatherService.Authority) return ThermalRuntime.TryRead(c, out reading);
            reading = local;
            return WeatherService.Active && WeatherService.Synchronized && have && ReferenceEquals(owner, c) && c != null && !c.Removed && !c.IsDead && Timing.TotalTime >= received && Timing.TotalTime - received < 3;
        }
        private static void ReceiveMap(ReadOnlySpan<byte> payload) {
            Follow();
            if (!MapRequested || !WeatherService.Active || !WeatherService.Synchronized || owner == null || owner.Removed || owner.IsDead) return;
            map.Apply(payload, WeatherService.Epoch, token, owner.ID, Timing.TotalTime);
        }
        internal static void Attach() {
            ModNet.ReceiveSpan(ThermalWire.Channel, ThermalWire.PacketSize, Receive);
            ModNet.ReceiveSpan(ThermalFieldWire.Channel, ThermalFieldWire.PacketSize, ReceiveMap);
        }
        internal static void Tick() {
            Follow();
            if (WeatherService.Authority) { CaptureMap(); return; }
            if (owner == null || owner.Removed || owner.IsDead || !WeatherService.Synchronized || Timing.TotalTime < nextProbe || !mapChanged && !MapRequested && have && Timing.TotalTime - received < 2) return;
            nextProbe = Timing.TotalTime + 1;
            request.Clear(); request.U8(MapRequested ? (byte)1 : (byte)0); request.U32(WeatherService.Epoch); request.U16(owner.ID); request.U32(token);
            ModNet.SendBytes(ThermalWire.Channel, request.Span, DeliveryMethod.Reliable);
            mapChanged = false;
        }
        private static void CaptureMap() {
            if (!MapRequested || owner == null || owner.Removed || owner.IsDead || !ThermalRuntime.Enabled || ThermalMap.Field == null || capturedAt == ThermalMap.UpdatedAt || Timing.TotalTime - ThermalMap.UpdatedAt >= 3) return;
            capturedAt = ThermalMap.UpdatedAt;
            Span<ThermalPixel> tile = stackalloc ThermalPixel[ThermalFieldWire.Pixels];
            WeatherGrid grid = ThermalMap.Field.Grid;
            int cx = (int)MathF.Floor((owner.WorldPosition.X / WeatherService.UnitsPerMetre - grid.Origin.X) / (grid.Cell * 8));
            int cy = (int)MathF.Floor((owner.WorldPosition.Y / WeatherService.UnitsPerMetre - grid.Origin.Y) / (grid.Cell * 8));
            for (int y = Math.Max(0, cy - 1); y <= cy + 1 && y * 8 < grid.Height; y++) for (int x = Math.Max(0, cx - 1); x <= cx + 1 && x * 8 < grid.Width; x++) {
                ThermalMap.Tile(x, y, tile);
                var header = new ThermalTileHeader(WeatherService.Epoch, token, owner.ID, ThermalMap.Revision, ++mapSequence, grid.Origin, grid.Cell, (ushort)x, (ushort)y);
                ThermalFieldWire.Write(mapWriter, header, tile); map.Apply(mapWriter.Span, WeatherService.Epoch, token, owner.ID, Timing.TotalTime);
            }
        }
        internal static void Reset() { owner = null; local = default; have = false; received = nextProbe = 0; token++; MapRequested = false; mapChanged = false; map.Reset(); capturedAt = double.NegativeInfinity; ThermalClient.Reset(); }
#else
        private static readonly WireWriter writer = new WireWriter(ThermalWire.PacketSize);
        private sealed class Peer {
            internal RequestSession Gate;
            internal Character Character;
            internal uint Token;
            internal double NextRequest;
            internal ulong LastKey;
            internal double SentAt;
            internal double MapUntil;
            internal int MapCursor;
            internal uint MapSequence;
            internal readonly ulong[] MapKeys = new ulong[9];
            internal readonly double[] MapSentAt = new double[9];
        }
        private static readonly Dictionary<Client, Peer> peers = new Dictionary<Client, Peer>();
        private static readonly List<Client> removed = new List<Client>();
        private static double next;
        private static double nextMap;
        private static readonly WireWriter mapWriter = new WireWriter(ThermalFieldWire.PacketSize);
        internal static void Attach() => ModNet.ReceiveSpan(ThermalWire.Channel, 11, Receive);
        private static void Receive(Client client, uint nonce, uint sequence, ReadOnlySpan<byte> payload) {
            if (!WeatherService.Active || client.Connection == null || !client.InGame || client.Character == null || payload.Length != 11) return;
            var r = new WireReader(payload);
            byte mode = r.U8();
            if (mode > 1 || r.U32() != WeatherService.Epoch || r.U16() != client.Character.ID) return;
            uint token = r.U32(); if (r.Failed || !r.End) return;
            if (!peers.TryGetValue(client, out Peer peer)) { peer = new Peer(); peers.Add(client, peer); }
            if (Timing.TotalTime < peer.NextRequest || !peer.Gate.Accept(client.Connection, nonce, sequence, out _)) return;
            peer.NextRequest = Timing.TotalTime + .5;
            if (!ReferenceEquals(peer.Character, client.Character) || peer.Token != token) { Array.Clear(peer.MapKeys); Array.Clear(peer.MapSentAt); }
            peer.Character = client.Character; peer.Token = token;
            peer.MapUntil = mode == 1 ? Timing.TotalTime + 3 : 0;
            if (ThermalRuntime.TryRead(client.Character, out ThermalReading reading)) Send(client, peer, reading, DeliveryMethod.Reliable);
        }
        private static void Send(Client client, Peer peer, in ThermalReading reading, DeliveryMethod delivery) {
            ThermalWire.Write(writer, WeatherService.Epoch, peer.Token, reading);
            ModNet.SendBytes(ThermalWire.Channel, client.Connection, writer.Span, delivery);
            peer.LastKey = ThermalWire.ContentKey(reading); peer.SentAt = Timing.TotalTime;
        }
        internal static void Tick() {
            if (GameMain.Server == null) return;
            bool bodyDue = Timing.TotalTime >= next, mapDue = Timing.TotalTime >= nextMap;
            if (!bodyDue && !mapDue) return;
            if (bodyDue) next = Timing.TotalTime + .5;
            if (mapDue) nextMap = Timing.TotalTime + .25;
            foreach (Client client in GameMain.Server.ConnectedClients) {
                if (client.Connection == null || !client.InGame || !peers.TryGetValue(client, out Peer peer) || !ReferenceEquals(peer.Character, client.Character) || !ThermalRuntime.TryRead(client.Character, out ThermalReading reading)) continue;
                if (bodyDue && (ThermalWire.ContentKey(reading) != peer.LastKey || Timing.TotalTime - peer.SentAt >= 2)) Send(client, peer, reading, DeliveryMethod.Unreliable);
                if (mapDue && peer.MapUntil > Timing.TotalTime && ThermalRuntime.Enabled && ThermalMap.Field != null && Timing.TotalTime - ThermalMap.UpdatedAt < 3) SendMap(client, peer);
            }
            removed.Clear();
            foreach (var pair in peers) {
                bool connected = false;
                for (int i = 0; i < GameMain.Server.ConnectedClients.Count; i++) if (ReferenceEquals(GameMain.Server.ConnectedClients[i], pair.Key)) { connected = true; break; }
                if (pair.Key.Connection == null || !connected) removed.Add(pair.Key);
            }
            for (int i = 0; i < removed.Count; i++) peers.Remove(removed[i]);
        }
        private static void SendMap(Client client, Peer peer) {
            WeatherGrid grid = ThermalMap.Field.Grid;
            Vector2 position = peer.Character.WorldPosition / WeatherService.UnitsPerMetre;
            int cx = (int)MathF.Floor((position.X - grid.Origin.X) / (grid.Cell * 8)), cy = (int)MathF.Floor((position.Y - grid.Origin.Y) / (grid.Cell * 8));
            Span<ThermalPixel> tile = stackalloc ThermalPixel[ThermalFieldWire.Pixels];
            for (int packet = 0; packet < 2; packet++) {
                int cursor = peer.MapCursor; peer.MapCursor = (cursor + 1) % 9;
                int offset = (cursor + 4) % 9, x = cx + offset % 3 - 1, y = cy + offset / 3 - 1;
                if (x < 0 || y < 0 || x * 8 >= grid.Width || y * 8 >= grid.Height) continue;
                ThermalMap.Tile(x, y, tile);
                var header = new ThermalTileHeader(WeatherService.Epoch, peer.Token, peer.Character.ID, ThermalMap.Revision, ++peer.MapSequence, grid.Origin, grid.Cell, (ushort)x, (ushort)y);
                ulong key = ThermalFieldWire.Write(mapWriter, header, tile);
                if (peer.MapKeys[cursor] == key && Timing.TotalTime - peer.MapSentAt[cursor] < 2) continue;
                ModNet.SendBytes(ThermalFieldWire.Channel, client.Connection, mapWriter.Span, DeliveryMethod.Unreliable);
                peer.MapKeys[cursor] = key; peer.MapSentAt[cursor] = Timing.TotalTime;
            }
        }
        internal static void Reset() { peers.Clear(); removed.Clear(); next = nextMap = 0; }
#endif
    }
}
