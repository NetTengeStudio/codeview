using System;
using System.Collections.Generic;
using ScpItdMod.Net;
using ScpItdMod.Weather;

namespace ScpItdMod.Meteo {
    public enum MeteoOp : byte { Overview, Series, Journal, Note, Bulletin, Alert }
    public enum MeteoReply : byte { Overview, Series, Journal, Ack, Reading, Alert }
    public enum MeteoError : byte { Ok, NoPower, NoLink, Far, Denied, Invalid, Full, Rate, Busy }
    public enum MeteoKind : byte { Station, Barograph }

    public struct MeteoStationInfo {
        public ushort Id;
        public string Name;
        public MeteoKind Kind;
        public bool Powered, Linked, Heated;
        public double Last;
        public float Ice;
    }

    public sealed class MeteoOverview {
        public bool RackPresent, RackPowered;
        public int Capacity, Used, Disks;
        public uint Written, Overwritten;
        public double Oldest, Newest, Now, Day;
        public bool Frozen;
        public string RackName = "";
        public readonly List<MeteoStationInfo> Stations = new List<MeteoStationInfo>();
        public ushort Selected;
        public bool HasLatest;
        public MeteoSample Latest;
        public MeteoForecast Forecast;
        public double LastAlert;
    }

    public sealed class MeteoSeries {
        public ushort Station;
        public double From, To;
        public readonly List<MeteoSample> Samples = new List<MeteoSample>();
        public readonly List<MeteoGap> Gaps = new List<MeteoGap>();
    }

    public static class MeteoWire {
        public const string Channel = "scpitd.meteo.v1";
        public const byte Proto = 1;
        public const int MaxRequest = 512, MaxReply = 8192, MaxStations = 12, MaxPoints = 180, MaxEntries = 24, MaxGaps = 24, NameMax = 40, AlertMax = 160;
        public const string RackTag = "scpitd_server", StationTag = "scpitd_met_station", BarographTag = "scpitd_barograph", DiskTag = "scpitd_disk";

        public static void Request(WireWriter w, MeteoOp op, uint token, ushort computer) {
            w.Clear(); w.U8(Proto); w.U8((byte)op); w.U32(token); w.U32(computer);
        }

        public static bool ReadRequest(ref WireReader r, out MeteoOp op, out uint token, out ushort computer) {
            op = default; token = 0; computer = 0;
            if (r.U8() != Proto) return false;
            op = (MeteoOp)r.U8(); token = r.U32(); uint c = r.U32();
            if (r.Failed || c == 0 || c > ushort.MaxValue || token == 0 || op > MeteoOp.Alert) return false;
            computer = (ushort)c; return true;
        }

        public static void Header(WireWriter w, MeteoReply kind, uint token, MeteoError error) {
            w.Clear(); w.U8(Proto); w.U8((byte)kind); w.U32(token); w.U8((byte)error);
        }

        public static bool ReadHeader(ref WireReader r, out MeteoReply kind, out uint token, out MeteoError error) {
            kind = default; token = 0; error = default;
            if (r.U8() != Proto) return false;
            kind = (MeteoReply)r.U8(); token = r.U32(); error = (MeteoError)r.U8();
            return !r.Failed && kind <= MeteoReply.Alert && error <= MeteoError.Busy;
        }

