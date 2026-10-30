using System;
using System.Buffers.Binary;
using ScpItdMod.Net;
using ScpItdMod.Rec;

namespace ScpItdMod.Cinematics {
    public sealed class CinematicSnapshot {
        public ulong Id, PlanHash, Fired;
        public uint Revision, Removed, Retiring;
        public string Plan;
        public ushort Stage;
        public float StageElapsed, Elapsed, UnitsPerMetre = 100f;
        public bool Hidden;
        public ushort[] Bindings;
        public float[] FadeAges;
    }
    public sealed class CinematicRoster {
        public uint Epoch, Sequence;
        public double ServerTime, Echo;
        public bool Paused;
        public CinematicSnapshot[] Scenes;
    }
    public static class CinematicWire {
        public const byte Version = 3;
        public const string Channel = "scpitd.cinematic.v3";
        public const int MaximumPacket = 8192;
        internal const byte ReadyRequest = 10, ReadyAccepted = 11;
        public static ulong PayloadHash(ReadOnlySpan<byte> payload) {
            ulong hash = 14695981039346656037UL;
            foreach (byte value in payload) hash = unchecked((hash ^ value) * 1099511628211UL);
            return hash;
        }
        public static ulong StateHash(uint revision, ushort stage, ulong fired, uint removed, uint retiring) {
            Span<byte> state = stackalloc byte[22];
            BinaryPrimitives.WriteUInt32LittleEndian(state, revision);
            BinaryPrimitives.WriteUInt16LittleEndian(state.Slice(4), stage);
            BinaryPrimitives.WriteUInt64LittleEndian(state.Slice(6), fired);
            BinaryPrimitives.WriteUInt32LittleEndian(state.Slice(14), removed);
            BinaryPrimitives.WriteUInt32LittleEndian(state.Slice(18), retiring);
            return PayloadHash(state);
        }
        public static void Header(WireWriter writer, byte kind) { writer.Clear(); writer.U8(Version); writer.U8(kind); }
        internal static void WriteReady(WireWriter writer, byte kind, uint epoch, ulong catalog) {
            Header(writer, kind); writer.U32(epoch); writer.U64(catalog);
        }
        internal static bool ReadReady(ReadOnlySpan<byte> payload, byte kind, out uint epoch, out ulong catalog) {
            var reader = new WireReader(payload);
            byte version = reader.U8(), operation = reader.U8();
            epoch = reader.U32(); catalog = reader.U64();
            return version == Version && operation == kind && !reader.Failed && reader.End;
        }
        internal static void WriteReadyAccepted(WireWriter writer, uint epoch, ulong catalog, uint session, uint sequence) {
            WriteReady(writer, ReadyAccepted, epoch, catalog); writer.U32(session); writer.U32(sequence);
        }
        internal static bool ReadReadyAccepted(ReadOnlySpan<byte> payload, out uint epoch, out ulong catalog, out uint session, out uint sequence) {
            var reader = new WireReader(payload);
            byte version = reader.U8(), operation = reader.U8();
            epoch = reader.U32(); catalog = reader.U64(); session = reader.U32(); sequence = reader.U32();
            return version == Version && operation == ReadyAccepted && !reader.Failed && reader.End;
        }
        public static void WriteRoster(WireWriter writer, CinematicRoster roster) {
            Header(writer, 0); writer.U32(roster.Epoch); writer.U32(roster.Sequence);
            writer.U64(BitConverter.DoubleToUInt64Bits(roster.ServerTime)); writer.U64(BitConverter.DoubleToUInt64Bits(roster.Echo));
            writer.Bool(roster.Paused);
            writer.U8((byte)roster.Scenes.Length);
            foreach (CinematicSnapshot s in roster.Scenes) WriteScene(writer, s);
        }
        public static void WriteScene(WireWriter writer, CinematicSnapshot s) {
            writer.U64(s.Id); writer.Text(s.Plan); writer.U64(s.PlanHash); writer.U32(s.Revision); writer.U16(s.Stage); writer.F32(s.StageElapsed); writer.F32(s.Elapsed);
            writer.U64(s.Fired); writer.U32(s.Removed); writer.U32(s.Retiring); writer.F32(s.UnitsPerMetre); writer.Bool(s.Hidden); writer.U8((byte)s.Bindings.Length);
            for (int b = 0; b < s.Bindings.Length; b++) {
                bool retiring = (s.Retiring & (1u << b)) != 0;
                WriteBinding(writer, s.Bindings[b], retiring, retiring && s.FadeAges != null ? s.FadeAges[b] : 0);
            }
        }
        public static void WriteBinding(WireWriter writer, ushort id, bool retiring, float fadeAge) { writer.U16(id); if (retiring) writer.F32(fadeAge); }
        public static bool ReadRoster(ReadOnlySpan<byte> payload, out CinematicRoster roster) {
            roster = null;
            if (payload.Length > MaximumPacket) return false;
            var reader = new WireReader(payload);
            if (reader.U8() != Version || reader.U8() != 0) return false;
            uint epoch = reader.U32(), sequence = reader.U32();
            double serverTime = BitConverter.UInt64BitsToDouble(reader.U64()), echo = BitConverter.UInt64BitsToDouble(reader.U64());
            bool paused = reader.Bool();
            int count = reader.U8();
            if (count > 8 || !double.IsFinite(serverTime) || serverTime < 0 || !double.IsFinite(echo) || echo < 0) return false;
            var scenes = new CinematicSnapshot[count];
            for (int i = 0; i < count; i++) {
                var s = new CinematicSnapshot { Id = reader.U64(), Plan = reader.Text(64), PlanHash = reader.U64(), Revision = reader.U32(), Stage = reader.U16(), StageElapsed = reader.F32(), Elapsed = reader.F32(), Fired = reader.U64(), Removed = reader.U32(), Retiring = reader.U32(), UnitsPerMetre = reader.F32(), Hidden = reader.Bool() };
                int bindings = reader.U8();
                if (bindings > CinematicPlan.MaximumBindings || s.Id == 0 || (uint)(s.Id >> 32) != epoch || s.Stage >= CinematicPlan.MaximumStages ||
                    !float.IsFinite(s.StageElapsed) || s.StageElapsed < 0 || s.StageElapsed > 3600 || !float.IsFinite(s.Elapsed) || s.Elapsed < s.StageElapsed || s.Elapsed > 3600 || s.Plan.Length == 0) return false;
                uint mask = bindings == 32 ? uint.MaxValue : (1u << bindings) - 1;
                if (((s.Removed | s.Retiring) & ~mask) != 0 || (s.Removed & s.Retiring) != 0 || !float.IsFinite(s.UnitsPerMetre) || s.UnitsPerMetre <= 0 || s.UnitsPerMetre > 1000) return false;
                s.Bindings = new ushort[bindings]; s.FadeAges = s.Retiring == 0 ? Array.Empty<float>() : new float[bindings];
                for (int b = 0; b < bindings; b++) {
                    s.Bindings[b] = reader.U16();
                    if ((s.Removed & (1u << b)) != 0 && s.Bindings[b] != 0) return false;
                    if ((s.Retiring & (1u << b)) != 0) { s.FadeAges[b] = reader.F32(); if (!float.IsFinite(s.FadeAges[b]) || s.FadeAges[b] < 0 || s.FadeAges[b] > 30) return false; }
                }
                for (int p = 0; p < i; p++) if (scenes[p].Id == s.Id) return false;
                scenes[i] = s;
            }
            if (reader.Failed || !reader.End) return false;
            roster = new CinematicRoster { Epoch = epoch, Sequence = sequence, ServerTime = serverTime, Echo = echo, Paused = paused, Scenes = scenes };
            return true;
        }
    }

