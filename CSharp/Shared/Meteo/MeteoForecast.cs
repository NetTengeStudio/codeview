using System;
using System.Collections.Generic;
using ScpItdMod.Weather;

namespace ScpItdMod.Meteo {
    public enum MeteoTrend : byte { RisingFast, Rising, Steady, Falling, FallingFast }

    public enum MeteoAlert : byte { None, Watch, Warning, Storm }

    public struct MeteoForecast {
        public bool Valid;
        public int Samples;
        public double Now;
        public WeatherRegime Observed;
        public float Age;
        public bool AgeExact;
        public float PressureSlope;
        public float PressureDrop;
        public MeteoTrend Trend;
        public bool Precursor;
        public WeatherRegime Likely;
        public float[] Probability;
        public float EtaLow, EtaMid, EtaHigh;
        public float Confidence;
        public float CoverageFraction;
        public float TemperatureNext, WindNext, VisibilityNext;
        public MeteoAlert Alert;
    }

    public sealed class MeteoForecaster {
        public const double LookBack = 2400, MinimumWindow = 60, GapLimit = 40;
        public const float PrecursorDrop = .5f, PrecursorSlope = -.3f, FullDrop = 8;
        private readonly WeatherConfig config;
        private readonly List<MeteoSample> window = new List<MeteoSample>(300);
        private byte[] classes = new byte[300], smooth = new byte[300];

        public MeteoForecaster(WeatherConfig config) { this.config = config ?? throw new ArgumentNullException(nameof(config)); }

        public WeatherRegime Classify(in MeteoSample s) {
            int best = 0; float bestCost = float.MaxValue;
            for (int i = 0; i < 6; i++) {
                WeatherState p = config.Profiles[i].State;
                float dv = MathF.Log10(MathF.Max(1, s.Visibility)) - MathF.Log10(MathF.Max(1, p.Visibility));
                float cost = dv * dv / .35f + (s.Temperature - p.Temperature) * (s.Temperature - p.Temperature) / 120f;
                if (s.WindValid) { float dw = (s.Wind - p.WindSpeed) / 8f; cost += dw * dw; }
                if (cost < bestCost) { bestCost = cost; best = i; }
            }
            return (WeatherRegime)best;
        }

        public static MeteoTrend TrendOf(float slopePerMinute) =>
            slopePerMinute <= -1.2f ? MeteoTrend.FallingFast : slopePerMinute <= -.25f ? MeteoTrend.Falling :
            slopePerMinute >= 1.2f ? MeteoTrend.RisingFast : slopePerMinute >= .25f ? MeteoTrend.Rising : MeteoTrend.Steady;

        public static float InverseSmooth(float y) {
            y = Math.Clamp(y, 0, 1); float lo = 0, hi = 1;
            for (int i = 0; i < 20; i++) { float mid = (lo + hi) * .5f; if (WeatherNoise.Smooth(mid) < y) lo = mid; else hi = mid; }
            return (lo + hi) * .5f;
        }

        public MeteoForecast Evaluate(MeteoArchive archive, ushort station, double now) {
            var f = new MeteoForecast { Now = now, Probability = new float[6] };
            archive.Range(station, now - LookBack, now, window, true);
            int n = window.Count; f.Samples = n;
            if (n < 6 || now - window[0].Time < MinimumWindow || now - window[n - 1].Time > 60) return f;
            if (classes.Length < n) { classes = new byte[n]; smooth = new byte[n]; }
            for (int i = 0; i < n; i++) classes[i] = (byte)Classify(window[i]);
            Span<int> votes = stackalloc int[6];
            for (int i = 0; i < n; i++) {
                votes.Clear();
                for (int k = Math.Max(0, i - 2); k <= Math.Min(n - 1, i + 2); k++) votes[classes[k]]++;
                int best = classes[i]; for (int r = 0; r < 6; r++) if (votes[r] > votes[best]) best = r;
                smooth[i] = (byte)best;
            }
            byte current = smooth[n - 1]; int first = n - 1;
            while (first > 0 && smooth[first - 1] == current) first--;
            f.Observed = (WeatherRegime)current;
            bool truncated = first == 0;
            double onset = window[first].Time;
            if (!truncated) { double gap = window[first].Time - window[first - 1].Time; onset -= gap * .5; f.AgeExact = gap <= GapLimit; }
            f.Age = (float)(now - onset) + (truncated ? 0 : config.BlendSeconds * .5f);
            if (truncated) f.AgeExact = false;
            double covered = 0; for (int i = 1; i < n; i++) { double g = window[i].Time - window[i - 1].Time; covered += g <= GapLimit ? g : 0; }
            f.CoverageFraction = (float)Math.Clamp(covered / Math.Max(1, window[n - 1].Time - window[0].Time), 0, 1);
            Pressure(f.Age, n, now, ref f);
            Prior(ref f);
            Confidence(ref f);
            Outlook(ref f);
            f.Valid = true;
            return f;
        }