        public static void Write(WireWriter w, MeteoOverview o) {
            w.Bool(o.RackPresent); w.Bool(o.RackPowered); w.Var((ulong)o.Capacity); w.Var((ulong)o.Used); w.U8((byte)o.Disks); w.U32(o.Written); w.U32(o.Overwritten);
            WeatherWire.Double(w, o.Oldest); WeatherWire.Double(w, o.Newest); WeatherWire.Double(w, o.Now); WeatherWire.Double(w, o.Day); w.Bool(o.Frozen); w.Text(o.RackName);
            WeatherWire.Double(w, o.LastAlert);
            w.U8((byte)o.Stations.Count);
            foreach (MeteoStationInfo s in o.Stations) {
                w.U16(s.Id); w.Text(s.Name); w.U8((byte)s.Kind); w.Bool(s.Powered); w.Bool(s.Linked); w.Bool(s.Heated); WeatherWire.Double(w, s.Last); w.F32(s.Ice);
            }
            w.U16(o.Selected); w.Bool(o.HasLatest);
            if (o.HasLatest) MeteoCodec.Write(w, o.Latest);
            MeteoForecast f = o.Forecast;
            w.Bool(f.Valid);
            if (!f.Valid) return;
            w.U16((ushort)Math.Min(f.Samples, ushort.MaxValue)); w.U8((byte)f.Observed); w.F32(f.Age); w.Bool(f.AgeExact); w.F32(f.PressureSlope); w.F32(f.PressureDrop); w.U8((byte)f.Trend); w.Bool(f.Precursor);
            w.U8((byte)f.Likely); for (int i = 0; i < 6; i++) w.U8((byte)Math.Clamp((int)MathF.Round(f.Probability[i] * 255), 0, 255));
            w.F32(f.EtaLow); w.F32(f.EtaMid); w.F32(f.EtaHigh); w.F32(f.Confidence); w.F32(f.CoverageFraction); w.F32(f.TemperatureNext); w.F32(f.WindNext); w.F32(f.VisibilityNext); w.U8((byte)f.Alert);
        }

        public static bool Read(ref WireReader r, MeteoOverview o) {
            o.RackPresent = r.Bool(); o.RackPowered = r.Bool(); o.Capacity = (int)r.Var(); o.Used = (int)r.Var(); o.Disks = r.U8(); o.Written = r.U32(); o.Overwritten = r.U32();
            o.Oldest = WeatherWire.Double(ref r); o.Newest = WeatherWire.Double(ref r); o.Now = WeatherWire.Double(ref r); o.Day = WeatherWire.Double(ref r); o.Frozen = r.Bool(); o.RackName = r.Text(NameMax * 4);
            o.LastAlert = WeatherWire.Double(ref r);
            int count = r.U8(); o.Stations.Clear();
            if (count > MaxStations || r.Failed) return false;
            for (int i = 0; i < count; i++) {
                var s = new MeteoStationInfo { Id = r.U16(), Name = r.Text(NameMax * 4), Kind = (MeteoKind)r.U8(), Powered = r.Bool(), Linked = r.Bool(), Heated = r.Bool(), Last = WeatherWire.Double(ref r), Ice = r.F32() };
                if (r.Failed || s.Kind > MeteoKind.Barograph) return false;
                o.Stations.Add(s);
            }
            o.Selected = r.U16(); o.HasLatest = r.Bool();
            if (o.HasLatest && !MeteoCodec.Read(ref r, out o.Latest)) return false;
            var f = new MeteoForecast { Probability = o.Forecast.Probability ?? new float[6] };
            f.Valid = r.Bool();
            if (f.Valid) {
                f.Samples = r.U16(); f.Observed = (WeatherRegime)r.U8(); f.Age = r.F32(); f.AgeExact = r.Bool(); f.PressureSlope = r.F32(); f.PressureDrop = r.F32(); f.Trend = (MeteoTrend)r.U8(); f.Precursor = r.Bool();
                f.Likely = (WeatherRegime)r.U8(); for (int i = 0; i < 6; i++) f.Probability[i] = r.U8() / 255f;
                f.EtaLow = r.F32(); f.EtaMid = r.F32(); f.EtaHigh = r.F32(); f.Confidence = r.F32(); f.CoverageFraction = r.F32(); f.TemperatureNext = r.F32(); f.WindNext = r.F32(); f.VisibilityNext = r.F32(); f.Alert = (MeteoAlert)r.U8();
                if ((byte)f.Observed > 5 || (byte)f.Likely > 5 || (byte)f.Trend > 4 || (byte)f.Alert > 3 ||
                    !float.IsFinite(f.Age) || f.Age < 0 || !float.IsFinite(f.PressureSlope) || !float.IsFinite(f.PressureDrop) ||
                    !float.IsFinite(f.EtaLow) || !float.IsFinite(f.EtaMid) || !float.IsFinite(f.EtaHigh) ||
                    f.EtaLow < 0 || f.EtaMid < f.EtaLow || f.EtaHigh < f.EtaMid ||
                    !float.IsFinite(f.Confidence) || f.Confidence < 0 || f.Confidence > 1 ||
                    !float.IsFinite(f.CoverageFraction) || f.CoverageFraction < 0 || f.CoverageFraction > 1 ||
                    !float.IsFinite(f.TemperatureNext) || !float.IsFinite(f.WindNext) || f.WindNext < 0 ||
                    !float.IsFinite(f.VisibilityNext) || f.VisibilityNext <= 0) return false;
            }
            f.Now = o.Now; o.Forecast = f;
            return !r.Failed && r.End && double.IsFinite(o.Now) && double.IsFinite(o.Day) &&
                o.Capacity >= 0 && o.Used >= 0 && o.Used <= o.Capacity && (!o.HasLatest || MeteoPlotMath.Finite(o.Latest));
        }

