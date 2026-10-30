namespace ScpItdMod.Rp {
    public enum SlotKind : byte { Hand, Pocket, Head, Outer, Inner, Headset, Card, Bag, Nested, Other }

    public enum TargetState : byte { Resisting, Cooperating, Bound, Unconscious, Dead }

    public enum TakeDeny : byte { None, NoPermit, Equipment, Clothing, Resisting, Locked }

    public static class SearchRules {
        public const float SearchSeconds = 3.0f;
        public const float QuickSearchSeconds = 1.6f;
        public const float PermitSeconds = 90.0f;
        public const float PermitRangeFactor = 2.0f;

        public static TargetState State(bool dead, bool conscious, bool bound, bool surrendered) {
            if (dead) { return TargetState.Dead; }
            if (!conscious) { return TargetState.Unconscious; }
            if (bound) { return TargetState.Bound; }
            return surrendered ? TargetState.Cooperating : TargetState.Resisting;
        }

        public static bool CanSearch(TargetState s) => s != TargetState.Resisting;

        public static float Duration(TargetState s) => s == TargetState.Cooperating ? SearchSeconds : QuickSearchSeconds;

        public static bool NeedsPermit(TargetState s) => s != TargetState.Dead;

        public static TakeDeny Take(SlotKind slot, TargetState state, bool permitted) {
            if (state == TargetState.Dead) { return TakeDeny.None; }
            if (!permitted) { return TakeDeny.NoPermit; }
            switch (state) {
                case TargetState.Resisting:
                    return TakeDeny.Resisting;
                case TargetState.Cooperating:
                    return slot == SlotKind.Hand || slot == SlotKind.Pocket || slot == SlotKind.Nested ? TakeDeny.None : TakeDeny.Equipment;
                default:
                    return slot == SlotKind.Inner ? TakeDeny.Clothing : TakeDeny.None;
            }
        }

        public static TakeDeny Put(SlotKind slot, TargetState state, bool permitted) {
            if (slot == SlotKind.Hand && state == TargetState.Bound) { return TakeDeny.Locked; }
            return Take(slot, state, permitted);
        }

        public static string Explain(TakeDeny d) => d switch {
            TakeDeny.NoPermit => "Сначала обыщите человека",
            TakeDeny.Equipment => "Снаряжение снимают только со связанного или без сознания; попросите передать",
            TakeDeny.Clothing => "Нижнюю одежду не снимают",
            TakeDeny.Resisting => "Человек сопротивляется",
            TakeDeny.Locked => "Руки скованы",
            _ => ""
        };

        public static string Title(SlotKind slot) => slot switch {
            SlotKind.Hand => "В руке",
            SlotKind.Pocket => "Карман",
            SlotKind.Head => "Голова",
            SlotKind.Outer => "Одежда",
            SlotKind.Inner => "Под одеждой",
            SlotKind.Headset => "Гарнитура",
            SlotKind.Card => "Карта",
            SlotKind.Bag => "Сумка",
            SlotKind.Nested => "Внутри",
            _ => "Слот"
        };
    }
}
