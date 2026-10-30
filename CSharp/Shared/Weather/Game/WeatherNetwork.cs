using System;
using System.Collections.Generic;
using System.Text;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using ScpItdMod.Core;
using ScpItdMod.Net;
using ScpItdMod.Weather.Material;

namespace ScpItdMod.Weather {
    internal static class WeatherNetwork {
        private static readonly WireWriter writer = new WireWriter(WeatherWire.MaximumPacket);
        internal static void BloodDrop(Vec2 point, Vec2 velocity) {
#if CLIENT
            WeatherClient.BloodDrop(point, velocity, WeatherService.Clock.Seconds);
#else
            writer.Clear(); writer.U8(7); writer.U32(WeatherService.Epoch);
            writer.F32(point.X); writer.F32(point.Y); writer.F32(velocity.X); writer.F32(velocity.Y);
            WeatherWire.Double(writer, WeatherService.Clock.Seconds);
            foreach (var pair in peers) {
                Client client = pair.Key;
                if (client.Connection == null || pair.Value.Epoch != WeatherService.Epoch || !pair.Value.Stains) continue;
                var observer = client.Character?.WorldPosition ?? client.SpectatePos;
                if (observer.HasValue && MathF.Abs(observer.Value.X / WeatherService.UnitsPerMetre - point.X) > 100) continue;
                ModNet.SendBytes(WeatherWire.Channel, client.Connection, writer.Span, DeliveryMethod.Unreliable);
            }
#endif
        }
        internal static void SnowBurst(Vec2 point, Vec2 velocity, float kilograms) {
#if CLIENT
            WeatherClient.SnowBurst(point, velocity, kilograms);
#else
            writer.Clear(); writer.U8(5); writer.U32(WeatherService.Epoch); writer.F32(point.X); writer.F32(point.Y); writer.F32(velocity.X); writer.F32(velocity.Y); writer.F32(kilograms);
            foreach (var pair in peers) {
                Client client = pair.Key;
                if (client.Connection == null || pair.Value.Epoch != WeatherService.Epoch) continue;
                var observer = client.Character?.WorldPosition ?? client.SpectatePos;
                if (observer.HasValue && MathF.Abs(observer.Value.X / WeatherService.UnitsPerMetre - point.X) > 100) continue;
                ModNet.SendBytes(WeatherWire.Channel, client.Connection, writer.Span, DeliveryMethod.Unreliable);
            }
#endif
        }
        // A shovel stroke or a new load as the server decided it: every peer plays the same stroke from the same parameters and shows the same load.
        internal static void Shovel(Item item, ShovelStroke kind, float bite, float load, Character striker) {
#if SERVER
            writer.Clear(); writer.U8(11); writer.U32(WeatherService.Epoch); writer.U16(item.ID); writer.U8((byte)kind);
            writer.U8((byte)Math.Clamp((int)MathF.Round(bite * 255), 0, 255)); writer.U8((byte)Math.Clamp((int)MathF.Round(load * 255), 0, 255)); writer.U16(striker?.ID ?? 0);
            foreach (var pair in peers) {
                Client client = pair.Key;
                if (client.Connection == null || pair.Value.Epoch != WeatherService.Epoch) continue;
                ModNet.SendBytes(WeatherWire.Channel, client.Connection, writer.Span, DeliveryMethod.Reliable);
            }
#endif
        }
        internal static bool Ready => WeatherService.Synchronized;
#if CLIENT
        private static double nextProbe, nextView;
        private static uint snowGeneration;
        private static ulong mismatched;
        private static SnowPack receivedPack;
        private static SnowBlocks receivedBlocks;
        private static uint pressSequence;
        private static void Receive(ReadOnlySpan<byte> payload) {
            var r = new WireReader(payload); byte type = r.U8(); uint epoch = r.U32();
            if (type == 4) { string message = r.Text(4096); if (!r.Failed && r.End) ModLog.Message("Weather: " + message); return; }
            if (!WeatherService.Active) return;
            if (type is 0 or 1) {
                bool initial = !Ready || epoch != WeatherService.Epoch;
                WeatherReceiveResult result = WeatherService.ApplyState(payload);
                if (result == WeatherReceiveResult.ConfigurationMismatch) ModLog.Once("Weather configuration differs from server");
                if (result != WeatherReceiveResult.Accepted) { nextProbe = 0; return; }
                if (initial) {
                    snowGeneration = 0; receivedPack = null; receivedBlocks = null; pressSequence = 0;
                    writer.Clear(); writer.U8(6); writer.U32(WeatherService.Epoch);
                    ModNet.SendBytes(WeatherWire.Channel, writer.Span, DeliveryMethod.Reliable);
                }
                return;
            }
            if (!Ready || epoch != WeatherService.Epoch) return;
            if (type == 2) {
                uint sequence = r.U32(); double seconds = WeatherWire.Double(ref r), echo = WeatherWire.Double(ref r); bool frozen = r.Bool();
                if (r.Failed || !r.End) return;
                WeatherService.SynchronizeClock(epoch, sequence, seconds, Timing.TotalTime - echo, frozen);
            } else if (type == 7) {
                var point = new Vec2(r.F32(), r.F32()); var velocity = new Vec2(r.F32(), r.F32());
                double born = WeatherWire.Double(ref r);
                if (!r.Failed && r.End && double.IsFinite(born) && float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(velocity.X) && float.IsFinite(velocity.Y)) WeatherClient.BloodDrop(point, velocity, born);
            } else if (type == 5) {
                var point = new Vec2(r.F32(), r.F32()); var velocity = new Vec2(r.F32(), r.F32()); float mass = r.F32();
                if (!r.Failed && r.End && float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(velocity.X) && float.IsFinite(velocity.Y) && float.IsFinite(mass) && mass > 0 && mass <= 100) WeatherClient.SnowBurst(point, velocity, mass);
            } else if (type == 11) {
                ushort id = r.U16(); byte kind = r.U8(); float bite = r.U8() / 255f, load = r.U8() / 255f; ushort striker = r.U16();
                if (r.Failed || !r.End || kind > (byte)ShovelStroke.Throw) return;
                if (Entity.FindEntityByID(id) is Item item && item.GetComponent<ScpWeatherShovel>() is ScpWeatherShovel shovel)
                    shovel.Remote((ShovelStroke)kind, bite, load, striker == 0 ? null : Entity.FindEntityByID(striker) as Character);
            } else if (type == 9) {
                // The presses the server applied during one tick, in its order: replaying them reproduces the footprints and ruts of everyone else, so their blocks need only be confirmed.
                ulong geometry = r.U64(); uint generation = r.U32(), sequence = r.U32(); int count = r.U16();
                SnowPack pack = SnowRuntime.Pack;
                if (r.Failed || pack == null || geometry != WeatherMap.Fingerprint || generation != snowGeneration || !ReferenceEquals(receivedPack, pack) || sequence <= pressSequence || r.Remaining != count * SnowPress.Bytes) return;
                pressSequence = sequence;
                for (int i = 0; i < count; i++) { if (!SnowPress.Read(ref r, out SnowPress press)) return; SnowPrint.Apply(pack.Material, press); }
            } else if (type == 3) {
                ulong geometry = r.U64(); uint generation = r.U32();
                SnowPack pack = SnowRuntime.Pack;
                if (pack == null || generation < snowGeneration || generation == 0) return;
                if (geometry != WeatherMap.Fingerprint) { if (geometry != mismatched) { mismatched = geometry; WeatherMap.Dirty(); } return; }
                bool reset = !ReferenceEquals(receivedPack, pack) || generation != snowGeneration;
                SnowBlocks candidate = reset ? new SnowBlocks(pack.Material, false) : receivedBlocks;
                SnowBlocks.Outcome outcome = candidate.Read(ref r, out long key, out uint revision);
                if (outcome == SnowBlocks.Outcome.Malformed) return;
                if (reset) { receivedPack = pack; receivedBlocks = candidate; snowGeneration = generation; pressSequence = 0; }
                SnowBlocks.Split(key, out int bx, out int by);
                if (outcome is SnowBlocks.Outcome.Agreed or SnowBlocks.Outcome.Replaced) {
                    writer.Clear(); writer.U8(2); writer.U32(WeatherService.Epoch); writer.U64(geometry); writer.U32(generation); writer.U32((uint)bx); writer.U32((uint)by); writer.U32(revision);
                    ModNet.SendBytes(WeatherWire.Channel, writer.Span, DeliveryMethod.Unreliable);
                } else if (outcome == SnowBlocks.Outcome.NeedParticles) {
                    // The local prediction and the server disagree here: ask for the particles themselves.
                    writer.Clear(); writer.U8(8); writer.U32(WeatherService.Epoch); writer.U64(geometry); writer.U32(generation); writer.U32((uint)bx); writer.U32((uint)by);
                    ModNet.SendBytes(WeatherWire.Channel, writer.Span, DeliveryMethod.Reliable);
                }
            }
        }
        // The footprints of the controlled character are pressed here at once and sent up every tick; the server applies them as they are and passes them on.
        private const int OwnPressesPerPacket = (2048 - 32) / SnowPress.Bytes;
        private static double nextPresses;
        private static void SendOwnPresses() {
            List<SnowPrint.Entry> journal = SnowPrint.Journal;
            SnowPrint.Recording = Ready && ReferenceEquals(receivedPack, SnowRuntime.Pack);
            if (Timing.TotalTime < nextPresses || journal.Count == 0) return;
            nextPresses = Timing.TotalTime + .1;
            if (!ReferenceEquals(receivedPack, SnowRuntime.Pack)) { journal.Clear(); return; }
            for (int start = 0; start < journal.Count;) {
                int count = 0, end = start;
                for (; end < journal.Count && count < OwnPressesPerPacket; end++) if (journal[end].Upstream) count++;
                if (count == 0) break;
                writer.Clear(); writer.U8(10); writer.U32(WeatherService.Epoch); writer.U64(WeatherMap.Fingerprint); writer.U32(snowGeneration); writer.U16((ushort)count);
                for (; start < end; start++) if (journal[start].Upstream) journal[start].Press.Write(writer);
                ModNet.SendBytes(WeatherWire.Channel, writer.Span, DeliveryMethod.Reliable);
            }
            journal.Clear();
        }
        internal static void Tick() {
            if (WeatherService.Authority) return;
            SendOwnPresses();
            if (Ready && Timing.TotalTime >= nextView && GameMain.GameScreen?.Cam != null) {
                nextView = Timing.TotalTime + .5;
                WeatherRect view = WeatherMap.Rect(GameMain.GameScreen.Cam.WorldView);
                writer.Clear(); writer.U8(3); writer.U32(WeatherService.Epoch); writer.F32(view.Left); writer.F32(view.Bottom); writer.F32(view.Right); writer.F32(view.Top);
                ModNet.SendBytes(WeatherWire.Channel, writer.Span, DeliveryMethod.Unreliable);
            }
            if (Timing.TotalTime < nextProbe) return;
            bool synchronized = Ready && !WeatherService.NeedsSnapshot;
            nextProbe = Timing.TotalTime + (synchronized ? 5 : 1);
            writer.Clear(); writer.U8(0); writer.U32(synchronized ? WeatherService.Epoch : 0); writer.U32(synchronized ? WeatherService.Director.Revision : uint.MaxValue); WeatherWire.Double(writer, Timing.TotalTime);
            ModNet.SendBytes(WeatherWire.Channel, writer.Span, DeliveryMethod.Unreliable);
        }
        internal static void Command(string[] args) {
            if (args.Length > 8) throw new ArgumentException("Too many weather command arguments");
            if (args.Length > 0 && args[0] == "quality") { WeatherCommands.Execute(args); return; }
            writer.Clear(); writer.U8(1); writer.U8((byte)args.Length);
            for (int i = 0; i < args.Length; i++) { if (args[i].Length > 128) throw new ArgumentException("Weather argument too long"); writer.Text(args[i]); }
            ModNet.SendBytes(WeatherWire.Channel, writer.Span, DeliveryMethod.Reliable);
        }
        internal static void Changed() { }
        internal static void SnowChanged() { }
        internal static void Reset() { nextProbe = nextView = nextPresses = 0; snowGeneration = 0; mismatched = 0; receivedPack = null; receivedBlocks = null; pressSequence = 0; SnowPrint.Recording = false; SnowPrint.Journal.Clear(); }
        internal static void Attach(Harmony harmony) => ModNet.ReceiveSpan(WeatherWire.Channel, WeatherWire.MaximumPacket, Receive);
        internal static void Release() => Reset();
#else
        private sealed class Peer {
            internal bool Stains;
            internal RequestSession Requests;
            internal uint Epoch, Revision;
            internal double NextCommand;
            internal readonly Dictionary<long, uint> BlockAck = new Dictionary<long, uint>();
            internal readonly Dictionary<long, double> BlockSent = new Dictionary<long, double>();
            internal readonly HashSet<long> Need = new HashSet<long>();
            internal uint SnowGeneration;
            internal PressBudget Presses;
            internal PressLoad Load;
            internal ulong SnowGeometry;
            internal double NextSnow;
            internal WeatherRect View;
            internal double ViewAt = double.NegativeInfinity;
            internal WeatherRect Interest;
        }
        // At most this many blocks go to a peer per tick. Near the character of the peer only a signature is sent first, so its own prediction is kept when it agrees.
        private const int SnowBlocksPerTick = 4, OwnPressesPerPacket = (2048 - 32) / SnowPress.Bytes;
        private const float OwnReach = 3;
        private const double SnowResend = .4, SnowPriorityMetres = 12, PredictedMetres = 3;
        private static readonly Dictionary<Client, Peer> peers = new Dictionary<Client, Peer>();
        private static bool changed;
        private static double nextSnow;
        private static uint sequence;
        private static SnowBlocks snowBlocks;
        private static SnowPack blocksPack;
        private static uint snowGeneration, pressSequence;
        internal static void Attach(Harmony harmony) {
            ModNet.ReceiveSpan(WeatherWire.Channel, 2048, Receive);
            harmony.Patch(AccessTools.Method(typeof(GameServer), nameof(GameServer.DisconnectClient), new[] { typeof(Client), typeof(PeerDisconnectPacket) }), prefix: ModuleHost.Bind(typeof(WeatherNetwork), nameof(Disconnect)));
        }
        private static void Disconnect(Client __0) { if (__0 != null) peers.Remove(__0); }
        private static void Receive(Client sender, uint nonce, uint seq, ReadOnlySpan<byte> payload) {
            if (!WeatherService.Active || sender.Connection == null || payload.Length == 0) return;
            bool update = payload[0] is 0 or 2 or 3 or 8;
            if (!peers.TryGetValue(sender, out Peer peer)) { peer = new Peer(); peers.Add(sender, peer); }
            if (!peer.Requests.Accept(sender.Connection, nonce, seq, update, out _)) return;
            var r = new WireReader(payload); byte type = r.U8();
            if (type == 6) {
                uint epoch = r.U32();
                if (!r.Failed && r.End && epoch == WeatherService.Epoch && !peer.Stains) {
                    peer.Stains = true; peer.BlockAck.Clear();
                }
                return;
            }
            if (type == 0) {
                uint epoch = r.U32(), revision = r.U32(); double echo = WeatherWire.Double(ref r);
                if (r.Failed || !r.End || !double.IsFinite(echo)) return;
                if (epoch != WeatherService.Epoch || revision > WeatherService.Director.Revision || peer.Epoch != epoch) { SendState(sender, peer, true); peer.SnowGeometry = 0; }
                else if (revision != WeatherService.Director.Revision) { peer.Revision = revision; SendState(sender, peer, false); }
                writer.Clear(); writer.U8(2); writer.U32(WeatherService.Epoch); writer.U32(++sequence); WeatherWire.Double(writer, WeatherService.Clock.Seconds); WeatherWire.Double(writer, echo); writer.Bool(WeatherService.Clock.Frozen);
                ModNet.SendBytes(WeatherWire.Channel, sender.Connection, writer.Span, DeliveryMethod.Unreliable); return;
            }
            if (type == 10) {
                uint epoch = r.U32(); ulong geometry = r.U64(); uint generation = r.U32(); int presses = r.U16();
                SnowPack pack = SnowRuntime.Pack; Character own = sender.Character;
                if (r.Failed || pack == null || own == null || epoch != WeatherService.Epoch || geometry != WeatherMap.Fingerprint || generation != snowGeneration || presses > OwnPressesPerPacket || r.Remaining != presses * SnowPress.Bytes) return;
                if (!peer.Presses.Take(Timing.TotalTime, presses)) return;
                if (own.AnimController == null || !own.AnimController.OnGround) return;
                float ox = own.WorldPosition.X / WeatherService.UnitsPerMetre, oy = own.WorldPosition.Y / WeatherService.UnitsPerMetre;
                for (int i = 0; i < presses; i++) {
                    if (!SnowPress.Read(ref r, out SnowPress press)) return;
                    // A client authors only the footprints under its own character, with no more than the weight a body can put on a boot: the weight a footprint is pressed with (PressLoad.BodyMass), not the mass of the ragdoll, which is a tenth of it.
                    if (MathF.Abs(press.X - ox) > OwnReach || press.Bottom > oy + OwnReach || press.Ceiling < oy - OwnReach || press.Pressure > SnowPress.MaximumPressure || press.Half > .5f) continue;
                    if (!peer.Load.Take(Timing.TotalTime, PressLoad.Impulse(press), PressLoad.BodyMass)) continue;
                    SnowPrint.Relay(pack.Material, press, sender.SessionId);
                }
                return;
            }
            if (type == 2 || type == 8) {
                uint epoch = r.U32(); ulong geometry = r.U64(); uint generation = r.U32(), bx = r.U32(), by = r.U32(), revision = type == 2 ? r.U32() : 0;
                if (!r.Failed && r.End && epoch == WeatherService.Epoch && geometry == peer.SnowGeometry && generation == peer.SnowGeneration && snowBlocks != null) {
                    long key = SnowBlocks.Key((int)bx, (int)by);
                    if (type == 8) peer.Need.Add(key);
                    else if (revision <= snowBlocks.Revision(key) && (!peer.BlockAck.TryGetValue(key, out uint known) || known < revision)) { peer.BlockAck[key] = revision; peer.Need.Remove(key); }
                }
                return;
            }
            if (type == 3) {
                uint epoch = r.U32(); float left = r.F32(), bottom = r.F32(), right = r.F32(), top = r.F32();
                var view = new WeatherRect(left, bottom, right, top);
                if (!r.Failed && r.End && epoch == WeatherService.Epoch && float.IsFinite(left) && float.IsFinite(bottom) && float.IsFinite(right) && float.IsFinite(top) && right > left && top > bottom && view.Overlaps(WeatherService.Grid.Bounds)) {
                    peer.View = view.Intersect(WeatherService.Grid.Bounds); peer.ViewAt = Timing.TotalTime;
                }
                return;
            }
            if (type != 1 || Timing.TotalTime < peer.NextCommand) return;
            peer.NextCommand = Timing.TotalTime + .2;
            int count = r.U8(); if (count < 1 || count > 8) return;
            var args = new string[count]; for (int i = 0; i < count; i++) args[i] = r.Text(128);
            if (r.Failed || !r.End) return;
            bool read = args[0] is "dump" or "forecast" || args[0] == "temp" && args.Length == 1 ||
                args[0] == "cold" && args.Length == 2 && args[1] == "status";
            string message;
            if (!read && !Access.Gm(sender)) message = "Для изменения погоды нужны полномочия ConsoleCommands";
            else { try { message = WeatherCommands.Execute(args); } catch (Exception e) { message = e.Message; } }
            foreach (string part in ReplyParts(message)) {
                writer.Clear(); writer.U8(4); writer.U32(WeatherService.Epoch); writer.Text(part);
                ModNet.SendBytes(WeatherWire.Channel, sender.Connection, writer.Span, DeliveryMethod.Reliable);
            }
        }
        // The client reads a reply with Text(4096) and drops the whole packet when it is longer,
        // so long diagnostics such as /cold status are split by line into parts below that limit.
        private const int ReplyPartBytes = 3000;
        private static IEnumerable<string> ReplyParts(string message) {
            var part = new StringBuilder(); int bytes = 0;
            foreach (string line in message.Split('\n')) {
                string piece = line + "\n"; int size = Encoding.UTF8.GetByteCount(piece);
                if (bytes + size > ReplyPartBytes && part.Length > 0) { yield return part.ToString().TrimEnd('\n'); part.Clear(); bytes = 0; }
                part.Append(piece); bytes += size;
            }
            if (part.Length > 0) yield return part.ToString().TrimEnd('\n');
        }
        private static void SendState(Client client, Peer peer, bool snapshot) {
            WeatherDirector director = WeatherService.Director;
            WeatherService.WriteState(writer, snapshot, peer.Revision);
            ModNet.SendBytes(WeatherWire.Channel, client.Connection, writer.Span, DeliveryMethod.Reliable);
            peer.Revision = director.Revision; peer.Epoch = WeatherService.Epoch;
        }
        internal static void Changed() => changed = true;
        internal static void SnowChanged() => nextSnow = 0;
        internal static void Tick() {
            if (GameMain.Server == null || !WeatherService.Active) return;
            SnowPack pack = SnowRuntime.Pack;
            if (pack == null) return;
            if (!ReferenceEquals(blocksPack, pack)) { blocksPack = pack; snowBlocks = new SnowBlocks(pack.Material); snowGeneration++; SnowPrint.Journal.Clear(); }
            SnowPrint.Recording = peers.Count > 0;
            if (Timing.TotalTime >= nextSnow) { nextSnow = Timing.TotalTime + .1; snowBlocks.Refresh(); SendPresses(); }
            foreach (var pair in peers) {
                Client client = pair.Key; Peer peer = pair.Value; if (client.Connection == null) continue;
                if (changed) SendState(client, peer, peer.Epoch != WeatherService.Epoch);
                if (peer.Epoch != WeatherService.Epoch || Timing.TotalTime < peer.NextSnow) continue;
                peer.NextSnow = Timing.TotalTime + .1;
                if (peer.SnowGeometry != WeatherMap.Fingerprint || peer.SnowGeneration != snowGeneration) {
                    peer.SnowGeometry = WeatherMap.Fingerprint; peer.SnowGeneration = snowGeneration; peer.BlockAck.Clear(); peer.BlockSent.Clear(); peer.Need.Clear();
                }
                Interest(client, peer, pack, out float x, out float y); WeatherRect interest = peer.Interest;
                for (int sent = 0; sent < SnowBlocksPerTick; sent++) {
                    long best = 0; bool found = false; double bestScore = 0;
                    IReadOnlyList<long> list = snowBlocks.Changed;
                    for (int i = 0; i < list.Count; i++) {
                        long key = list[i]; WeatherRect box = SnowBlocks.Bounds(key);
                        if (!box.Overlaps(interest)) continue;
                        peer.BlockAck.TryGetValue(key, out uint ack); if (ack == snowBlocks.Revision(key)) continue;
                        peer.BlockSent.TryGetValue(key, out double at);
                        float dx = MathF.Max(0, MathF.Max(box.Left - x, x - box.Right)), dy = MathF.Max(0, MathF.Max(box.Bottom - y, y - box.Top));
                        double score = (Timing.TotalTime - at) / (1 + MathF.Sqrt(dx * dx + dy * dy) / SnowPriorityMetres);
                        if (score > bestScore) { bestScore = score; best = key; found = true; }
                    }
                    if (!found) break;
                    peer.BlockSent.TryGetValue(best, out double sentAt); if (Timing.TotalTime - sentAt < SnowResend) break;
                    peer.BlockSent[best] = Timing.TotalTime;
                    WeatherRect near = SnowBlocks.Bounds(best);
                    float cx = MathF.Max(0, MathF.Max(near.Left - x, x - near.Right)), cy = MathF.Max(0, MathF.Max(near.Bottom - y, y - near.Top));
                    peer.BlockAck.TryGetValue(best, out uint confirmed);
                    bool predicted = !peer.Need.Contains(best) && (snowBlocks.Foreign(best) <= confirmed || client.Character != null && cx * cx + cy * cy < PredictedMetres * PredictedMetres);
                    writer.Clear(); writer.U8(3); writer.U32(WeatherService.Epoch); writer.U64(WeatherMap.Fingerprint); writer.U32(snowGeneration);
                    snowBlocks.Write(writer, best, predicted);
                    ModNet.SendBytes(WeatherWire.Channel, client.Connection, writer.Span, DeliveryMethod.Unreliable);
                }
            }
            changed = false;
        }
        // What a peer is shown: its camera when it reported one lately, otherwise a wide box round its character.
        private static void Interest(Client client, Peer peer, SnowPack pack, out float x, out float y) {
            var observer = client.Character?.WorldPosition ?? client.SpectatePos ?? Submarine.MainSub.WorldPosition;
            x = observer.X / WeatherService.UnitsPerMetre; y = observer.Y / WeatherService.UnitsPerMetre;
            peer.Interest = new WeatherRect(x - 80, y - 45, x + 80, y + 45);
            if (Timing.TotalTime - peer.ViewAt < 2) {
                float margin = pack.MaximumDepth + WeatherService.Grid.Cell * 2;
                peer.Interest = new WeatherRect(peer.View.Left - margin, peer.View.Bottom - margin, peer.View.Right + margin, peer.View.Top + margin);
            }
        }
        // The presses of the last tick go to every peer whose view they fall in, before the blocks they changed, so a peer confirms those blocks from its own replay.
        private const int PressesPerPacket = (WeatherWire.MaximumPacket - 64) / SnowPress.Bytes;
        private static void SendPresses() {
            List<SnowPrint.Entry> journal = SnowPrint.Journal; if (journal.Count == 0) return;
            pressSequence++;
            foreach (var pair in peers) {
                Client client = pair.Key; Peer peer = pair.Value;
                if (client.Connection == null || peer.Epoch != WeatherService.Epoch || peer.SnowGeneration != snowGeneration || peer.SnowGeometry != WeatherMap.Fingerprint) continue;
                Interest(client, peer, blocksPack, out _, out _);
                int start = 0;
                while (start < journal.Count) {
                    int count = 0, end = start;
                    for (; end < journal.Count && count < PressesPerPacket; end++) if (Visible(journal[end], client, peer.Interest)) count++;
                    if (count == 0) break;
                    writer.Clear(); writer.U8(9); writer.U32(WeatherService.Epoch); writer.U64(WeatherMap.Fingerprint); writer.U32(snowGeneration); writer.U32(pressSequence); writer.U16((ushort)count);
                    for (; start < end; start++) if (Visible(journal[start], client, peer.Interest)) journal[start].Press.Write(writer);
                    ModNet.SendBytes(WeatherWire.Channel, client.Connection, writer.Span, DeliveryMethod.Unreliable);
                    if (end < journal.Count) pressSequence++;
                }
            }
            journal.Clear();
        }
        // A peer is not sent its own footprints back: it pressed them first.
        private static bool Visible(in SnowPrint.Entry entry, Client client, in WeatherRect view) =>
            entry.VisibleTo(client.SessionId, client.Character?.ID ?? 0) && entry.Press.X >= view.Left && entry.Press.X <= view.Right && entry.Press.Ceiling >= view.Bottom && entry.Press.Bottom <= view.Top;
        internal static void Reset() { peers.Clear(); changed = true; nextSnow = 0; sequence = snowGeneration = pressSequence = 0; snowBlocks = null; blocksPack = null; SnowPrint.Recording = false; SnowPrint.Journal.Clear(); }
        internal static void Release() => Reset();
#endif
    }
}