    public sealed class CinematicReplica {
        private uint epoch, sequence;
        private bool known;
        private double offset;
        private readonly ServerClock clock = new ServerClock();
        public CinematicRoster Latest { get; private set; }
        public double ReceivedAt { get; private set; }
        public double RoundTripSeconds => clock.Valid ? clock.Delay : double.PositiveInfinity;
        public bool Accept(CinematicRoster roster, double now) {
            if (roster == null || !double.IsFinite(now) || !double.IsFinite(roster.ServerTime) || roster.ServerTime < 0 || !double.IsFinite(roster.Echo) || roster.Echo < 0) return false;
            if (known && (roster.Epoch != epoch ? unchecked((int)(roster.Epoch - epoch)) <= 0 : unchecked((int)(roster.Sequence - sequence)) <= 0)) return false;
            if (known && roster.Epoch == epoch && roster.ServerTime < Latest.ServerTime) return false;
            if (!known || roster.Epoch != epoch) { clock.Reset(); offset = roster.ServerTime - now; }
            if (roster.Echo > 0 && roster.Echo <= now) {
                double rtt = now - roster.Echo;
                if (rtt < 10) { clock.Add(roster.Echo, now, roster.ServerTime); offset = clock.Offset; }
            }
            epoch = roster.Epoch; sequence = roster.Sequence; known = true; Latest = roster; ReceivedAt = now; return true;
        }
        public double Age(double now) => Latest == null || Latest.Paused ? 0 : Math.Clamp(now + offset - Latest.ServerTime, 0, 6);
        public void Reset() { Latest = null; known = false; clock.Reset(); offset = 0; ReceivedAt = 0; }
    }
}
