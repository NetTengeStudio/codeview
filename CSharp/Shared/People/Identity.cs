namespace ScpItdMod.People {
    public enum Sense : byte { Sight, Voice, Radio }

    public enum Shown : byte { Real, Callsign, Masked, Unseen, Admin }

    public struct IdentityFacts {
        public bool Self;
        public bool Gm;
        public bool Known;
        public bool Masked;
        public bool Visible;
    }

    public readonly struct Designation {
        public readonly Shown Kind;
        public readonly string Text;

        public Designation(Shown kind, string text) {
            Kind = kind;
            Text = text;
        }
    }

    public static class Identity {
        public const string MaskedLabel = "Человек в маске";
        public const string UnseenLabel = "Голос";

        public static Shown Decide(Sense sense, in IdentityFacts f) {
            if (f.Gm && !f.Self) { return Shown.Admin; }
            if (f.Self || f.Known) {
                if (sense != Sense.Sight || !f.Masked || f.Self) { return Shown.Real; }
                return Shown.Masked;
            }
            if (sense == Sense.Radio) { return Shown.Callsign; }
            if (sense == Sense.Voice && !f.Visible) { return Shown.Unseen; }
            return f.Masked ? Shown.Masked : Shown.Callsign;
        }

        public static Designation Label(Sense sense, in IdentityFacts f, string real, string callsign) {
            Shown kind = Decide(sense, f);
            string text = kind switch {
                Shown.Real => string.IsNullOrEmpty(real) ? callsign : real,
                Shown.Admin => string.IsNullOrEmpty(real) || real == callsign ? callsign : real + " (" + callsign + ")",
                Shown.Masked => MaskedLabel,
                Shown.Unseen => UnseenLabel,
                _ => callsign
            };
            return new Designation(kind, text ?? "");
        }
    }
}
