using System;
using System.Collections.Generic;

namespace ScpItdMod.Meteo {
    public readonly struct MeteoEntry {
        public readonly double Time;
        public readonly ulong Author;
        public readonly string AuthorName, Text;
        public readonly ushort Station;
        public MeteoEntry(double time, ulong author, string authorName, ushort station, string text) { Time = time; Author = author; AuthorName = authorName; Station = station; Text = text; }
    }

    public readonly struct MeteoGap {
        public readonly double From, To;
        public MeteoGap(double from, double to) { From = from; To = to; }
        public double Length => To - From;
    }

    public sealed class MeteoArchive {
        public const int SampleBlocks = 1, EntryBlocks = 4, EntryTextMax = 240, EntriesMax = 256;
        public const int BaseCapacity = 1024, DiskBlocks = 4096, MaxDisks = 8, MinimumCapacity = 64;
        private MeteoSample[] ring;
        private int head, count;
        private readonly List<MeteoEntry> entries = new List<MeteoEntry>(EntriesMax);
        public int Capacity { get; private set; }
        public long Overwritten { get; private set; }
        public long Written { get; private set; }
        public double Oldest { get; private set; }
        public double Newest { get; private set; }
        public int Samples => count;
        public IReadOnlyList<MeteoEntry> Entries => entries;
        public int Used => count * SampleBlocks + entries.Count * EntryBlocks;
        public float Fill => Capacity == 0 ? 1 : Used / (float)Capacity;
        public bool Full => Used >= Capacity;

        public MeteoArchive(int capacity) { Capacity = Math.Max(MinimumCapacity, capacity); ring = new MeteoSample[Capacity]; }

        public static int CapacityFor(int disks) => BaseCapacity + DiskBlocks * Math.Clamp(disks, 0, MaxDisks);

        private int SamplesFor(int capacity) => (capacity - entries.Count * EntryBlocks) / SampleBlocks;
        private static int EntryLimit(int capacity) => Math.Min(EntriesMax, (capacity - SampleBlocks) / EntryBlocks);

        public void Resize(int capacity) {
            capacity = Math.Max(MinimumCapacity, capacity);
            if (capacity == Capacity) return;
            int remove = entries.Count - EntryLimit(capacity);
            if (remove > 0) entries.RemoveRange(0, remove);
            int allowed = SamplesFor(capacity);
            var next = new MeteoSample[capacity];
            int keep = Math.Min(count, allowed);
            for (int i = 0; i < keep; i++) next[keep - 1 - i] = At(count - 1 - i);
            Overwritten += count - keep;
            ring = next; head = keep % capacity; count = keep; Capacity = capacity;
            Oldest = count == 0 ? 0 : At(0).Time;
            Newest = count == 0 ? 0 : At(count - 1).Time;
        }

        public MeteoSample At(int index) {
            if ((uint)index >= (uint)count) throw new ArgumentOutOfRangeException(nameof(index));
            return ring[(head - count + index + ring.Length) % ring.Length];
        }

        public const double LateLimit = 600;

        public bool Add(in MeteoSample s) {
            if (!double.IsFinite(s.Time) || s.Time < 0) return false;
            if (count > 0 && s.Time < Newest) return AddLate(s);
            for (int i = count - 1; i >= 0 && At(i).Time == s.Time; i--) if (At(i).Station == s.Station) return false;
            if (count == SamplesFor(Capacity)) { Overwritten++; count--; }
            ring[head] = s; head = (head + 1) % ring.Length; count++;
            Written++;
            Newest = Math.Max(Newest, s.Time);
            Oldest = At(0).Time;
            return true;
        }

        private void Set(int index, in MeteoSample s) => ring[(head - count + index + ring.Length) % ring.Length] = s;

        private bool AddLate(in MeteoSample s) {
            if (s.Time < Newest - LateLimit || s.Time < Oldest) return false;
            int at = Find(s.Time);
            for (int i = at; i < count && At(i).Time <= s.Time; i++) {
                if (At(i).Station == s.Station && At(i).Time == s.Time) return false;
                at = i + 1;
            }
            if (count == SamplesFor(Capacity)) { Overwritten++; count--; at = Math.Max(0, at - 1); }
            head = (head + 1) % ring.Length; count++;
            for (int i = count - 1; i > at; i--) Set(i, At(i - 1));
            Set(at, s);
            Written++; Oldest = At(0).Time;
            return true;
        }

