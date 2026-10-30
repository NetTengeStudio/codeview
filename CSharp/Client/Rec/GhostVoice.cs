using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using Barotrauma.Sounds;
using Microsoft.Xna.Framework;
using OpenAL;

namespace ScpItdMod.Rec {
    internal sealed class GhostVoice : Sound {
        public const int MaxPackets = 3000;

        private readonly VoiceDecoder.DecodePacket decode = VoiceDecoder.Create();
        private readonly byte[] data;
        private readonly int[] offsets;
        private readonly int[] lengths;
        private readonly long[] starts;
        private readonly short[] frame = new short[VoipConfig.BUFFER_SIZE];
        private readonly long end;
        private readonly int count;
        private int next;
        private int frameAt;
        private int frameLength;
        private long cursor;
        private SoundChannel channel;

        private GhostVoice(byte[] data, int[] offsets, int[] lengths, long[] starts, int count, long end)
            : base(GameMain.SoundManager, "scpitd ghost voice", true, true, getFullPath: false) {
            ALFormat = Al.FormatMono16;
            SampleRate = VoipConfig.FREQUENCY;
            this.data = data;
            this.offsets = offsets;
            this.lengths = lengths;
            this.starts = starts;
            this.count = count;
            this.end = end;
        }

        public override double? DurationSeconds => end / (double)VoipConfig.FREQUENCY;

        public override SoundManager.SourcePoolIndex SourcePoolIndex => SoundManager.SourcePoolIndex.Voice;

        public bool Playing => channel != null && channel.IsPlaying;

        public static GhostVoice Create(VoiceStore store, double from, double to, Subject? only, List<VoicePacket> packets, List<byte[]> owners) {
            if (GameMain.SoundManager == null || GameMain.SoundManager.Disabled) { return null; }
            store.Query(from, to, only, packets, owners);
            int n = Math.Min(packets.Count, MaxPackets);
            if (n == 0) { return null; }
            int bytes = 0;
            for (int i = 0; i < n; i++) { bytes += packets[i].Length; }
            var data = new byte[bytes];
            var offsets = new int[n];
            var lengths = new int[n];
            var starts = new long[n];
            int at = 0;
            for (int i = 0; i < n; i++) {
                VoicePacket p = packets[i];
                store.Payload(owners[i], p).CopyTo(data.AsSpan(at));
                offsets[i] = at;
                lengths[i] = p.Length;
                starts[i] = Math.Max(0L, (long)Math.Round((p.Time - from) * VoipConfig.FREQUENCY));
                at += p.Length;
            }
            long end = Math.Max(starts[n - 1] + VoipConfig.BUFFER_SIZE, (long)Math.Round((to - from) * VoipConfig.FREQUENCY));
            packets.Clear();
            owners.Clear();
            return new GhostVoice(data, offsets, lengths, starts, n, end);
        }

        public void Start(Vector2? at, float range, bool muffle) {
            if (channel != null) { return; }
            Vector3? position = at.HasValue ? new Vector3(at.Value, 0.0f) : null;
            channel = new SoundChannel(this, 1.0f, position, 1.0f, range * 0.4f, range, SoundManager.SoundCategoryVoip, muffle);
        }

        public void Move(Vector2 at) {
            if (channel != null) { channel.Position = new Vector3(at, 0.0f); }
        }

        public void Gain(float gain) {
            if (channel != null) { channel.Gain = gain; }
        }

        public override int FillStreamBuffer(int samplePos, short[] buffer) {
            int written = 0;
            while (written < buffer.Length && cursor < end) {
                if (frameAt < frameLength) {
                    int n = Math.Min(buffer.Length - written, frameLength - frameAt);
                    Array.Copy(frame, frameAt, buffer, written, n);
                    frameAt += n;
                    written += n;
                    cursor += n;
                    continue;
                }
                if (next < count && starts[next] <= cursor) {
                    try {
                        frameLength = decode(data, offsets[next], lengths[next], frame, 0, VoipConfig.BUFFER_SIZE);
                    } catch (Exception) {
                        frameLength = 0;
                    }
                    frameAt = 0;
                    next++;
                    continue;
                }
                long until = next < count ? starts[next] : end;
                int silence = (int)Math.Min(buffer.Length - written, Math.Max(1L, until - cursor));
                Array.Clear(buffer, written, silence);
                written += silence;
                cursor += silence;
            }
            return written;
        }

        public override float GetAmplitudeAtPlaybackPos(int playbackPos) => 0.0f;

        public override void Dispose() {
            channel?.Dispose();
            channel = null;
            base.Dispose();
        }
    }
}
