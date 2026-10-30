using System.Collections.Generic;
using ScpItdMod.Net;

namespace ScpItdMod.Rp {
    public enum RpMsg : byte { Act, Cancel, Answer, Started, Ended, Offer, State, Note, Emote, EmoteStart, EmoteStop, Permit, Shown, Synchronize }

    public enum RpEnd : byte { Done, Cancelled, Interrupted, Denied, Declined, Expired }

    public sealed class RpRun {
        public RpAct Act;
        public ushort Actor;
        public ushort Target;
        public ushort Item;
        public double Start;
        public float Duration;

        public float Progress(double now) => Duration <= 0.0f ? 1.0f : (float)System.Math.Clamp((now - Start) / Duration, 0.0, 1.0);

        public bool Finished(double now) => now - Start >= Duration;
    }

    public static class RpWire {
        public const byte Proto = 2;
        public const int MaxRequest = 64;

        public static void Synchronize(WireWriter w) {
            w.Clear(); w.U8(Proto); w.U8((byte)RpMsg.Synchronize);
        }

        public static void Act(WireWriter w, RpAct act, ushort target, ushort item) {
            w.Clear();
            w.U8(Proto);
            w.U8((byte)RpMsg.Act);
            w.U8((byte)act);
            w.U32(target);
            w.U32(item);
        }

        public static void Cancel(WireWriter w) {
            w.Clear();
            w.U8(Proto);
            w.U8((byte)RpMsg.Cancel);
        }

        public static void Answer(WireWriter w, uint offer, bool accept) {
            w.Clear();
            w.U8(Proto);
            w.U8((byte)RpMsg.Answer);
            w.Var(offer);
            w.Bool(accept);
        }

        public static void Started(WireWriter w, RpAct act, ushort actor, ushort target, float duration) {
            w.Clear();
            w.U8(Proto);
            w.U8((byte)RpMsg.Started);
            w.U8((byte)act);
            w.U32(actor);
            w.U32(target);
            w.U32((uint)System.BitConverter.SingleToInt32Bits(duration));
        }

        public static void Ended(WireWriter w, RpAct act, RpEnd end, RpDeny deny) {
            w.Clear();
            w.U8(Proto);
            w.U8((byte)RpMsg.Ended);
            w.U8((byte)act);
            w.U8((byte)end);
            w.U8((byte)deny);
        }

        public static void Offer(WireWriter w, uint offer, ushort from, string itemName) {
            w.Clear();
            w.U8(Proto);
            w.U8((byte)RpMsg.Offer);
            w.Var(offer);
            w.U32(from);
            w.Text(itemName ?? "");
        }

        public static void State(WireWriter w, List<ushort> surrendered, List<ushort> hidden, uint revision) {
            w.Clear();
            w.U8(Proto);
            w.U8((byte)RpMsg.State);
            w.U32(revision);
            WriteIds(w, surrendered);
            WriteIds(w, hidden);
        }

        public static void Note(WireWriter w, string text) {
            w.Clear();
            w.U8(Proto);
            w.U8((byte)RpMsg.Note);
            w.Text(text ?? "");
        }

        public static void Emote(WireWriter w, byte index, System.ReadOnlySpan<byte> catalog) {
            w.Clear();
            w.U8(Proto);
            w.U8((byte)RpMsg.Emote);
            w.U8(index);
            w.Bytes(catalog);
        }

        public static void EmoteStart(WireWriter w, ushort actor, ulong run, byte index, byte variant, float age, System.ReadOnlySpan<byte> catalog) {
            w.Clear();
            w.U8(Proto);
            w.U8((byte)RpMsg.EmoteStart);
            w.U32(actor);
            w.U64(run);
            w.U8(index);
            w.U8(variant);
            w.U32((uint)System.BitConverter.SingleToInt32Bits(age));
            w.Bytes(catalog);
        }

        public static void EmoteStop(WireWriter w, ushort actor, ulong run, EmoteDeny why) {
            w.Clear();
            w.U8(Proto);
            w.U8((byte)RpMsg.EmoteStop);
            w.U32(actor);
            w.U64(run);
            w.U8((byte)why);
        }

        public static void Permit(WireWriter w, ushort target, float seconds) {
            w.Clear();
            w.U8(Proto);
            w.U8((byte)RpMsg.Permit);
            w.U32(target);
            w.U32((uint)System.BitConverter.SingleToInt32Bits(seconds));
        }

        public static void Shown(WireWriter w, ushort from, ushort item) {
            w.Clear();
            w.U8(Proto);
            w.U8((byte)RpMsg.Shown);
            w.U32(from);
            w.U32(item);
        }

        public static void WriteIds(WireWriter w, List<ushort> ids) {
            w.Var((ulong)ids.Count);
            for (int i = 0; i < ids.Count; i++) { w.U32(ids[i]); }
        }

        public static bool ReadIds(ref WireReader r, List<ushort> ids) {
            ids.Clear();
            int count = r.Count(2048);
            for (int i = 0; i < count && !r.Failed; i++) {
                uint id = r.U32();
                if (id > ushort.MaxValue) { return false; }
                ids.Add((ushort)id);
            }
            return !r.Failed;
        }

        public static bool ReadEntity(ref WireReader r, out ushort id) {
            uint raw = r.U32();
            id = (ushort)raw;
            return !r.Failed && raw <= ushort.MaxValue;
        }
    }
}
