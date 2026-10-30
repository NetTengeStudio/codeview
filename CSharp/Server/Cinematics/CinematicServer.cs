using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using ScpItdMod.Core;
using ScpItdMod.Net;

namespace ScpItdMod.Cinematics {
    internal static class CinematicServer {
        private sealed class Peer {
            internal RequestSession Requests;
            internal Gate Gate, UpdateGate;
            internal readonly HashSet<ulong> Hidden = new HashSet<ulong>();
            internal double Echo;
            internal CinematicObserver Observer;
            internal CinematicReadiness Readiness;
        }
        private static readonly Dictionary<Client, Peer> peers = new Dictionary<Client, Peer>();
        private static readonly List<Client> expired = new List<Client>();
        private static readonly WireWriter writer = new WireWriter(CinematicWire.MaximumPacket);
        private static uint sequence;
        private static bool changed;
        private static bool wasPaused;
        private static double next;
        private static long sentBytes, receivedBytes;
        internal static void Attach(Harmony harmony) {
            ModNet.ReceiveSpan(CinematicWire.Channel, 2048, Receive);
            harmony.Patch(AccessTools.Method(typeof(GameServer), nameof(GameServer.Update), new[] { typeof(float) }), postfix: ModuleHost.Bind(typeof(CinematicServer), nameof(Tick)));
            harmony.Patch(AccessTools.Method(typeof(GameServer), nameof(GameServer.DisconnectClient), new[] { typeof(Client), typeof(PeerDisconnectPacket) }),
                prefix: ModuleHost.Bind(typeof(CinematicServer), nameof(Disconnect)));
        }
        internal static bool Ready(Client client) => peers.TryGetValue(client, out Peer peer) && peer.Readiness.Ready(CinematicHost.Epoch, CinematicSubScenes.CatalogHash);
        private static void Disconnect(Client __0) { if (__0 == null) return; peers.Remove(__0); CinematicHost.Disconnect(CinematicActors.Disconnected(__0)); }
        internal static void Changed() => changed = true;
        internal static void Tick() {
            CinematicActors.TickRetries();
            bool paused = GameMain.GameSession?.GameMode?.Paused == true;
            if (paused != wasPaused) { wasPaused = paused; changed = true; }
            if (GameMain.Server == null || !changed && Timing.TotalTime < next) return;
            bool reliable = changed;
            changed = false; next = Timing.TotalTime + 2;
            if (!reliable && CinematicHost.Sessions.Count == 0) return;
            expired.Clear();
            foreach (var pair in peers) {
                if (!Connected(pair.Key)) { expired.Add(pair.Key); continue; }
                Send(pair.Key, pair.Value, reliable ? DeliveryMethod.Reliable : DeliveryMethod.Unreliable);
            }
            foreach (Client client in expired) peers.Remove(client);
        }
        private static bool Connected(Client client) {
            foreach (Client connected in GameMain.Server.ConnectedClients) if (ReferenceEquals(client, connected)) return true;
            return false;
        }
        private static bool Interested(Client client, CinematicSession session) {
            if (session.AudienceKeys == null) return true;
            ulong key = Access.Id(client);
            for (int i = 0; i < session.AudienceKeys.Length; i++) if (session.AudienceKeys[i] == key) return true;
            return false;
        }
        private static void Send(Client client, Peer peer, DeliveryMethod delivery) {
            peer.Hidden.RemoveWhere(NotActive);
            CinematicWire.Header(writer, 0); writer.U32(CinematicHost.Epoch); writer.U32(++sequence);
            writer.U64(BitConverter.DoubleToUInt64Bits(Timing.TotalTime)); writer.U64(BitConverter.DoubleToUInt64Bits(peer.Echo));
            writer.Bool(GameMain.GameSession?.GameMode?.Paused == true);
            int count = 0;
            foreach (CinematicSession session in CinematicHost.Sessions) if (Interested(client, session)) count++;
            writer.U8((byte)count);
            foreach (CinematicSession session in CinematicHost.Sessions) {
                if (!Interested(client, session)) continue;
                CinematicSequencer q = session.Sequencer;
                writer.U64(session.Id); writer.Text(session.Bindings.Plan.Id); writer.U64(session.Bindings.Plan.Hash);
                writer.U32(q.Revision); writer.U16((ushort)q.Stage); writer.F32((float)q.StageElapsed); writer.F32((float)q.Elapsed); writer.U64(q.Fired); writer.U32(session.Player.Lifetime.Removed); writer.U32(session.Player.Lifetime.Retiring); writer.F32(session.Bindings.UnitsPerMetre);
                writer.Bool(q.Status != CinematicStatus.Running || q.PresentationSkipped || peer.Hidden.Contains(session.Id)); writer.U8((byte)session.Bindings.Entities.Length);
                for (int i = 0; i < session.Bindings.Entities.Length; i++) CinematicWire.WriteBinding(writer, session.Bindings.Id(i), (session.Player.Lifetime.Retiring & (1u << i)) != 0, session.Player.Lifetime.FadeAge(i));
                if (NetTrace.Enabled) NetTrace.State(CinematicWire.Channel, "scene-send", session.Id, CinematicHost.Epoch, sequence, q.Revision, q.Stage,
                    CinematicWire.StateHash(q.Revision, (ushort)q.Stage, q.Fired, session.Player.Lifetime.Removed, session.Player.Lifetime.Retiring), Timing.TotalTime);
            }
            sentBytes += writer.Length;
            if (NetTrace.Enabled) NetTrace.State(CinematicWire.Channel, "roster-send", 0, CinematicHost.Epoch, sequence, 0, count, CinematicWire.PayloadHash(writer.Span), Timing.TotalTime);
            ModNet.SendBytes(CinematicWire.Channel, client.Connection, writer.Span, delivery);
            peer.Echo = 0;
        }
        // Who keeps the helicopter from being removed, for the log; empty when nobody does.
        internal static string RemovalBlockers(Vec2 point, float radius, float minimumDistance) {
            if (GameMain.Server == null) return "no server";
            string names = "";
            foreach (Client client in GameMain.Server.ConnectedClients) {
                if (!client.InGame) continue;
                if (!Clear(client, point, radius, minimumDistance)) names += (names.Length > 0 ? ", " : "") + client.Name + (peers.TryGetValue(client, out Peer peer) && peer.Observer.Fresh(Timing.TotalTime) ? " (sees it)" : " (no fresh view report)");
            }
            return names;
        }
        internal static bool ClearForRemoval(Vec2 point, float radius, float minimumDistance) => RemovalBlockers(point, radius, minimumDistance).Length == 0 && GameMain.Server != null;
        private static bool Clear(Client client, Vec2 point, float radius, float minimumDistance) {
            // A player's view is what the client last reported. A client that has not reported (a lost packet, a client still starting) must not hold
            // the helicopter on the pad for ever: without a fresh report the server's own knowledge of where that player looks (the spectating camera
            // or the character) decides.
            if (peers.TryGetValue(client, out Peer peer) && peer.Observer.Fresh(Timing.TotalTime)) {
                if (!peer.Observer.Clear(Timing.TotalTime, point, radius, minimumDistance)) return false;
            } else {
                Vec2? looking = client.SpectatePos is Microsoft.Xna.Framework.Vector2 s ? new Vec2(s.X, s.Y) : client.Character is { Removed: false } c ? new Vec2(c.WorldPosition.X, c.WorldPosition.Y) : null;
                if (looking is Vec2 where && (point - where).LengthSquared() <= (minimumDistance + radius) * (minimumDistance + radius)) return false;
            }
            if (client.Character is { Removed: false } observer) {
                Vec2 at = new Vec2(observer.WorldPosition.X, observer.WorldPosition.Y);
                float range = peers.TryGetValue(client, out Peer p) && p.Observer.Fresh(Timing.TotalTime) ? MathF.Max(minimumDistance, p.Observer.View.HalfSize.Length()) : minimumDistance;
                if ((point - at).LengthSquared() <= (range + radius) * (range + radius)) return false;
            }
            return true;
        }
        private static bool NotActive(ulong id) { foreach (CinematicSession session in CinematicHost.Sessions) if (session.Id == id) return false; return true; }
        private static void Note(Client client, string message) {
            if (message.Length > 128) message = message.Substring(0, 128);
            CinematicWire.Header(writer, 7); writer.Text(message); sentBytes += writer.Length;
            ModNet.SendBytes(CinematicWire.Channel, client.Connection, writer.Span, DeliveryMethod.Reliable);
        }
        private static void Receive(Client sender, uint nonce, uint seq, ReadOnlySpan<byte> payload) {
            if (sender?.Connection == null || payload.Length < 2 || payload.Length > 2048 || payload[0] != CinematicWire.Version) return;
            bool update = payload[1] == 9;
            if (!peers.TryGetValue(sender, out Peer peer)) { peer = new Peer(); peers.Add(sender, peer); }
            if (!(update ? peer.UpdateGate.Take(Timing.TotalTime, 12, 20) : peer.Gate.Take(Timing.TotalTime, 12, 20)) ||
                !peer.Requests.Accept(sender.Connection, nonce, seq, update, out _)) return;
            receivedBytes += payload.Length;
            var reader = new WireReader(payload);
            if (reader.U8() != CinematicWire.Version) return;
            byte operation = reader.U8();
            if (operation == CinematicWire.ReadyRequest) {
                if (!CinematicWire.ReadReady(payload, CinematicWire.ReadyRequest, out uint epoch, out ulong catalog)) return;
                if (!peer.Readiness.Accept(epoch, catalog, CinematicHost.Epoch, CinematicSubScenes.CatalogHash)) {
                    Note(sender, "Cinematic readiness rejected: round or catalog differs"); return;
                }
                NetTrace.State(CinematicWire.Channel, "ready-accept", Access.Id(sender), epoch, seq, nonce, 0, catalog, Timing.TotalTime);
                CinematicWire.WriteReadyAccepted(writer, epoch, catalog, nonce, seq);
                sentBytes += writer.Length;
                ModNet.SendBytes(CinematicWire.Channel, sender.Connection, writer.Span, DeliveryMethod.Reliable);
                return;
            }
            if (operation == 9) {
                float x = reader.F32(), y = reader.F32(), width = reader.F32(), height = reader.F32();
                if (reader.Failed || !reader.End || !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(width) || !float.IsFinite(height) || MathF.Abs(x) > 1000000 || MathF.Abs(y) > 1000000 || width < 1 || height < 1 || width > 100000 || height > 100000) return;
                peer.Observer = new CinematicObserver(new CinematicViewport(new Vec2(x, y), new Vec2(width, height)), Timing.TotalTime);
                return;
            }
            if (operation == 0) {
                double echo = BitConverter.UInt64BitsToDouble(reader.U64());
                if (reader.Failed || !reader.End || !double.IsFinite(echo) || echo < 0) return;
                peer.Echo = echo; Send(sender, peer, DeliveryMethod.Reliable); return;
            }
            if (operation == 6) {
                ulong id = reader.U64();
                if (reader.Failed || !reader.End) return;
                foreach (CinematicSession scene in CinematicHost.Sessions) if (scene.Id == id && Interested(sender, scene)) { peer.Hidden.Add(id); Send(sender, peer, DeliveryMethod.Reliable); return; }
                return;
            }
            if (operation is < 1 or > 5 and not 8) return;
            if (!Access.Gm(sender)) { Note(sender, "ConsoleCommands permission is required to direct world cinematics"); return; }
            int count = reader.U8(); if (count > 20) return;
            var args = new string[count];
            for (int i = 0; i < count; i++) args[i] = reader.Text(128);
            if (reader.Failed || !reader.End) return;
            try { CinematicModule.ExecuteCommand(operation, args); }
            catch (Exception error) { ModLog.Error("Cinematic admin command: " + error.Message); Note(sender, error.Message); }
            Send(sender, peer, DeliveryMethod.Reliable);
        }
        internal static void Reset() { foreach (Peer peer in peers.Values) { peer.Hidden.Clear(); peer.Readiness = default; peer.Observer = default; } changed = true; next = 0; }
        internal static void Status() => ModLog.Message($"cinematic wire peers={peers.Count} sent={sentBytes}B received={receivedBytes}B; BOVehicles owns physical replication");
    }
}
