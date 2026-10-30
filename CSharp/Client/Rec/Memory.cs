using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Lights;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;
using ScpItdMod.Blink;
using ScpItdMod.Core;
using ScpItdMod.People;
using ScpItdMod.Rp;

namespace ScpItdMod.Rec {
    public static class Memory {
        public const double SampleSeconds = 0.1;
        public const double SightSeconds = 0.5;
        public const double LightHullSeconds = 5.0;
        public const double PruneSeconds = 15.0;
        public const long TapeBudget = 4L << 20;
        public const long VoiceBudget = 2L << 20;
        public const double KeepSeconds = 60.0 * 60.0;

        private static readonly LookTable looks = new LookTable(1024);
        private static readonly Tape tape = new Tape(TapeBudget, KeepSeconds, looks);
        private static readonly VoiceStore voice = new VoiceStore(VoiceBudget, KeepSeconds);
        private static readonly RecallIndex index = new RecallIndex();
        private static readonly Capture capture = new Capture(looks, c => Names.Witnessed(c, Sense.Sight));
        private static readonly Dictionary<Character, (bool Seen, double Until)> sight = new Dictionary<Character, (bool, double)>();
        private struct PendingVoice {
            public Client Client;
            public Character Speaker;
            public double Time;
            public ushort Buffer;
            public byte Size;
            public float Lag;
            public bool Radio;
        }
        private const int VoiceSlots = 256 * VoipQueue.BUFFER_COUNT;
        private static readonly PendingVoice[] pendingVoice = new PendingVoice[VoiceSlots];
        private static readonly byte[] pendingData = new byte[VoiceSlots * VoipConfig.MAX_COMPRESSED_SIZE];
        private static readonly ulong[] pendingMask = new ulong[(VoiceSlots + 63) / 64];
        private sealed class HeardVoice { public VoiceWindow Window; }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Client, HeardVoice> heardVoice = new System.Runtime.CompilerServices.ConditionalWeakTable<Client, HeardVoice>();
        private static readonly List<(LightSource Light, Hull Hull)> lights = new List<(LightSource, Hull)>(256);
        private static readonly List<Character> stale = new List<Character>();
        private static Character owner;
        private static double nextSample;
        private static double nextLights;
        private static double nextPrune;

        public static Tape Tape => tape;

        public static LookTable Looks => looks;

        public static VoiceStore Voice => voice;

        public static RecallIndex Index => index;

        public static Character Owner => owner;

        public static long Bytes => tape.Bytes + voice.Bytes + looks.Bytes;