        public bool AddEntry(in MeteoEntry e) {
            if (!double.IsFinite(e.Time) || e.Time < 0 || string.IsNullOrWhiteSpace(e.Text) || e.Text.Length > EntryTextMax) return false;
            if (entries.Count >= EntryLimit(Capacity)) entries.RemoveAt(0);
            entries.Add(e);
            int allowed = SamplesFor(Capacity);
            if (count > allowed) {
                Overwritten += count - allowed; count = allowed;
                Oldest = count == 0 ? 0 : At(0).Time;
            }
            return true;
        }

        public void Clear() { count = 0; head = 0; entries.Clear(); Written = 0; Overwritten = 0; Oldest = Newest = 0; }

        public int Find(double time) {
            int lo = 0, hi = count;
            while (lo < hi) { int mid = (lo + hi) >> 1; if (At(mid).Time < time) lo = mid + 1; else hi = mid; }
            return lo;
        }

        public bool Latest(ushort station, out MeteoSample s) {
            for (int i = count - 1; i >= 0; i--) { MeteoSample c = At(i); if (station == 0 || c.Station == station) { s = c; return true; } }
            s = default; return false;
        }

        public void Range(ushort station, double from, double to, List<MeteoSample> into, bool autoOnly = false) {
            into.Clear();
            for (int i = Find(from); i < count; i++) {
                MeteoSample s = At(i);
                if (s.Time > to) break;
                if ((station == 0 || s.Station == station) && (!autoOnly || !s.Manual)) into.Add(s);
            }
        }

        public void Decimate(ushort station, double from, double to, int maxPoints, List<MeteoSample> into) {
            into.Clear();
            if (maxPoints <= 0 || to <= from) return;
            double width = (to - from) / maxPoints;
            int index = Find(from);
            for (int bucket = 0; bucket < maxPoints && index < count; bucket++) {
                double end = from + (bucket + 1) * width;
                int n = 0; double time = 0; float t = 0, p = 0, w = 0, sx = 0, sy = 0, h = 0, v = 0, sn = 0, ice = 0;  MeteoFlags flags = MeteoFlags.None; ushort st = 0;
                bool wind = false; int windN = 0; int hn = 0;
                while (index < count) {
                    MeteoSample s = At(index);
                    if (s.Time >= end && bucket < maxPoints - 1) break;
                    index++;
                    if (s.Time > to) { index = count; break; }
                    if ((station != 0 && s.Station != station) || s.Manual) continue;
                    n++; time += s.Time; t += s.Temperature; p += s.Pressure; v += MathF.Log(s.Visibility); sn += s.Snow; ice = MathF.Max(ice, s.Ice); flags |= s.Flags; st = s.Station;
                    if (s.WindValid) { w += s.Wind; sx += MathF.Sin(s.Direction) * s.Wind; sy += MathF.Cos(s.Direction) * s.Wind; windN++; wind = true; }
                    if (s.HumidityValid) { h += s.Humidity; hn++; }
                }
                if (n == 0) continue;
                float dir = wind && (sx != 0 || sy != 0) ? MathF.Atan2(sx, sy) : 0;
                float avgWind = windN > 0 ? w / windN : 0;
                MeteoFlags f = flags & ~MeteoFlags.HumidityInvalid;
                if (windN == 0) f |= MeteoFlags.WindStalled;
                if (hn == 0) f |= MeteoFlags.HumidityInvalid;
                into.Add(new MeteoSample(time / n, st, t / n, p / n, avgWind, dir, hn > 0 ? h / hn : 0, MathF.Exp(v / n), sn / n, ice, f));
            }
        }

        public void Gaps(ushort station, double from, double to, double minimum, List<MeteoGap> into) {
            into.Clear();
            double last = double.NaN;
            for (int i = Find(from); i < count; i++) {
                MeteoSample s = At(i);
                if (s.Time > to) break;
                if ((station != 0 && s.Station != station) || s.Manual) continue;
                if (!double.IsNaN(last) && s.Time - last > minimum) into.Add(new MeteoGap(last, s.Time));
                last = s.Time;
            }
        }
    }
}