        public static void Write(WireWriter w, MeteoSeries s) {
            w.U16(s.Station); WeatherWire.Double(w, s.From); WeatherWire.Double(w, s.To);
            w.U16((ushort)s.Samples.Count); foreach (MeteoSample m in s.Samples) MeteoCodec.Write(w, m);
            w.U8((byte)s.Gaps.Count); foreach (MeteoGap g in s.Gaps) { WeatherWire.Double(w, g.From); WeatherWire.Double(w, g.To); }
        }

        public static bool Read(ref WireReader r, MeteoSeries s) {
            s.Station = r.U16(); s.From = WeatherWire.Double(ref r); s.To = WeatherWire.Double(ref r);
            int count = r.U16(); s.Samples.Clear(); s.Gaps.Clear();
            if (count > MaxPoints || r.Failed || r.Remaining < count * MeteoCodec.Bytes ||
                !double.IsFinite(s.From) || !double.IsFinite(s.To) || s.To < s.From) return false;
            double previous = double.NegativeInfinity;
            for (int i = 0; i < count; i++) {
                if (!MeteoCodec.Read(ref r, out MeteoSample m) || !MeteoPlotMath.Finite(m) || m.Station != s.Station ||
                    m.Time <= previous || m.Time < s.From - MeteoCodec.TimeStep || m.Time > s.To + MeteoCodec.TimeStep) return false;
                s.Samples.Add(m); previous = m.Time;
            }
            int gaps = r.U8(); if (gaps > MaxGaps) return false;
            previous = double.NegativeInfinity;
            for (int i = 0; i < gaps; i++) {
                double from = WeatherWire.Double(ref r), to = WeatherWire.Double(ref r);
                if (!double.IsFinite(from) || !double.IsFinite(to) || to <= from || from < previous) return false;
                s.Gaps.Add(new MeteoGap(from, to)); previous = to;
            }
            return !r.Failed && r.End && double.IsFinite(s.From) && double.IsFinite(s.To);
        }

        public static void Write(WireWriter w, IReadOnlyList<MeteoEntry> entries, int take) {
            int n = Math.Min(take, entries.Count); w.U8((byte)n);
            for (int i = entries.Count - n; i < entries.Count; i++) { MeteoEntry e = entries[i]; WeatherWire.Double(w, e.Time); w.U16(e.Station); w.Text(e.AuthorName); w.Text(e.Text); }
        }

        public static bool Read(ref WireReader r, List<MeteoEntry> into) {
            into.Clear(); int n = r.U8();
            if (n > MaxEntries || r.Failed) return false;
            for (int i = 0; i < n; i++) {
                double t = WeatherWire.Double(ref r); ushort st = r.U16(); string who = r.Text(NameMax * 4), text = r.Text(MeteoArchive.EntryTextMax * 4);
                if (r.Failed || !double.IsFinite(t)) return false;
                into.Add(new MeteoEntry(t, 0, who, st, text));
            }
            return r.End;
        }
    }
}
