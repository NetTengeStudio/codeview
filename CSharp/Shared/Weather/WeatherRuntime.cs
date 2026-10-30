using System;
using ScpItdMod.Net;

namespace ScpItdMod.Weather {
    public enum WeatherReceiveResult : byte { Accepted, Malformed, ConfigurationMismatch, SnapshotRequired }

    public sealed class WeatherRuntime {
        private readonly WeatherConfig config;
        private readonly WeatherControl[] incoming;
        private uint clockSequence;
        public WeatherDirector Director { get; private set; }
        public WeatherClock Clock { get; }
        public WeatherState Current { get; private set; }
        public SolarState Sun { get; private set; }
        public LightColor OutdoorLight { get; private set; }
        public bool Authority { get; }
        public bool Ready { get; private set; }
        public bool NeedsSnapshot { get; private set; }
        public uint Epoch { get; private set; }

        public WeatherRuntime(WeatherConfig config, uint seed, uint epoch, bool authority, WeatherClock clock = null) {
            this.config = config ?? throw new ArgumentNullException(nameof(config));
            Clock = clock ?? new WeatherClock(); Clock.Reset();
            incoming = authority ? Array.Empty<WeatherControl>() : new WeatherControl[WeatherDirector.MaximumControls];
            Director = new WeatherDirector(config, seed); Epoch = epoch; Authority = authority;
            Ready = authority; NeedsSnapshot = !authority; Refresh(0, true);
        }

        private void Refresh(float dt, bool initialize) {
            Current = Director.Evaluate(Clock.Seconds, out SolarState sun); Sun = sun;
            LightColor target = WeatherLighting.Outdoor(Current, sun, Clock.Seconds, Director.Seed, config.Moonlight);
            OutdoorLight = initialize ? target : WeatherLighting.Adapt(OutdoorLight, target, dt);
        }

        public void Step(float dt) {
            if (!Ready || !float.IsFinite(dt) || dt <= 0) return;
            Clock.Step(dt); Refresh(dt, false);
        }

        private void RequireAuthority() { if (!Authority) throw new InvalidOperationException("Weather controls require authority."); }
        public WeatherControl Set(WeatherControlKind kind, float a, float b, float duration) {
            RequireAuthority();
            WeatherControl control = Director.Add(kind, Clock.Seconds, a, b, duration);
            Refresh(0, false); return control;
        }
        public void Freeze(bool frozen) { RequireAuthority(); Clock.Freeze(frozen); }

        public void WriteState(WireWriter writer, bool snapshot, uint revision) {
            RequireAuthority();
            if (revision > Director.Revision) snapshot = true;
            int start = snapshot ? 0 : (int)revision;
            writer.Clear(); writer.U8((byte)(snapshot ? 0 : 1)); writer.U32(Epoch); writer.U64(config.Fingerprint);
            writer.U32(Director.Seed); writer.U32((uint)start); writer.U16((ushort)(Director.ControlCount - start));
            WeatherWire.Double(writer, Clock.Seconds); writer.Bool(Clock.Frozen);
            for (int i = start; i < Director.ControlCount; i++) WeatherWire.Write(writer, Director.ControlAt(i));
        }

        public WeatherReceiveResult ApplyState(ReadOnlySpan<byte> payload) {
            if (Authority) throw new InvalidOperationException("An authoritative runtime cannot import a remote state.");
            var reader = new WireReader(payload);
            byte type = reader.U8(); uint epoch = reader.U32(); ulong fingerprint = reader.U64();
            uint seed = reader.U32(), start = reader.U32(); int count = reader.U16();
            double seconds = WeatherWire.Double(ref reader); bool frozen = reader.Bool();
            if (reader.Failed || type > 1 || count > incoming.Length || !double.IsFinite(seconds) || seconds < 0 || seconds > WeatherDirector.MaximumSeconds)
                return Reject(WeatherReceiveResult.Malformed);
            if (fingerprint != config.Fingerprint) return Reject(WeatherReceiveResult.ConfigurationMismatch);
            if (type == 0 && start != 0) return Reject(WeatherReceiveResult.Malformed);
            if (type == 1 && (!Ready || epoch != Epoch || seed != Director.Seed || start != Director.Revision)) return Reject(WeatherReceiveResult.SnapshotRequired);
            for (int i = 0; i < count; i++) if (!WeatherWire.Read(ref reader, out incoming[i])) return Reject(WeatherReceiveResult.Malformed);
            if (reader.Failed || !reader.End) return Reject(WeatherReceiveResult.Malformed);
            if (type == 0 && Ready && epoch == Epoch && count < Director.ControlCount) return Reject(WeatherReceiveResult.SnapshotRequired);
            WeatherDirector next = type == 0 ? new WeatherDirector(config, seed) : Director;
            if (!next.Import(incoming.AsSpan(0, count))) return Reject(WeatherReceiveResult.SnapshotRequired);
            bool initialize = !Ready || epoch != Epoch;
            Clock.Synchronize(seconds, 0, frozen, initialize);
            Director = next; Epoch = epoch; Ready = true; NeedsSnapshot = false;
            if (initialize) clockSequence = 0;
            Refresh(0, initialize); return WeatherReceiveResult.Accepted;
        }

        private WeatherReceiveResult Reject(WeatherReceiveResult result) { NeedsSnapshot = true; return result; }

        public bool SynchronizeClock(uint epoch, uint sequence, double seconds, double roundTrip, bool frozen) {
            if (Authority || !Ready || epoch != Epoch || sequence <= clockSequence || !double.IsFinite(seconds)
                || seconds < 0 || seconds > WeatherDirector.MaximumSeconds) return false;
            if (!Clock.Synchronize(seconds, roundTrip, frozen)) return false;
            clockSequence = sequence; Refresh(0, false); return true;
        }
    }
}