        public static void Attach(Harmony harmony) {
            harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.ControlLocalPlayer)),
                postfix: ModuleHost.Bind(typeof(Memory), nameof(Tick)));
            harmony.Patch(AccessTools.Method(typeof(ChatBox), nameof(ChatBox.AddMessage)),
                postfix: ModuleHost.Bind(typeof(Memory), nameof(ChatPostfix)));
            harmony.Patch(AccessTools.Method(typeof(VoipClient), nameof(VoipClient.Read)),
                postfix: ModuleHost.Bind(typeof(Memory), nameof(VoicePostfix)));
            EmoteView.Heard += OnEmote;
            ModCommands.Register("scpitd_memory", "client: what the controlled character remembers", _ => Report());
        }

        public static void Detach() => EmoteView.Heard -= OnEmote;

        public static void Reset() {
            tape.Clear();
            voice.Clear();
            capture.Clear();
            looks.Clear();
            index.Clear();
            sight.Clear();
            Array.Clear(pendingVoice); Array.Clear(pendingMask); heardVoice.Clear();
            lights.Clear();
            owner = null;
            nextSample = nextLights = nextPrune = 0;
        }

        private static bool Own(Character me) {
            if (me == null || me.Removed) { return false; }
            if (!ReferenceEquals(owner, me)) {
                Reset();
                owner = me;
                Visions.Reset();
            }
            return !me.IsDead;
        }

        public static void Tick(Character __instance) {
            if (!ReferenceEquals(__instance, Character.Controlled) || !Own(__instance)) { return; }
            double now = Timing.TotalTime;
            VoicePostfix();
            if (now < nextSample) { return; }
            nextSample = now + SampleSeconds;
            if (now >= nextLights) {
                nextLights = now + LightHullSeconds;
                MapLights();
            }
            if (now >= nextPrune) {
                nextPrune = now + PruneSeconds;
                Prune(now);
            }
            Sample(__instance, now);
        }

        private static bool Able(Character me) => RpWorld.Conscious(me) && BlinkClient.EyesOpen;

        private static void Sample(Character me, double now) {
            tape.BeginFrame(now);
            if (Able(me)) {
                var humans = Humans.All;
                for (int i = 0; i < humans.Count; i++) {
                    Character c = humans[i];
                    if (ReferenceEquals(c, me) || !Capture.Recordable(c) || !Sees(me, c, now)) { continue; }
                    if (!capture.Pose(c, out FigurePose p)) { continue; }
                    tape.Figure(p);
                    index.Mark(p.Subject, Witness.Seen, now, p.X, p.Y);
                }
            }
            tape.EndFrame();
        }

        private static bool Sees(Character me, Character c, double now) {
            if (sight.TryGetValue(c, out var cached) && now < cached.Until) { return cached.Seen; }
            Vector2 at = c.WorldPosition;
            var f = new SightFacts {
                Self = false,
                Able = true,
                Hidden = RpWorld.IsHidden(c),
                InView = c.hudInfoVisible,
                LineClear = c.hudInfoVisible,
                Distance = Vector2.Distance(me.WorldPosition, at),
                Light = LightAt(at, c.CurrentHull)
            };
            bool seen = Perception.Seen(f);
            sight[c] = (seen, now + SightSeconds);
            return seen;
        }

        private static void MapLights() {
            lights.Clear();
            if (GameMain.LightManager == null) { return; }
            foreach (LightSource l in GameMain.LightManager.Lights) {
                if (l == null || !l.Enabled || l.Range <= 0.0f) { continue; }
                lights.Add((l, Hull.FindHull(l.WorldPosition, null, useWorldCoordinates: true)));
                if (lights.Count >= 512) { break; }
            }
        }

        private static float LightAt(Vector2 at, Hull hull) {
            if (GameMain.LightManager == null || !GameMain.LightManager.LightingEnabled) { return 1.0f; }
            Color ambient = hull == null ? GameMain.LightManager.AmbientLight : hull.AmbientLight;
            float total = Perception.Luminance(ambient.R, ambient.G, ambient.B) * (hull == null ? 1 : ambient.A / 255f)
                + Weather.WeatherService.SkylightAt(at).Luma;
            for (int i = 0; i < lights.Count && total < Perception.DarkLight; i++) {
                (LightSource l, Hull lh) = lights[i];
                if (!l.Enabled || lh != null && hull != null && !ReferenceEquals(lh, hull)) { continue; }
                Color c = l.Color;
                total += Perception.Falloff(Vector2.Distance(at, l.WorldPosition), l.Range, l.CurrentBrightness, Perception.Luminance(c.R, c.G, c.B) * c.A / 255.0f);
            }
            return total;
        }

        private static void Prune(double now) {
            capture.Prune();
            stale.Clear();
            foreach (var pair in sight) {
                if (pair.Key.Removed || now > pair.Value.Until + 5.0) { stale.Add(pair.Key); }
            }
            for (int i = 0; i < stale.Count; i++) { sight.Remove(stale[i]); }
            index.Trim(Math.Min(tape.Oldest, now - KeepSeconds));
        }

        private static bool Hears(Character speaker, bool radio, float range) {
            Character me = Character.Controlled;
            if (me == null || !ReferenceEquals(me, owner) || me.IsDead || speaker == null || ReferenceEquals(speaker, me)) { return false; }
            var f = new HearFacts {
                Able = RpWorld.Conscious(me),
                Delivered = true,
                Radio = radio,
                RadioHeld = radio,
                Distance = Vector2.Distance(me.WorldPosition, speaker.WorldPosition),
                Range = range
            };
            return Perception.Heard(f);
        }

        public static void ChatPostfix(ChatMessage message) {
            if (message == null || owner == null) { return; }
            if (message.Type != ChatMessageType.Default && message.Type != ChatMessageType.Radio) { return; }
            Character speaker = message.SenderCharacter;
            bool radio = message.Type == ChatMessageType.Radio;
            if (!Hears(speaker, radio, ChatMessage.SpeakRange)) { return; }
            double now = Timing.TotalTime;
            Subject s = Capture.SubjectOf(speaker);
            index.Mark(s, Witness.Heard, now, speaker.WorldPosition.X, speaker.WorldPosition.Y);
            tape.Note(new TapeNote { Time = now, Kind = TapeEvent.Speech, Subject = s, Value = radio ? 1u : 0u, Text = message.TranslatedText });
        }

        private static void OnEmote(Character actor, EmoteDef def, int variant) {
            if (owner == null || def == null || !Hears(actor, false, MathF.Max(200.0f, def.Range))) { return; }
            double now = Timing.TotalTime;
            Subject s = Capture.SubjectOf(actor);
            index.Mark(s, Witness.Heard, now, actor.WorldPosition.X, actor.WorldPosition.Y);
            tape.Note(new TapeNote { Time = now, Kind = TapeEvent.Emote, Subject = s, Value = (uint)(def.Index | variant << 8), Text = def.Id });
        }

        public static void ReceiveVoice(in VoiceFrame frame) {
            GameClient client = GameMain.Client;
            if (client == null || !Own(Character.Controlled)) { return; }
            Client source = null;
            foreach (Client candidate in client.ConnectedClients) {
                if (candidate.SessionId == frame.Client) { source = candidate; break; }
            }
            Character speaker = source?.Character;
            if (speaker == null || speaker.ID != frame.Subject.Entity || source.Muted || source.MutedLocally) { return; }
            int slot = frame.Client * VoipQueue.BUFFER_COUNT + frame.Buffer % VoipQueue.BUFFER_COUNT;
            pendingVoice[slot] = new PendingVoice { Client = source, Speaker = speaker, Time = Timing.TotalTime, Buffer = frame.Buffer, Size = (byte)frame.Data.Length, Lag = frame.Lag, Radio = frame.Radio };
            frame.Data.CopyTo(pendingData.AsSpan(slot * VoipConfig.MAX_COMPRESSED_SIZE, frame.Data.Length));
            pendingMask[slot >> 6] |= 1UL << (slot & 63);
            VoicePostfix();
        }

        public static void VoicePostfix() {
            if (GameMain.Client == null || owner == null) { return; }
            double now = Timing.TotalTime;
            for (int word = 0; word < pendingMask.Length; word++) {
                ulong mask = pendingMask[word];
                while (mask != 0) {
                    int bit = System.Numerics.BitOperations.TrailingZeroCount(mask); mask &= mask - 1;
                    int slot = word * 64 + bit;
                    ref PendingVoice packet = ref pendingVoice[slot];
                    Client c = packet.Client; Character speaker = packet.Speaker;
                    bool discard = c == null || speaker == null || speaker.Removed || !ReferenceEquals(c.Character, speaker) || c.Muted || c.MutedLocally ||
                        c.VoiceVolume <= 0 || GameSettings.CurrentConfig.Audio.VoiceChatVolume <= 0 || now - packet.Time > 0.2 || !Hears(speaker, packet.Radio, ChatMessage.SpeakRangeVOIP);
                    VoipQueue q = c?.VoipQueue;
                    if (!discard && q != null) {
                        ushort age = (ushort)(q.LatestBufferID - packet.Buffer);
                        if (age < VoipQueue.BUFFER_COUNT) {
                            q.RetrieveBuffer(packet.Buffer, out int size, out byte[] data);
                            if (size == packet.Size && data != null && data.AsSpan(0, size).SequenceEqual(pendingData.AsSpan(slot * VoipConfig.MAX_COMPRESSED_SIZE, size))) {
                                if (heardVoice.GetValue(c, static _ => new HeardVoice()).Window.Accept(packet.Buffer)) {
                                    Subject subject = Capture.SubjectOf(speaker);
                                    voice.Add(packet.Time - packet.Lag, subject, packet.Radio ? (byte)1 : (byte)0, data.AsSpan(0, size));
                                    index.Mark(subject, Witness.Heard, packet.Time, speaker.WorldPosition.X, speaker.WorldPosition.Y);
                                }
                                discard = true;
                            }
                        } else if (age < 32768) { discard = true; }
                    }
                    if (discard) { packet = default; pendingMask[word] &= ~(1UL << bit); }
                }
            }
        }

        private static void Report() {
            ModLog.Message($"memory of {owner?.Name ?? "nobody"}: {Bytes / 1024} KB (tape {tape.Bytes / 1024}/{TapeBudget / 1024} KB, {tape.Segments} segs, thinned {tape.ThinnedCount}, evicted {tape.Evicted}; voice {voice.Packets} pk {voice.Bytes / 1024} KB; looks {looks.Live}; spans {index.Count}); visions {Visions.Describe()}");
        }
    }
}
