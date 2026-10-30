namespace ScpItdMod.Life {
    public enum DeathDeny : byte { None, Disabled, NoCharacter, AlreadyDead, Bound, BeingHeld, TooSoon }

    public struct DeathFacts {
        public bool Enabled;
        public bool HasCharacter;
        public bool Alive;
        public bool Bound;
        public bool BeingHeld;
        public double SinceSpawn;
    }

    public static class DeathRules {
        public const float HoldSeconds = 3.0f;
        public const double MinLifeSeconds = 30.0;

        public static DeathDeny Check(in DeathFacts f) {
            if (!f.Enabled) { return DeathDeny.Disabled; }
            if (!f.HasCharacter) { return DeathDeny.NoCharacter; }
            if (!f.Alive) { return DeathDeny.AlreadyDead; }
            if (f.Bound) { return DeathDeny.Bound; }
            if (f.BeingHeld) { return DeathDeny.BeingHeld; }
            if (f.SinceSpawn < MinLifeSeconds) { return DeathDeny.TooSoon; }
            return DeathDeny.None;
        }

        public static string Explain(DeathDeny deny) => deny switch {
            DeathDeny.Disabled => "На этом сервере добровольная смерть отключена.",
            DeathDeny.NoCharacter => "У вас нет персонажа.",
            DeathDeny.AlreadyDead => "Персонаж уже мёртв.",
            DeathDeny.Bound => "Нельзя, пока вы связаны.",
            DeathDeny.BeingHeld => "Нельзя, пока с вами что-то делают (обыск, задержание, диагностика).",
            DeathDeny.TooSoon => "Персонаж появился меньше 30 секунд назад.",
            _ => ""
        };
    }
}
