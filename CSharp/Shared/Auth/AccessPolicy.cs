using System;
using System.Collections.Generic;
using System.Globalization;

namespace ScpItdMod.Auth {
    public sealed class Credentials {
        public readonly HashSet<string> Jobs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public int Clearance;

        public void Clear() {
            Jobs.Clear();
            Tags.Clear();
            Clearance = 0;
        }

        public void AddTag(string tag) {
            if (string.IsNullOrEmpty(tag)) { return; }
            Tags.Add(tag);
            int level = AccessPolicy.ClearanceOf(tag);
            if (level > Clearance) { Clearance = level; }
        }
    }

    public sealed class AccessPolicy {
        public const string ClearancePrefix = "clearance";

        private enum Kind : byte { Job, Tag, Clearance }

        private readonly struct Term {
            public readonly Kind Kind;
            public readonly string Value;
            public readonly int Level;

            public Term(Kind kind, string value, int level) {
                Kind = kind;
                Value = value;
                Level = level;
            }
        }

        private readonly Term[] terms;
        private readonly bool valid;

        private AccessPolicy(Term[] terms, string source, bool valid = true) {
            this.terms = terms;
            Source = source;
            this.valid = valid;
        }

        public static readonly AccessPolicy Open = new AccessPolicy(Array.Empty<Term>(), "");
        private static readonly AccessPolicy Denied = new AccessPolicy(Array.Empty<Term>(), "invalid", false);

        public string Source { get; }

        public bool IsOpen => valid && terms.Length == 0;

        public static bool TryParse(string text, out AccessPolicy policy) {
            policy = Denied;
            if (string.IsNullOrWhiteSpace(text)) { policy = Open; return true; }
            var list = new List<Term>();
            foreach (string raw in text.Split('|')) {
                string part = raw.Trim();
                if (part.Length == 0) { return false; }
                if (part.StartsWith("job:", StringComparison.OrdinalIgnoreCase)) {
                    string job = part.Substring(4).Trim();
                    if (job.Length == 0) { return false; }
                    list.Add(new Term(Kind.Job, job, 0));
                } else if (part.StartsWith("tag:", StringComparison.OrdinalIgnoreCase)) {
                    string tag = part.Substring(4).Trim();
                    if (tag.Length == 0) { return false; }
                    list.Add(new Term(Kind.Tag, tag, 0));
                } else if (part.StartsWith("clearance>=", StringComparison.OrdinalIgnoreCase)) {
                    if (!int.TryParse(part.Substring(11).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int level) || level < 0) { return false; }
                    list.Add(new Term(Kind.Clearance, null, level));
                } else {
                    return false;
                }
            }
            policy = new AccessPolicy(list.ToArray(), text.Trim());
            return true;
        }

        public bool Allows(Credentials c) {
            if (!valid) { return false; }
            if (terms.Length == 0) { return true; }
            if (c == null) { return false; }
            for (int i = 0; i < terms.Length; i++) {
                Term t = terms[i];
                switch (t.Kind) {
                    case Kind.Job:
                        if (c.Jobs.Contains(t.Value)) { return true; }
                        break;
                    case Kind.Tag:
                        if (c.Tags.Contains(t.Value)) { return true; }
                        break;
                    case Kind.Clearance:
                        if (c.Clearance >= t.Level) { return true; }
                        break;
                }
            }
            return false;
        }

        public static int ClearanceOf(string tag) {
            if (tag == null || tag.Length <= ClearancePrefix.Length || !tag.StartsWith(ClearancePrefix, StringComparison.OrdinalIgnoreCase)) { return 0; }
            int start = ClearancePrefix.Length;
            if (tag[start] == ':' || tag[start] == '_') { start++; }
            return int.TryParse(tag.AsSpan(start), NumberStyles.None, CultureInfo.InvariantCulture, out int level) ? level : 0;
        }

        public static AccessPolicy FromTags(IEnumerable<string> tags, string prefix) {
            var parts = new List<string>();
            foreach (string tag in tags) {
                if (tag == null || !tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { continue; }
                string rest = tag.Substring(prefix.Length);
                int split = rest.IndexOf('_');
                if (split <= 0) { return Denied; }
                string kind = rest.Substring(0, split);
                string value = rest.Substring(split + 1);
                if (kind.Equals("clearance", StringComparison.OrdinalIgnoreCase)) {
                    parts.Add("clearance>=" + value);
                } else if (kind.Equals("job", StringComparison.OrdinalIgnoreCase) || kind.Equals("tag", StringComparison.OrdinalIgnoreCase)) {
                    parts.Add(kind + ":" + value);
                } else { return Denied; }
            }
            return parts.Count == 0 ? Open : TryParse(string.Join("|", parts), out AccessPolicy policy) ? policy : Denied;
        }
    }

    public sealed class ChannelPlan {
        public readonly struct Band {
            public readonly int From;
            public readonly int To;
            public readonly string Name;
            public readonly AccessPolicy Policy;

            public Band(int from, int to, string name, AccessPolicy policy) {
                From = from;
                To = to;
                Name = name;
                Policy = policy;
            }
        }

        private readonly List<Band> bands = new List<Band>();

        public int Count => bands.Count;

        public bool Add(int from, int to, string name, string access) {
            if (from > to || from < 0 || !AccessPolicy.TryParse(access, out AccessPolicy policy)) { return false; }
            for (int i = 0; i < bands.Count; i++) {
                if (from <= bands[i].To && to >= bands[i].From) { return false; }
            }
            bands.Add(new Band(from, to, string.IsNullOrWhiteSpace(name) ? $"{from}–{to}" : name.Trim(), policy));
            bands.Sort((a, b) => a.From.CompareTo(b.From));
            return true;
        }

        public bool Find(int channel, out Band band) {
            int lo = 0;
            int hi = bands.Count - 1;
            while (lo <= hi) {
                int mid = (lo + hi) >> 1;
                Band b = bands[mid];
                if (channel < b.From) { hi = mid - 1; } else if (channel > b.To) { lo = mid + 1; } else {
                    band = b;
                    return true;
                }
            }
            band = default;
            return false;
        }

        public bool Allows(int channel, Credentials c) => !Find(channel, out Band band) || band.Policy.Allows(c);
    }
}
