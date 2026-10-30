using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace ScpItdMod.Rec {
    public struct VoicePacket {
        public double Time;
        public Subject Subject;
        public int Offset;
        public int Length;
        public byte Channel;
    }

    public sealed class VoiceStore {
        public const int ChunkBytes = 64 * 1024;
        public const int MaxPacket = 1500;

        private sealed class Chunk {
            public readonly byte[] Data = new byte[ChunkBytes];
            public int Used;
            public readonly List<VoicePacket> Packets = new List<VoicePacket>(256);
            public double Start = double.MaxValue;
            public double End = double.MinValue;
        }

        private readonly List<Chunk> chunks = new List<Chunk>();
        private static readonly int PacketBytes = Unsafe.SizeOf<VoicePacket>();
        public static long MinimumBudget => ChunkBytes + 256L * PacketBytes;
        private long budget, bytes;
        private int maxHeaders;
        private readonly double keep;

        public VoiceStore(long budgetBytes, double keepSeconds) {
            keep = keepSeconds;
            Resize(budgetBytes);
        }

        public long Bytes => bytes;

        public long Budget => budget;

        public void Resize(long budgetBytes) {
            if (budgetBytes <= 0) { throw new ArgumentOutOfRangeException(nameof(budgetBytes)); }
            budget = Math.Max(MinimumBudget, budgetBytes);
            int maximum = (int)Math.Min(ChunkBytes, (budget - ChunkBytes) / PacketBytes);
            maxHeaders = 1 << System.Numerics.BitOperations.Log2((uint)maximum);
            Enforce();
            if (chunks.Count == 1 && bytes > budget) {
                Chunk source = chunks[0], replacement = new Chunk();
                int first = Math.Max(0, source.Packets.Count - maxHeaders);
                for (int i = first; i < source.Packets.Count; i++) {
                    VoicePacket packet = source.Packets[i];
                    source.Data.AsSpan(packet.Offset, packet.Length).CopyTo(replacement.Data.AsSpan(replacement.Used));
                    packet.Offset = replacement.Used; replacement.Used += packet.Length;
                    replacement.Packets.Add(packet);
                    replacement.Start = Math.Min(replacement.Start, packet.Time); replacement.End = packet.Time;
                }
                chunks[0] = replacement; bytes = Size(replacement); Dropped++;
            }
        }

        private static long Size(Chunk chunk) => ChunkBytes + (long)chunk.Packets.Capacity * PacketBytes;

        private void Enforce() {
            while (chunks.Count > 1 && (bytes > budget || Newest - chunks[0].End > keep)) {
                bytes -= Size(chunks[0]); Recycle(chunks[0]); chunks.RemoveAt(0); Dropped++;
            }
        }

        public int Packets {
            get {
                int n = 0;
                foreach (Chunk c in chunks) { n += c.Packets.Count; }
                return n;
            }
        }

        public double Oldest => chunks.Count == 0 ? double.MaxValue : chunks[0].Start;

        public int Dropped { get; private set; }

        public double Newest { get; private set; } = double.MinValue;

        public void Clear() {
            foreach (Chunk c in chunks) { Recycle(c); }
            chunks.Clear();
            bytes = 0;
            Newest = double.MinValue;
        }

        public bool Add(double time, Subject subject, byte channel, ReadOnlySpan<byte> packet) {
            if (!double.IsFinite(time) || packet.Length == 0 || packet.Length > MaxPacket) { return false; }
            Chunk c = chunks.Count > 0 ? chunks[chunks.Count - 1] : null;
            if (c == null || c.Used + packet.Length > ChunkBytes || time < c.End || c.Packets.Count >= maxHeaders) {
                c = new Chunk();
                chunks.Add(c);
                bytes += Size(c);
            }
            packet.CopyTo(c.Data.AsSpan(c.Used));
            int capacity = c.Packets.Capacity;
            c.Packets.Add(new VoicePacket { Time = time, Subject = subject, Offset = c.Used, Length = packet.Length, Channel = channel });
            bytes += (long)(c.Packets.Capacity - capacity) * PacketBytes;
            c.Used += packet.Length;
            if (time < c.Start) { c.Start = time; }
            c.End = time;
            if (time > Newest) { Newest = time; }
            Enforce();
            return true;
        }

        public int Query(double t0, double t1, Subject? only, List<VoicePacket> into, List<byte[]> owners) {
            into.Clear();
            owners.Clear();
            for (int ci = 0; ci < chunks.Count; ci++) {
                Chunk c = chunks[ci];
                if (c.End < t0 || c.Start > t1) { continue; }
                int lo = 0;
                int hi = c.Packets.Count;
                while (lo < hi) {
                    int mid = (lo + hi) >> 1;
                    if (c.Packets[mid].Time < t0) { lo = mid + 1; } else { hi = mid; }
                }
                for (int i = lo; i < c.Packets.Count && c.Packets[i].Time <= t1; i++) {
                    VoicePacket p = c.Packets[i];
                    if (only.HasValue && !p.Subject.Equals(only.Value)) { continue; }
                    into.Add(p);
                    owners.Add(c.Data);
                }
            }
            if (chunks.Count > 1) { Sort(into, owners); }
            return into.Count;
        }

        private static void Sort(List<VoicePacket> into, List<byte[]> owners) {
            for (int i = 1; i < into.Count; i++) {
                VoicePacket p = into[i];
                byte[] o = owners[i];
                int j = i - 1;
                while (j >= 0 && into[j].Time > p.Time) {
                    into[j + 1] = into[j];
                    owners[j + 1] = owners[j];
                    j--;
                }
                into[j + 1] = p;
                owners[j + 1] = o;
            }
            int w = 0;
            for (int i = 0; i < into.Count; i++) {
                if (w > 0 && into[w - 1].Time == into[i].Time && into[w - 1].Subject.Equals(into[i].Subject)) { continue; }
                into[w] = into[i];
                owners[w] = owners[i];
                w++;
            }
            into.RemoveRange(w, into.Count - w);
            owners.RemoveRange(w, owners.Count - w);
        }

        public ReadOnlySpan<byte> Payload(byte[] owner, in VoicePacket p) => new ReadOnlySpan<byte>(owner, p.Offset, p.Length);

        private static void Recycle(Chunk c) => c.Packets.Clear();
    }
}
