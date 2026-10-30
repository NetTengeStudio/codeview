namespace ScpItdMod.Weather {
    public enum FloorKind : byte { Unknown, Metal, Snow, DeepSnow }

    public static class FloorMaterial {
        public const string MetalTag = "scpitd_floor_metal", SnowTag = "scpitd_floor_snow";
        public const float SweepReach = .35f;

        public static FloorKind Resolve(bool metalTag, bool snowTag, SnowContact contact, bool interior) {
            if (contact == SnowContact.Deep) return FloorKind.DeepSnow;
            if (contact == SnowContact.Dusting || contact == SnowContact.Packed || contact == SnowContact.Loose) return FloorKind.Snow;
            if (metalTag) return FloorKind.Metal;
            if (snowTag) return interior ? FloorKind.Metal : FloorKind.Snow;
            return FloorKind.Unknown;
        }
    }
}
