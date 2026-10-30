using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using ScpItdMod.Core;
using ScpItdMod.Net;

namespace ScpItdMod.Rec {
    public static class CamClient {
        public const double ListSeconds = 2.0;
        public const double VoiceWindow = 20.0;

        private static readonly WireWriter w = new WireWriter(128);
        private static readonly List<CamInfo> cameras = new List<CamInfo>();
        private static readonly List<VoicePacket> packets = new List<VoicePacket>();
        private static readonly List<byte[]> owners = new List<byte[]>();
        private static readonly List<FigurePose> poses = new List<FigurePose>(Tape.MaxFigures);
        private static readonly TapeReader reader = new TapeReader();
        private static readonly CamArchive archive = new CamArchive();
        private static uint listGeneration;
        private static double nextList;
        private static double offset;
        private static readonly ServerClock clock = new ServerClock();
        private static uint listSentGeneration;
        private static double listSentAt;
        private static ushort terminal;
        private static uint cameraEpoch;
        private static object voice;
        private static double voiceFrom = double.NaN;
        private static bool voiceSuspended;
        private static bool open;
        private const string VoiceKey = "record_voice";
        private static double nextConsent;
        public static bool RecordVoice => Prefs.Flag(VoiceKey, false);

        public static IReadOnlyList<CamInfo> Cameras => cameras;

        public static CamArchive Archive => archive;

        public static CamDeny Deny { get; private set; }

        public static bool Listed { get; private set; }

        public static bool Open => open;

        public static double ServerNow => Timing.TotalTime + offset;

        public static int Revision { get; private set; }

        public static IReadOnlyList<FigurePose> Frame => poses;

        public static bool InGap { get; private set; }

        public static void Attach() {
            ModNet.ReceiveSpan(CamWire.Channel, ScreenFrameWire.MaxPacketBytes, OnReply);
            ModCommands.Register("scpitd_recordvoice", "разрешить запись своего голоса: true/false; по умолчанию false", args => {
                if (args.Length > 0 && bool.TryParse(args[0], out bool value)) { AllowVoice(value); }
                ModLog.Message(RecordVoice ? "Запись моего голоса разрешена" : "Запись моего голоса запрещена");
            });
        }

        public static void AllowVoice(bool allowed) { Prefs.Set(VoiceKey, allowed ? "1" : "0"); nextConsent = 0; ConsentTick(); }
        public static void ConsentTick() {
            if (GameMain.Client == null || Timing.TotalTime < nextConsent) { return; }
            nextConsent = Timing.TotalTime + 30;
            CamWire.Header(w, CamMsg.VoiceConsent); w.U32(0); w.U32(0); w.Bool(RecordVoice);
            ModNet.SendBytes(CamWire.Channel, w.Span, DeliveryMethod.Reliable);
        }

        public static void Reset() {
            Close();
            cameras.Clear();
            Listed = false;
            Deny = CamDeny.None;
            Revision++;
            nextConsent = 0;
            clock.Reset();
        }

        public static void Begin(ushort terminalId) {
            if (terminal != terminalId) {
                cameras.Clear();
                Listed = false;
            }
            terminal = terminalId;
            open = true;
            Deny = CamDeny.None;
            nextList = 0;
        }

        public static void Close() {
            open = false;
            StopVoice();
            archive.Reset(0);
            reader.Reset();
            poses.Clear();
        }

        public static CamInfo Selected {
            get {
                for (int i = 0; i < cameras.Count; i++) {
                    if (cameras[i].Item == archive.Camera && archive.Camera != 0) { return cameras[i]; }
                }
                return null;
            }
        }

        public static void Select(ushort camera) {
            if (camera == archive.Camera && (camera == 0 || Selected?.Epoch == cameraEpoch)) { return; }
            StopVoice();
            archive.Reset(camera);
            reader.Reset();
            poses.Clear();
            CamInfo info = Selected;
            cameraEpoch = info?.Epoch ?? 0;
            if (info != null) { archive.View.SetRange(info.Oldest, info.Newest); archive.View.GoLive(); }
            Revision++;
        }

        public static void Control(ushort camera, bool record) {
            Header(CamMsg.Control, ++listGeneration);
            w.U32(camera);
            w.Bool(record);
            Send();
        }

        public static void Tick(float dt) {
            if (!open) { return; }
            double now = Timing.TotalTime;
            if (now >= nextList) {
                nextList = now + ListSeconds;
                Header(CamMsg.List, ++listGeneration);
                listSentGeneration = listGeneration;
                listSentAt = now;
                Send();
            }
            if (archive.Camera == 0) { return; }
            Playback view = archive.View;
            view.Advance(dt);
            if (view.TakeRequest(now, out uint generation, out double from, out double to)) {
                Header(CamMsg.Fetch, generation);
                w.U32(archive.Camera);
                w.U64((ulong)BitConverter.DoubleToInt64Bits(from));
                w.U64((ulong)BitConverter.DoubleToInt64Bits(to));
                Send();
            }
            InGap = !reader.Read(archive.Tape, view.Position, poses) || reader.InGap;
            Voice(view);
        }

