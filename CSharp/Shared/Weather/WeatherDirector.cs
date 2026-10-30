using System;

namespace ScpItdMod.Weather {
    public sealed class WeatherDirector {
        public const int MaximumControls = 256;
        public const double MaximumSeconds = 31536000;
        private readonly WeatherConfig config;
        private readonly WeatherControl[] controls = new WeatherControl[MaximumControls];
        private double segmentStart, segmentEnd;
        private int segment, count;
        private WeatherRegime current, previous, next;
        public uint Seed { get; }
        public uint Revision => count == 0 ? 0 : controls[count - 1].Revision;
        public int ControlCount => count;
        public WeatherControl ControlAt(int index) => controls[index];
        public WeatherDirector(WeatherConfig config, uint seed) { this.config = config ?? throw new ArgumentNullException(nameof(config)); Seed = seed; Reset(); }
        private void Reset() { segment = 0; segmentStart = 0; current = previous = config.StartRegime; segmentEnd = Duration(current, 0); next = Choose(current, 1); }
        private float Duration(WeatherRegime regime, int index) { WeatherProfile p = config.Profiles[(int)regime]; return WeatherNoise.Mix(p.Minimum, p.Maximum, WeatherNoise.Unit(Seed ^ unchecked((uint)index * 747796405u))); }
        private WeatherRegime Choose(WeatherRegime regime, int index) {
            float v = WeatherNoise.Unit(Seed ^ unchecked((uint)index * 2891336453u));
            float[] row = config.Profiles[(int)regime].Transitions;
            for (int i = 0; i < row.Length; i++) { v -= row[i]; if (v <= 0 && row[i] > 0) return (WeatherRegime)i; }
            for (int i = row.Length - 1; i >= 0; i--) if (row[i] > 0) return (WeatherRegime)i;
            throw new InvalidOperationException("Empty weather transition row");
        }
        private WeatherState Natural(double seconds) {
            if (seconds < segmentStart) Reset();
            while (seconds >= segmentEnd) {
                previous = current; current = next; segment++; segmentStart = segmentEnd;
                segmentEnd += Duration(current, segment); next = Choose(current, segment + 1);
            }
            WeatherState target = config.Profiles[(int)current].State;
            target.Next = next; target.SegmentEnd = segmentEnd;
            float blend = WeatherNoise.Smooth((float)((seconds - segmentStart) / Math.Max(.001, Math.Min(config.BlendSeconds, (segmentEnd - segmentStart) * .4))));
            WeatherState value = WeatherNoise.Blend(config.Profiles[(int)previous].State, target, blend);
            for (int i = 0; i < config.Schedule.Length; i++) {
                WeatherSchedule change = config.Schedule[i]; if (seconds < change.At) break;
                WeatherState scheduled = config.Profiles[(int)change.Regime].State;
                float t = WeatherNoise.Smooth((float)((seconds - change.At) / Math.Max(.001, change.Blend)));
                value = WeatherNoise.Blend(value, scheduled, t);
                value.Next = i + 1 < config.Schedule.Length ? config.Schedule[i + 1].Regime : next;
                value.SegmentEnd = i + 1 < config.Schedule.Length ? config.Schedule[i + 1].At : segmentEnd;
            }
            return value;
        }
        private WeatherState Base(double seconds) {
            if (!double.IsFinite(seconds) || seconds < 0 || seconds > MaximumSeconds) throw new ArgumentOutOfRangeException(nameof(seconds));
            WeatherState value = Natural(seconds);
            int regime = -1, wind = -1, temperature = -1;
            for (int i = count - 1; i >= 0; i--) {
                ref readonly WeatherControl c = ref controls[i]; if (c.At > seconds) continue;
                if (c.Kind == WeatherControlKind.Regime && regime < 0) regime = i;
                else if (c.Kind == WeatherControlKind.Wind && wind < 0) wind = i;
                else if (c.Kind == WeatherControlKind.Temperature && temperature < 0) temperature = i;
                if (regime >= 0 && wind >= 0 && temperature >= 0) break;
            }
            if (regime >= 0) {
                ref readonly WeatherControl c = ref controls[regime];
                WeatherState target = config.Profiles[(int)c.A].State;
                value = WeatherNoise.Blend(c.From, target, Progress(c, seconds));
                value.Next = target.Regime; value.SegmentEnd = double.PositiveInfinity;
            }
            if (wind >= 0) {
                ref readonly WeatherControl c = ref controls[wind]; float t = Progress(c, seconds);
                value.WindSpeed = WeatherNoise.Mix(c.From.WindSpeed, c.A, t); value.WindDirection = WeatherNoise.Angle(c.From.WindDirection, c.B, t);
            }
            if (temperature >= 0) { ref readonly WeatherControl c = ref controls[temperature]; value.Temperature = WeatherNoise.Mix(c.From.Temperature, c.A, Progress(c, seconds)); }
            value.Warning = float.IsFinite((float)value.SegmentEnd) && Severity(value.Next) > Severity(value.Regime)
                ? WeatherNoise.Smooth((float)(1 - (value.SegmentEnd - seconds) / Math.Max(1, config.WarningSeconds))) : 0;
            value.Pressure -= value.Warning * 8;
            value.Cloud = Math.Clamp(value.Cloud + value.Warning * .15f, 0, 1);
            return value;
        }
        public WeatherState Evaluate(double seconds) => Evaluate(seconds, out _);
        public WeatherState Evaluate(double seconds, out SolarState sky) {
            WeatherState value = Base(seconds);
            Turbulence air = WeatherTurbulence.Sample(Seed, seconds, value.Gustiness);
            value.WindSpeed = Math.Clamp(value.WindSpeed * air.Wind, 0, 60);
            value.WindDirection += air.Direction;
            value.Snowfall = Math.Clamp(value.Snowfall * air.Snowfall, 0, 1);
            value.Visibility = Math.Max(1f, value.Visibility * air.Visibility);
            value.Cloud = Math.Clamp(value.Cloud + air.Cloud * Math.Min(1f, value.Cloud + .3f), 0, 1);
            value.Wind = new Core.Vec2(-MathF.Sin(value.WindDirection) * value.WindSpeed, 0);
            double day = Day(seconds);
            value.Temperature = Math.Clamp(value.Temperature + WeatherNoise.Value(Seed ^ 0x8cb92ba7u, seconds / 180) * 1.5f + PolarSky.DiurnalSwing(day, value.Cloud, value.WindSpeed), -80, -15);
            sky = PolarSky.Evaluate(config.Latitude, day, DayOfYear(seconds), value.Cloud);
            value.SkyIlluminance = sky.Illuminance;
            return value;
        }
        private static float Progress(in WeatherControl c, double t) => c.Duration <= 0 ? 1 : WeatherNoise.Smooth((float)((t - c.At) / c.Duration));
        public WeatherControl Add(WeatherControlKind kind, double at, float a, float b = 0, float duration = 30) {
            if (!Valid(kind, at, a, b, duration) || count > 0 && at < controls[count - 1].At) throw new ArgumentException("Invalid weather control");
            if (count == MaximumControls) throw new InvalidOperationException("Weather control history is full");
            WeatherState from = Base(at);
            if (kind == WeatherControlKind.TimeScale || kind == WeatherControlKind.Time) {
                from.SegmentEnd = Day(at); from.SkyIlluminance = (float)DayRate(at);
                if (kind == WeatherControlKind.Time) a /= 24;
            }
            var c = new WeatherControl(Revision + 1, kind, at, a, b, duration, from); controls[count++] = c; return c;
        }
        public bool Import(in WeatherControl c) {
            if (count == MaximumControls || c.Revision != Revision + 1 || !Valid(c) || count > 0 && c.At < controls[count - 1].At) return false;
            controls[count++] = c; return true;
        }
        public bool Import(ReadOnlySpan<WeatherControl> incoming) {
            if (incoming.Length > MaximumControls - count) return false;
            uint expected = Revision;
            double previousAt = count == 0 ? 0 : controls[count - 1].At;
            for (int i = 0; i < incoming.Length; i++) {
                ref readonly WeatherControl control = ref incoming[i];
                if (control.Revision != ++expected || control.At < previousAt || !Valid(control)) return false;
                previousAt = control.At;
            }
            incoming.CopyTo(controls.AsSpan(count)); count += incoming.Length; return true;
        }
        public static bool Valid(in WeatherControl c) {
            WeatherState s = c.From;
            bool time = c.Kind is WeatherControlKind.Time or WeatherControlKind.TimeScale;
            bool segment = time ? double.IsFinite(s.SegmentEnd) && Math.Abs(s.SegmentEnd) <= MaximumSeconds / 60 + MaximumControls + 1
                : s.SegmentEnd >= 0;
            return Valid(c.Kind, c.At, c.Kind == WeatherControlKind.Time ? c.A * 24 : c.A, c.B, c.Duration)
                && float.IsFinite(s.Temperature) && float.IsFinite(s.WindSpeed) && float.IsFinite(s.WindDirection) && float.IsFinite(s.Gustiness)
                && float.IsFinite(s.Snowfall) && float.IsFinite(s.Drifting) && float.IsFinite(s.Visibility) && s.Visibility > 0 && float.IsFinite(s.Cloud)
                && float.IsFinite(s.SkyIlluminance) && float.IsFinite(s.Humidity) && float.IsFinite(s.Frost) && float.IsFinite(s.Pressure)
                && (byte)s.Regime < 6 && (byte)s.Next < 6 && segment
                && (!time || s.SkyIlluminance >= 0 && s.SkyIlluminance <= 1f / 60);
        }
        public static bool Valid(WeatherControlKind kind, double at, float a, float b, float duration) {
            if (!double.IsFinite(at) || at < 0 || at > MaximumSeconds || !float.IsFinite(a) || !float.IsFinite(b) || !float.IsFinite(duration) || duration < 0 || duration > 3600) return false;
            return kind switch {
                WeatherControlKind.Regime => a >= 0 && a <= 5 && a == MathF.Truncate(a),
                WeatherControlKind.Wind => a >= 0 && a <= 60 && b >= -MathF.Tau && b <= MathF.Tau,
                WeatherControlKind.Temperature => a >= -80 && a <= -15,
                WeatherControlKind.Time => a >= 0 && a < 24,
                WeatherControlKind.Season => a >= 0 && a <= 2 && a == MathF.Truncate(a),
                WeatherControlKind.TimeScale => a >= 1 && a <= 10080,
                _ => false
            };
        }
        public double DayRate(double at) {
            for (int i = count - 1; i >= 0; i--) if (controls[i].At <= at && controls[i].Kind == WeatherControlKind.TimeScale) return 1 / (controls[i].A * 60);
            return config.LockTime ? 0 : 1 / (config.MinutesPerDay * 60);
        }
        public double Day(double at) {
            for (int i = count - 1; i >= 0; i--) {
                ref readonly WeatherControl c = ref controls[i];
                if (c.At > at || c.Kind != WeatherControlKind.Time && c.Kind != WeatherControlKind.TimeScale) continue;
                if (c.Kind == WeatherControlKind.TimeScale) return c.From.SegmentEnd + (at - c.At) / (c.A * 60);
                double previousDay = c.From.SegmentEnd;
                double target = Math.Floor(previousDay) + c.A;
                double offset = target - previousDay;
                if (offset < -.5) offset++;
                if (offset > .5) offset--;
                return previousDay + (at - c.At) * c.From.SkyIlluminance + offset * Progress(c, at);
            }
            return config.LockTime ? config.StartHour / 24d : config.StartHour / 24d + at / (config.MinutesPerDay * 60d);
        }
        public float DayOfYear(double at) {
            PolarSeason season = config.Season;
            for (int i = count - 1; i >= 0; i--) if (controls[i].At <= at && controls[i].Kind == WeatherControlKind.Season) { season = (PolarSeason)controls[i].A; break; }
            return season switch { PolarSeason.PolarDay => 355, PolarSeason.PolarNight => 172, _ => config.DayOfYear };
        }
        public static int Severity(WeatherRegime regime) => regime switch { WeatherRegime.Storm => 4, WeatherRegime.Blizzard => 3, WeatherRegime.Drifting => 2, WeatherRegime.Snowfall => 1, _ => 0 };
    }

    public sealed class WeatherClock {
        private double correction;
        public double Seconds { get; private set; }
        public bool Initialized { get; private set; }
        public bool Frozen { get; private set; }
        public void Reset(double seconds = 0, bool frozen = false) {
            if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
            Seconds = seconds; Frozen = frozen; correction = 0; Initialized = true;
        }
        public bool Synchronize(double seconds, double roundTrip, bool frozen, bool hard = false) {
            if (!double.IsFinite(seconds) || seconds < 0 || !double.IsFinite(roundTrip) || roundTrip < 0 || roundTrip > 4) return false;
            double target = seconds + (frozen ? 0 : roundTrip * .5);
            if (!Initialized || hard || frozen) { Reset(target, frozen); return true; }
            correction = target - Seconds; Frozen = frozen; return true;
        }
        public void Freeze(bool value) { Frozen = value; correction = 0; }
        public void Step(double dt) {
            if (!Initialized || !double.IsFinite(dt) || dt <= 0 || Frozen) return;
            double nudge = Math.Clamp(correction, -dt * .1, dt * .1);
            Seconds += dt + nudge; correction -= nudge;
        }
    }
}
