using System;

namespace ScpItdMod.Weather {
    public enum SnowContact : byte { None, Dusting, Loose, Packed, Deep }

    public readonly struct SnowFooting {
        public readonly float Depth, Compaction, Fresh, Sinkage;
        public readonly SnowContact Contact;
        public SnowFooting(float depth, float compaction, float fresh, float sinkage) {
            Depth = depth; Compaction = compaction; Fresh = fresh; Sinkage = sinkage; Contact = SnowContactModel.Classify(depth, compaction, fresh);
        }
        public static readonly SnowFooting Bare = default;
    }

    public static class SnowContactModel {
        public const float DustingDepth = .02f, LooseDepth = .08f, DeepDepth = .28f, SoftLayer = .12f, PackedCompaction = .55f;
        public const float FootAbove = .3f, FootBelow = .45f;

        public static SnowContact Classify(float depth, float compaction, float fresh) {
            if (!(depth >= DustingDepth)) return SnowContact.None;
            if (depth < LooseDepth) return SnowContact.Dusting;
            float soft = Math.Clamp(fresh, 0, depth);
            if (soft >= SoftLayer || depth >= DeepDepth && compaction < PackedCompaction) return SnowContact.Deep;
            return compaction >= PackedCompaction ? SnowContact.Packed : SnowContact.Loose;
        }
    }
}