        private static void Voice(Playback view) {
            bool want = view.Playing && !view.Loading && Math.Abs(view.Speed - 1.0f) < 0.01f;
            if (!want) {
                if (!voiceSuspended) {
                    StopVoice();
                    voiceSuspended = true;
                }
                return;
            }
            bool stale = voice == null || double.IsNaN(voiceFrom) || view.Position < voiceFrom || view.Position > voiceFrom + VoiceWindow - 0.5;
            if (voiceSuspended || stale) {
                StopVoice();
                voiceSuspended = false;
                voiceFrom = view.Position;
                voice = VoiceOps.Create(archive.Voice, voiceFrom, voiceFrom + VoiceWindow, null, packets, owners);
                VoiceOps.Start(voice, null, 1000.0f);
            }
        }

        private static void StopVoice() {
            VoiceOps.Dispose(voice);
            voice = null;
            voiceFrom = double.NaN;
        }

        public static Segment SegmentAt(double t) => archive.Tape.At(t);

        private static void Header(CamMsg msg, uint generation) {
            CamWire.Header(w, msg);
            w.U32(generation);
            w.U32(terminal);
        }

        private static void Send() => ModNet.SendBytes(CamWire.Channel, w.Span, DeliveryMethod.Reliable);

        private static void OnReply(ReadOnlySpan<byte> payload) {
            if (payload.Length >= 2 && payload[0] == CamWire.Proto && (payload[1] == (byte)CamMsg.ScreenFrame || payload[1] == (byte)CamMsg.ScreenPart)) { ScreenCameras.Receive(payload); return; }
            if (payload.Length >= 2 && payload[0] == CamWire.Proto && payload[1] == (byte)CamMsg.MemoryVoice) {
                if (VoiceWire.Read(payload, VoipConfig.MAX_COMPRESSED_SIZE, (float)VoipConfig.BUFFER_SIZE / VoipConfig.FREQUENCY * VoipQueue.BUFFER_COUNT, out VoiceFrame frame)) { Memory.ReceiveVoice(frame); }
                return;
            }
            var r = new WireReader(payload);
            if (r.U8() != CamWire.Proto) { return; }
            var msg = (CamMsg)r.U8();
            uint generation = r.U32();
            switch (msg) {
                case CamMsg.Cameras: {
                    double serverNow = BitConverter.Int64BitsToDouble((long)r.U64());
                    int count = r.Count(CamWire.MaxCameras);
                    var list = new List<CamInfo>(count);
                    for (int i = 0; i < count && !r.Failed; i++) { list.Add(CamInfo.Read(ref r)); }
                    if (r.Failed || !r.End || !double.IsFinite(serverNow)) { return; }
                    for (int i = 0; i < list.Count; i++) { if (list[i].Item == 0 || list[i].Epoch == 0) { return; } }
                    if (generation == listSentGeneration) { clock.Add(listSentAt, Timing.TotalTime, serverNow); }
                    offset = clock.Valid ? clock.Offset : serverNow - Timing.TotalTime;
                    cameras.Clear();
                    cameras.AddRange(list);
                    Listed = true;
                    Deny = CamDeny.None;
                    CamInfo sel = Selected;
                    if (sel != null) {
                        if (sel.Epoch != cameraEpoch) { Select(sel.Item); }
                        archive.View.SetRange(sel.Oldest, sel.Newest);
                    } else if (archive.Camera != 0) {
                        Select(0);
                    }
                    Revision++;
                    break;
                }
                case CamMsg.Chunk: {
                    uint camera = r.U32();
                    if (camera > ushort.MaxValue) { return; }
                    uint previous = archive.View.Generation;
                    archive.Chunk(generation, (ushort)camera, ref r);
                    if (archive.View.Generation != previous) { StopVoice(); reader.Reset(); poses.Clear(); }
                    break;
                }
                case CamMsg.Done: {
                    uint camera = r.U32();
                    if (camera > ushort.MaxValue) { return; }
                    archive.Done(generation, (ushort)camera);
                    break;
                }
                case CamMsg.Denied: {
                    var deny = (CamDeny)r.U8();
                    if (r.Failed) { return; }
                    Deny = deny;
                    archive.View.Denied(generation);
                    if (deny == CamDeny.NoTerminal || deny == CamDeny.TooFar || deny == CamDeny.NoPower) {
                        cameras.Clear();
                        Listed = false;
                        Select(0);
                    }
                    Revision++;
                    break;
                }
            }
        }
    }
}
