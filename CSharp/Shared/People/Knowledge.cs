using System;
using System.Collections.Generic;
using System.Globalization;
using ScpItdMod.Docs;
using ScpItdMod.Net;

namespace ScpItdMod.People {
    public enum Acquired : byte { Introduced = 1, Team = 2, Granted = 3 }

    public sealed class Knowledge {
        public const int PerViewer = 1024;
        private const uint LegacyMagic = 0x574F4E4B;
        private const uint Magic = 0x324F4E4B;

        private readonly Dictionary<uint, Dictionary<uint, Acquired>> known = new Dictionary<uint, Dictionary<uint, Acquired>>();

        public int Revision { get; private set; }

        public bool Knows(uint viewer, uint target) =>
            viewer != 0 && target != 0 && (viewer == target || (known.TryGetValue(viewer, out var set) && set.ContainsKey(target)));

        public Acquired How(uint viewer, uint target) =>
            known.TryGetValue(viewer, out var set) && set.TryGetValue(target, out Acquired how) ? how : 0;

        public bool Learn(uint viewer, uint target, Acquired how = Acquired.Introduced) {
            if (viewer == 0 || target == 0 || viewer == target || how == 0) { return false; }
            if (!known.TryGetValue(viewer, out var set)) {
                set = new Dictionary<uint, Acquired>();
                known.Add(viewer, set);
            }
            if (set.ContainsKey(target) || set.Count >= PerViewer) { return false; }
            set.Add(target, how);
            Revision++;
            return true;
        }

        public bool Forget(uint viewer, uint target) {
            if (!known.TryGetValue(viewer, out var set) || !set.Remove(target)) { return false; }
            Revision++;
            return true;
        }

        public void Drop(uint profile) {
            known.Remove(profile);
            foreach (var set in known.Values) { set.Remove(profile); }
            Revision++;
        }

        public int Count(uint viewer) => known.TryGetValue(viewer, out var set) ? set.Count : 0;

        public void Save(WireWriter writer) {
            writer.Clear();
            writer.U32(Magic);
            writer.Var((ulong)known.Count);
            foreach (var pair in known) {
                writer.Var(pair.Key);
                writer.Var((ulong)pair.Value.Count);
                foreach (var entry in pair.Value) {
                    writer.Var(entry.Key);
                    writer.U8((byte)entry.Value);
                }
            }
            writer.U32(Crc32.Of(writer.Span));
        }

        public PeopleError Load(ReadOnlySpan<byte> data) {
            if (known.Count > 0) { return PeopleError.Invalid; }
            if (data.Length < 8 || Crc32.Of(data.Slice(0, data.Length - 4)) !=
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(data.Length - 4))) {
                return PeopleError.Corrupt;
            }
            var reader = new WireReader(data.Slice(0, data.Length - 4));
            uint magic = reader.U32();
            if (magic != Magic && magic != LegacyMagic) { return PeopleError.Corrupt; }
            bool legacy = magic == LegacyMagic;
            int viewers = reader.Count(1 << 20);
            var loaded = new Dictionary<uint, Dictionary<uint, Acquired>>();
            for (int v = 0; v < viewers && !reader.Failed; v++) {
                uint viewer = (uint)reader.Count(int.MaxValue);
                int count = reader.Count(PerViewer);
                var set = new Dictionary<uint, Acquired>();
                for (int i = 0; i < count && !reader.Failed; i++) {
                    uint target = (uint)reader.Count(int.MaxValue);
                    var how = legacy ? Acquired.Introduced : (Acquired)reader.U8();
                    if (how < Acquired.Introduced || how > Acquired.Granted) { return PeopleError.Corrupt; }
                    set[target] = how;
                }
                if (!loaded.TryAdd(viewer, set)) { return PeopleError.Corrupt; }
            }
            if (reader.Failed || !reader.End) { return PeopleError.Corrupt; }
            foreach (var pair in loaded) { known.Add(pair.Key, pair.Value); }
            Revision++;
            return PeopleError.Ok;
        }
    }

    public sealed class Roles {
        private readonly Dictionary<string, string> hidden = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public int Count => hidden.Count;

        public void Hide(string job, string template) {
            if (string.IsNullOrWhiteSpace(job) || string.IsNullOrWhiteSpace(template)) { return; }
            hidden[job.Trim()] = template.Trim();
        }

        public bool Hidden(string job) => job != null && hidden.ContainsKey(job);

        private readonly HashSet<(string, string)> teams = new HashSet<(string, string)>();

        public void Acquaint(string job, string with) {
            if (string.IsNullOrWhiteSpace(job) || string.IsNullOrWhiteSpace(with)) { return; }
            teams.Add((job.Trim().ToLowerInvariant(), with.Trim().ToLowerInvariant()));
        }

        public bool Teammates(string viewerJob, string targetJob) =>
            viewerJob != null && targetJob != null && teams.Contains((viewerJob.ToLowerInvariant(), targetJob.ToLowerInvariant()));

        public string Alias(string job, uint profile, Func<string, bool> taken = null) {
            if (job == null || !hidden.TryGetValue(job, out string template)) { return null; }
            int number = Number(job, profile);
            for (int attempt = 0; attempt < 9000; attempt++) {
                string alias = template.Replace("{n}", number.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
                if (taken == null || !taken(alias)) { return alias; }
                number = number >= 9999 ? 1000 : number + 1;
            }
            return null;
        }

        public static int Number(string job, uint profile) {
            uint h = 2166136261u;
            for (int i = 0; i < job.Length; i++) {
                h ^= char.ToLowerInvariant(job[i]);
                h *= 16777619u;
            }
            h ^= profile;
            h *= 16777619u;
            h ^= h >> 15;
            h *= 2246822519u;
            h ^= h >> 13;
            return 1000 + (int)(h % 9000u);
        }
    }
}
