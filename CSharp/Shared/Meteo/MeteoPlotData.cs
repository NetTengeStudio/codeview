using System;
using ScpItdMod.Core;

namespace ScpItdMod.Meteo {
    public enum MeteoMetric : byte { Pressure, Temperature, Wind, Visibility }

    public readonly struct MeteoAxis {
        public readonly float Low, High, Step;
        public MeteoAxis(float low, float high, float step) { Low = low; High = high; Step = step; }
        public float Fraction(float value) => (value - Low) / (High - Low);
    }

    public static class MeteoPlotMath {
        public static Vec2 Velocity(float speed, float fromRadians) => new Vec2(-MathF.Sin(fromRadians) * speed, MathF.Cos(fromRadians) * speed);

        public static float Value(in MeteoSample sample, MeteoMetric metric) => metric switch {
            MeteoMetric.Pressure => sample.Pressure,
            MeteoMetric.Temperature => sample.Temperature,
            MeteoMetric.Wind => sample.WindValid ? sample.Wind : float.NaN,
            MeteoMetric.Visibility => sample.Visibility > 0 ? MathF.Log10(sample.Visibility) : float.NaN,
            _ => float.NaN
        };

        public static float NiceStep(float extent, int ticks = 4) {
            float raw = MathF.Max(0.000001f, extent / ticks);
            float power = MathF.Pow(10, MathF.Floor(MathF.Log10(raw))), normalized = raw / power;
            return power * (normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10);
        }

        public static MeteoAxis Axis(MeteoMetric metric, float low, float high) {
            if (!float.IsFinite(low) || !float.IsFinite(high) || low > high) { low = 0; high = 1; }
            if (metric == MeteoMetric.Visibility) {
                low = MathF.Floor(low); high = MathF.Ceiling(high);
                if (high <= low) { high = low + 1; }
                return new MeteoAxis(low, high, 1);
            }
            if (metric == MeteoMetric.Wind) { low = 0; high = MathF.Max(10, high * 1.12f); }
            else {
                float padding = MathF.Max((high - low) * 0.08f, metric == MeteoMetric.Pressure ? 1 : 0.5f);
                low -= padding; high += padding;
            }
            float step = NiceStep(high - low);
            return new MeteoAxis(MathF.Floor(low / step) * step, MathF.Ceiling(high / step) * step, step);
        }

        public static bool Finite(in MeteoSample sample) => double.IsFinite(sample.Time) &&
            float.IsFinite(sample.Temperature) && float.IsFinite(sample.Pressure) && sample.Pressure > 0 &&
            float.IsFinite(sample.Wind) && sample.Wind >= 0 && float.IsFinite(sample.Direction) &&
            float.IsFinite(sample.Visibility) && sample.Visibility > 0 && float.IsFinite(sample.Humidity) &&
            sample.Humidity >= 0 && sample.Humidity <= 1 && float.IsFinite(sample.Snow) && sample.Snow >= 0 &&
            float.IsFinite(sample.Ice) && sample.Ice >= 0 && sample.Ice <= 1;
    }

    public sealed class MeteoPlotData {
        private readonly MeteoSample[] samples = new MeteoSample[MeteoWire.MaxPoints];
        private readonly float[] values = new float[4 * MeteoWire.MaxPoints];
        private readonly bool[] connected = new bool[MeteoWire.MaxPoints];
        private readonly MeteoGap[] gaps = new MeteoGap[MeteoWire.MaxGaps];
        private readonly MeteoAxis[] axes = new MeteoAxis[4];
        public int Count { get; private set; }
        public int GapCount { get; private set; }
        public ushort Station { get; private set; }
        public double From { get; private set; }
        public double To { get; private set; }
        public ref readonly MeteoSample At(int index) {
            if ((uint)index >= (uint)Count) { throw new ArgumentOutOfRangeException(nameof(index)); }
            return ref samples[index];
        }
        public MeteoGap Gap(int index) {
            if ((uint)index >= (uint)GapCount) { throw new ArgumentOutOfRangeException(nameof(index)); }
            return gaps[index];
        }
        public MeteoAxis Axis(MeteoMetric metric) => axes[(int)metric];
        public float Value(MeteoMetric metric, int index) => values[(int)metric * MeteoWire.MaxPoints + index];
        public bool Connects(int index) => index > 0 && index < Count && connected[index];

        public void Clear() { Count = GapCount = 0; Station = 0; From = To = 0; }

