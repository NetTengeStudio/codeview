using System;
using System.Collections.Generic;
using System.Text;
using ScpItdMod.Net;

namespace ScpItdMod.Rec {
    public enum WornSlot : byte { Head, Outer, Inner, Bag, Right, Left }

    public sealed class LookDesc {
        public const int Slots = 6;

        public string HeadTags = "";
        public byte Hair;
        public byte Beard;
        public byte Moustache;
        public byte Face;
        public uint Skin;
        public uint HairColor;
        public uint FacialColor;
        public readonly string[] Items = new string[Slots];
        public readonly byte[] Variants = new byte[Slots];
        public bool Masked;
        public string Label = "";

        public string Key { get; private set; }
        public ulong Hash { get; private set; }
        public int WireBytes { get; private set; }

        public string Item(WornSlot s) => Items[(int)s];

        public LookDesc Seal() {
            var b = new StringBuilder(96);
            b.Append(HeadTags).Append('|').Append(Hair).Append(',').Append(Beard).Append(',').Append(Moustache).Append(',').Append(Face)
             .Append('|').Append(Skin).Append(',').Append(HairColor).Append(',').Append(FacialColor).Append('|').Append(Masked ? '1' : '0');
            for (int i = 0; i < Slots; i++) { b.Append('|').Append(Items[i]).Append(':').Append(Variants[i]); }
            b.Append('|').Append(Label);
            Key = b.ToString();
            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < Key.Length; i++) { hash = (hash ^ Key[i]) * 1099511628211UL; }
            Hash = hash;
            WireBytes = 23 + TextBytes(HeadTags) + TextBytes(Label);
            for (int i = 0; i < Slots; i++) { WireBytes += TextBytes(Items[i]); }
            return this;
        }

        private static int TextBytes(string text) {
            int bytes = Encoding.UTF8.GetByteCount(text ?? ""), prefix = 1;
            for (int remaining = bytes; remaining >= 128; remaining >>= 7) { prefix++; }
            return bytes + prefix;
        }

        public void Write(WireWriter w) {
            w.Text(HeadTags);
            w.U8(Hair);
            w.U8(Beard);
            w.U8(Moustache);
            w.U8(Face);
            w.U32(Skin);
            w.U32(HairColor);
            w.U32(FacialColor);
            w.Bool(Masked);
            for (int i = 0; i < Slots; i++) {
                w.Text(Items[i] ?? "");
                w.U8(Variants[i]);
            }
            w.Text(Label);
        }

        public static LookDesc Read(ref WireReader r) {
            var d = new LookDesc {
                HeadTags = r.Text(256), Hair = r.U8(), Beard = r.U8(), Moustache = r.U8(), Face = r.U8(),
                Skin = r.U32(), HairColor = r.U32(), FacialColor = r.U32(), Masked = r.Bool()
            };
            for (int i = 0; i < Slots; i++) {
                string id = r.Text(256);
                d.Items[i] = id.Length == 0 ? null : id;
                d.Variants[i] = r.U8();
            }
            d.Label = r.Text(256);
            return d.Seal();
        }

        public long Bytes {
            get {
                long n = 160 + (Key?.Length ?? 0) * 2 + HeadTags.Length * 2 + Label.Length * 2;
                for (int i = 0; i < Slots; i++) { n += (Items[i]?.Length ?? 0) * 2; }
                return n;
            }
        }
    }

    public sealed class LookTable : ILookRefs {
        private readonly Dictionary<string, int> byKey = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<LookDesc> byId = new List<LookDesc> { null };
        private readonly List<int> refs = new List<int> { 0 };
        private readonly Stack<int> free = new Stack<int>();
        private readonly int max;

        public LookTable(int maxEntries = 4096) => max = maxEntries;

        public int Live { get; private set; }

        public long Bytes { get; private set; }

        public LookDesc Get(int id) => (uint)id < (uint)byId.Count ? byId[id] : null;

        public int RefsOf(int id) => (uint)id < (uint)refs.Count ? refs[id] : 0;

        public int Intern(LookDesc d) {
            if (d?.Key == null) { return 0; }
            if (byKey.TryGetValue(d.Key, out int id)) {
                refs[id]++;
                return id;
            }
            if (Live >= max) { return 0; }
            while (free.Count > 0 && byId[free.Peek()] != null) { free.Pop(); }
            if (free.Count > 0) {
                id = free.Pop();
                byId[id] = d;
                refs[id] = 1;
            } else {
                id = byId.Count;
                byId.Add(d);
                refs.Add(1);
            }
            byKey[d.Key] = id;
            Live++;
            Bytes += d.Bytes;
            return id;
        }

        public bool Put(int id, LookDesc d) {
            if (id <= 0 || d?.Key == null || id > 1 << 20) { return false; }
            if (Get(id) is LookDesc existing) { return existing.Hash == d.Hash; }
            if (Live >= max) { return false; }
            while (byId.Count <= id) {
                byId.Add(null);
                refs.Add(0);
            }
            byId[id] = d;
            refs[id] = 1;
            byKey[d.Key] = id;
            Live++;
            Bytes += d.Bytes;
            return true;
        }

        public void Retain(int id) {
            if (id > 0 && id < refs.Count && byId[id] != null) { refs[id]++; }
        }

        public void Release(int id) {
            if (id <= 0 || id >= refs.Count || byId[id] == null) { return; }
            if (--refs[id] > 0) { return; }
            LookDesc d = byId[id];
            if (byKey.TryGetValue(d.Key, out int mapped) && mapped == id) { byKey.Remove(d.Key); }
            byId[id] = null;
            refs[id] = 0;
            free.Push(id);
            Live--;
            Bytes -= d.Bytes;
        }

        public void Clear() {
            byKey.Clear();
            byId.Clear();
            byId.Add(null);
            refs.Clear();
            refs.Add(0);
            free.Clear();
            Live = 0;
            Bytes = 0;
        }
    }
}