        private void Pressure(float age, int n, double now, ref MeteoForecast f) {
            int m = 0; double sx = 0, sy = 0, sxx = 0, sxy = 0;
            for (int i = n - 1; i >= 0 && now - window[i].Time <= 60; i--) { double x = window[i].Time - now, y = window[i].Pressure; sx += x; sy += y; sxx += x * x; sxy += x * y; m++; }
            f.PressureSlope = 0;
            if (m < 4) return;
            double d = m * sxx - sx * sx, perSecond = 0;
            if (Math.Abs(d) > 1e-9) perSecond = (m * sxy - sx * sy) / d;
            f.PressureSlope = (float)(perSecond * 60);
            f.Trend = TrendOf(f.PressureSlope);
            double current = sy / m - perSecond * (sx / m);
            f.PressureDrop = config.Profiles[(int)f.Observed].State.Pressure - (float)current;
            f.Precursor = age > config.BlendSeconds * 1.4f + 5 && f.PressureDrop >= PrecursorDrop && f.PressureSlope <= PrecursorSlope;
        }

        private void Prior(ref MeteoForecast f) {
            WeatherProfile profile = config.Profiles[(int)f.Observed];
            float warning = config.WarningSeconds;
            float minimum = profile.Minimum, maximum = profile.Maximum;
            float lo, hi;
            if (f.AgeExact) { lo = MathF.Max(0, minimum - f.Age); hi = MathF.Max(lo + 15, maximum - f.Age); }
            else { lo = 0; hi = MathF.Max(15, maximum - f.Age); }
            float severity = WeatherDirector.Severity(f.Observed);
            float surviving = hi <= lo ? 0 : Math.Clamp((hi - MathF.Max(warning, lo)) / (hi - lo), 0, 1);
            float sum = 0;
            for (int i = 0; i < 6; i++) {
                float w = profile.Transitions[i];
                if (w <= 0) continue;
                bool worse = WeatherDirector.Severity((WeatherRegime)i) > severity;
                if (f.Precursor) w = worse ? w : 0;
                else if (worse && f.AgeExact) w *= surviving;
                f.Probability[i] = w; sum += w;
            }
            if (sum <= 0) { for (int i = 0; i < 6; i++) f.Probability[i] = profile.Transitions[i]; sum = 1; }
            int likely = 0;
            for (int i = 0; i < 6; i++) { f.Probability[i] /= sum; if (f.Probability[i] > f.Probability[likely]) likely = i; }
            f.Likely = (WeatherRegime)likely;
            if (f.Precursor) {
                float progress = InverseSmooth(Math.Clamp(f.PressureDrop / FullDrop, 0, 1));
                float remaining = MathF.Max(0, (1 - progress) * warning);
                float spread = 8 + .25f * remaining;
                f.EtaLow = MathF.Max(0, remaining - spread); f.EtaMid = remaining; f.EtaHigh = remaining + spread;
            } else {
                f.EtaLow = lo + (hi - lo) * .2f; f.EtaMid = lo + (hi - lo) * .5f; f.EtaHigh = lo + (hi - lo) * .8f;
            }
        }

        private void Confidence(ref MeteoForecast f) {
            float c = Math.Clamp(f.Samples / 60f, .1f, 1) * (.4f + .6f * f.CoverageFraction);
            if (!f.AgeExact) c *= .65f;
            float top = 0; for (int i = 0; i < 6; i++) top = MathF.Max(top, f.Probability[i]);
            c *= .45f + .55f * top;
            if (f.Precursor) c = MathF.Min(1, c * 1.5f + .15f);
            f.Confidence = Math.Clamp(c, 0, 1);
        }

        private void Outlook(ref MeteoForecast f) {
            float t = 0, w = 0, v = 0;
            for (int i = 0; i < 6; i++) { WeatherState s = config.Profiles[i].State; t += s.Temperature * f.Probability[i]; w += s.WindSpeed * f.Probability[i]; v += MathF.Log10(s.Visibility) * f.Probability[i]; }
            f.TemperatureNext = t; f.WindNext = w; f.VisibilityNext = MathF.Pow(10, v);
            int current = WeatherDirector.Severity(f.Observed), likely = WeatherDirector.Severity(f.Likely);
            float severe = 0; for (int i = 0; i < 6; i++) if (WeatherDirector.Severity((WeatherRegime)i) >= 3) severe += f.Probability[i];
            f.Alert = MeteoAlert.None;
            if (current >= 4 || f.Precursor && likely >= 3 && f.EtaMid <= 180) f.Alert = MeteoAlert.Storm;
            else if (current >= 3 || f.Precursor && likely >= 2 || severe > .5f && f.EtaLow <= 300) f.Alert = MeteoAlert.Warning;
            else if (likely > current && f.Probability[(int)f.Likely] > .35f && f.EtaLow <= 420 || severe > .3f && f.EtaLow <= 300) f.Alert = MeteoAlert.Watch;
        }
    }
}