        public bool Load(MeteoSeries series) {
            Clear();
            if (series == null || !double.IsFinite(series.From) || !double.IsFinite(series.To) || series.To <= series.From ||
                series.Samples.Count > samples.Length || series.Gaps.Count > gaps.Length) { return false; }
            double previous = double.NegativeInfinity;
            ReadOnlySpan<MeteoSample> source = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(series.Samples);
            for (int i = 0; i < series.Samples.Count; i++) {
                ref readonly MeteoSample sample = ref source[i];
                if (!MeteoPlotMath.Finite(sample) || sample.Station != series.Station || sample.Time <= previous ||
                    sample.Time < series.From - MeteoCodec.TimeStep || sample.Time > series.To + MeteoCodec.TimeStep) { return false; }
                previous = sample.Time;
            }
            for (int i = 0; i < series.Gaps.Count; i++) {
                MeteoGap gap = series.Gaps[i];
                if (!double.IsFinite(gap.From) || !double.IsFinite(gap.To) || gap.To <= gap.From ||
                    i > 0 && gap.From < series.Gaps[i - 1].To) { return false; }
                gaps[i] = gap;
            }
            Station = series.Station; From = series.From; To = series.To;
            Count = series.Samples.Count; GapCount = series.Gaps.Count;
            Span<float> lows = stackalloc float[4], highs = stackalloc float[4];
            lows.Fill(float.PositiveInfinity); highs.Fill(float.NegativeInfinity);
            int gapIndex = 0;
            double maxInterval = Math.Max(35, (To - From) / MeteoWire.MaxPoints * 4);
            for (int i = 0; i < Count; i++) {
                samples[i] = series.Samples[i];
                while (gapIndex < GapCount && gaps[gapIndex].To <= (i > 0 ? samples[i - 1].Time : From)) { gapIndex++; }
                connected[i] = i > 0 && samples[i].Time - samples[i - 1].Time <= maxInterval &&
                    !(gapIndex < GapCount && gaps[gapIndex].From < samples[i].Time && gaps[gapIndex].To > samples[i - 1].Time);
                for (int metric = 0; metric < 4; metric++) {
                    float value = MeteoPlotMath.Value(samples[i], (MeteoMetric)metric);
                    values[metric * MeteoWire.MaxPoints + i] = value;
                    if (!float.IsFinite(value)) { continue; }
                    lows[metric] = MathF.Min(lows[metric], value); highs[metric] = MathF.Max(highs[metric], value);
                }
            }
            for (int metric = 0; metric < 4; metric++) { axes[metric] = MeteoPlotMath.Axis((MeteoMetric)metric, lows[metric], highs[metric]); }
            return true;
        }

        public int Nearest(double time) {
            if (Count == 0 || !double.IsFinite(time) || time < From || time > To) { return -1; }
            for (int i = 0; i < GapCount; i++) { if (time > gaps[i].From && time < gaps[i].To) { return -1; } }
            int low = 0, high = Count - 1;
            while (low < high) { int mid = low + ((high - low) >> 1); if (samples[mid].Time < time) { low = mid + 1; } else { high = mid; } }
            int result = low > 0 && time - samples[low - 1].Time <= samples[low].Time - time ? low - 1 : low;
            double tolerance = Math.Max(10, (To - From) / MeteoWire.MaxPoints * 1.5);
            return Math.Abs(samples[result].Time - time) <= tolerance ? result : -1;
        }

        public bool PressureSlope(double to, double seconds, out float perMinute, out int points) {
            perMinute = 0; points = 0;
            if (!double.IsFinite(to) || !double.IsFinite(seconds) || seconds <= 0) { return false; }
            double from = to - seconds;
            for (int i = 0; i < GapCount; i++) { if (gaps[i].From < to && gaps[i].To > from) { return false; } }
            double sx = 0, sy = 0, sxx = 0, sxy = 0;
            for (int i = Count - 1; i >= 0; i--) {
                ref readonly MeteoSample sample = ref samples[i];
                if (sample.Time > to) { continue; }
                if (sample.Time < from) { break; }
                double x = sample.Time - to, y = sample.Pressure;
                points++; sx += x; sy += y; sxx += x * x; sxy += x * y;
            }
            double determinant = points * sxx - sx * sx;
            if (points < 4 || determinant <= 1e-9) { return false; }
            perMinute = (float)((points * sxy - sx * sy) / determinant * 60);
            return float.IsFinite(perMinute);
        }
    }
}
