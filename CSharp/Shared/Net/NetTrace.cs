using System;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace ScpItdMod.Net {
    public static class NetTrace {
        public const int Capacity = 4096;
        public readonly struct Measurement {
            internal readonly long Start, Allocated;
            internal readonly bool Active;
            internal Measurement(bool active) { Active = active; Start = active ? Stopwatch.GetTimestamp() : 0; Allocated = active ? GC.GetAllocatedBytesForCurrentThread() : 0; }
        }
        private struct Entry {
            public long Ordinal, Tick, Duration, Allocated;
            public string Channel, Event;
            public ulong Peer, Entity, State;
            public uint Epoch, Sequence, Revision;
            public int Bytes, Phase;
            public double ServerTime;
        }
        private static readonly object gate = new object();
        private static Entry[] entries;
        private static long written;
        private static volatile bool enabled;
        public static bool Enabled => enabled;
        public static void Enable(bool value) {
            lock (gate) {
                if (value && entries == null) entries = new Entry[Capacity];
                enabled = value;
            }
        }
        public static void Reset() {
            lock (gate) { enabled = false; entries = null; written = 0; }
        }
        public static Measurement Start() => new Measurement(enabled);
        public static void Finish(in Measurement measurement, string channel, string kind, int bytes, ulong peer = 0, uint sequence = 0) {
            if (!measurement.Active) return;
            Append(new Entry { Channel = channel, Event = kind, Bytes = bytes, Peer = peer, Sequence = sequence,
                Duration = Stopwatch.GetTimestamp() - measurement.Start, Allocated = GC.GetAllocatedBytesForCurrentThread() - measurement.Allocated });
        }
        public static void State(string channel, string kind, ulong entity, uint epoch, uint sequence, uint revision, int phase, ulong state, double serverTime = 0) {
            if (!enabled) return;
            Append(new Entry { Channel = channel, Event = kind, Entity = entity, Epoch = epoch, Sequence = sequence, Revision = revision, Phase = phase, State = state, ServerTime = serverTime });
        }
        private static void Append(Entry entry) {
            lock (gate) {
                if (!enabled || entries == null) return;
                entry.Ordinal = ++written; entry.Tick = Stopwatch.GetTimestamp();
                entries[(int)((written - 1) % Capacity)] = entry;
            }
        }
        public static void Dump(Action<string> write) {
            if (write == null) throw new ArgumentNullException(nameof(write));
            Entry[] snapshot;
            long total;
            lock (gate) {
                total = written;
                int count = (int)Math.Min(total, Capacity);
                snapshot = new Entry[count];
                for (int i = 0; i < count; i++) snapshot[i] = entries[(int)((total - count + i) % Capacity)];
            }
            write("SCPITD_TRACE " + JsonSerializer.Serialize(new { kind = "header", version = 1, frequency = Stopwatch.Frequency, retained = snapshot.Length, overwritten = Math.Max(0, total - Capacity) }));
            foreach (Entry entry in snapshot) write("SCPITD_TRACE " + JsonSerializer.Serialize(new {
                ordinal = entry.Ordinal, tick = entry.Tick, channel = entry.Channel, kind = entry.Event,
                peer = entry.Peer.ToString("x16", CultureInfo.InvariantCulture), entity = entry.Entity.ToString("x16", CultureInfo.InvariantCulture),
                epoch = entry.Epoch, sequence = entry.Sequence, revision = entry.Revision, phase = entry.Phase,
                state = entry.State.ToString("x16", CultureInfo.InvariantCulture), serverTime = entry.ServerTime,
                bytes = entry.Bytes, durationTicks = entry.Duration, allocated = entry.Allocated
            }));
        }
    }
}
