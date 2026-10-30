namespace ScpItdMod.Rp {
    public enum RpAct : byte { None, Examine, Introduce, Give, Search, Detain, Free, Hide, Leave, Surrender, Diagnose, Present }

    public enum RpDeny : byte {
        None, TooFar, NoTarget, TargetDead, SelfBusy, SelfBound, NoItem, HandsFull, Resisting, NoCuffs, NotBound,
        AlreadyBound, NotHideout, Occupied, NotHidden, Hidden, Self, NoSkill, NoRoom, Unconscious
    }

    public struct RpFacts {
        public float Distance;
        public bool HasTarget;
        public bool TargetIsSelf;
        public bool TargetIsCharacter;
        public bool TargetAlive;
        public bool TargetConscious;
        public bool TargetBound;
        public bool TargetSurrendered;
        public bool TargetHandsFree;
        public bool TargetIsHideout;
        public bool HideoutOccupied;
        public bool SelfConscious;
        public bool SelfBound;
        public bool SelfHidden;
        public bool SelfBusy;
        public bool HoldingItem;
        public bool HoldingCuffs;
        public bool CanDiagnose;
    }

    public static class RpRules {
        public const float Slack = 1.3f;

        public static float Range(RpAct act) => act switch {
            RpAct.Examine => 350.0f,
            RpAct.Introduce => 250.0f,
            RpAct.Give => 150.0f,
            RpAct.Search => 150.0f,
            RpAct.Detain => 130.0f,
            RpAct.Free => 130.0f,
            RpAct.Hide => 140.0f,
            RpAct.Diagnose => 130.0f,
            RpAct.Present => 250.0f,
            _ => float.MaxValue
        };

        public static float Duration(RpAct act) => act switch {
            RpAct.Search => 3.0f,
            RpAct.Detain => 4.0f,
            RpAct.Free => 2.0f,
            RpAct.Hide => 1.2f,
            RpAct.Leave => 0.6f,
            RpAct.Diagnose => 5.0f,
            _ => 0.0f
        };

        public static bool Timed(RpAct act) => Duration(act) > 0.0f;

        public static bool OnSelf(RpAct act) => act == RpAct.Surrender || act == RpAct.Leave;

        public static RpDeny Check(RpAct act, in RpFacts f) {
            if (!f.SelfConscious) { return RpDeny.SelfBusy; }
            if (f.SelfBusy) { return RpDeny.SelfBusy; }
            if (act == RpAct.Leave) { return f.SelfHidden ? RpDeny.None : RpDeny.NotHidden; }
            if (f.SelfHidden) { return RpDeny.Hidden; }
            if (act == RpAct.Surrender) { return f.SelfBound ? RpDeny.SelfBound : RpDeny.None; }
            if (!f.HasTarget) { return RpDeny.NoTarget; }
            if (f.TargetIsSelf) { return RpDeny.Self; }
            if (f.Distance > Range(act)) { return RpDeny.TooFar; }
            if (act == RpAct.Hide) {
                if (!f.TargetIsHideout) { return RpDeny.NotHideout; }
                if (f.HideoutOccupied) { return RpDeny.Occupied; }
                return f.SelfBound ? RpDeny.SelfBound : RpDeny.None;
            }
            if (!f.TargetIsCharacter) { return RpDeny.NoTarget; }
            if (act == RpAct.Examine) { return RpDeny.None; }
            if (!f.TargetAlive) { return act == RpAct.Search ? RpDeny.None : RpDeny.TargetDead; }
            switch (act) {
                case RpAct.Introduce:
                    return f.TargetConscious ? RpDeny.None : RpDeny.TargetDead;
                case RpAct.Give:
                    if (f.SelfBound) { return RpDeny.SelfBound; }
                    if (!f.HoldingItem) { return RpDeny.NoItem; }
                    if (!f.TargetConscious) { return RpDeny.Unconscious; }
                    if (f.TargetBound) { return RpDeny.HandsFull; }
                    return f.TargetHandsFree ? RpDeny.None : RpDeny.NoRoom;
                case RpAct.Present:
                    if (!f.HoldingItem) { return RpDeny.NoItem; }
                    return f.TargetConscious ? RpDeny.None : RpDeny.Unconscious;
                case RpAct.Search:
                    if (f.SelfBound) { return RpDeny.SelfBound; }
                    return Yielding(f) ? RpDeny.None : RpDeny.Resisting;
                case RpAct.Detain:
                    if (f.SelfBound) { return RpDeny.SelfBound; }
                    if (f.TargetBound) { return RpDeny.AlreadyBound; }
                    if (!f.HoldingCuffs) { return RpDeny.NoCuffs; }
                    return Yielding(f) ? RpDeny.None : RpDeny.Resisting;
                case RpAct.Free:
                    if (f.SelfBound) { return RpDeny.SelfBound; }
                    return f.TargetBound ? RpDeny.None : RpDeny.NotBound;
                case RpAct.Diagnose:
                    if (f.SelfBound) { return RpDeny.SelfBound; }
                    return f.CanDiagnose ? RpDeny.None : RpDeny.NoSkill;
                default:
                    return RpDeny.NoTarget;
            }
        }

        public static bool Yielding(in RpFacts f) => !f.TargetConscious || f.TargetBound || f.TargetSurrendered;

        public static bool Interrupted(RpAct act, in RpFacts f) {
            if (OnSelf(act)) { return Check(act, f) != RpDeny.None; }
            if (f.Distance > Range(act) * Slack) { return true; }
            var relaxed = f;
            relaxed.Distance = 0.0f;
            relaxed.SelfBusy = false;
            return Check(act, relaxed) != RpDeny.None;
        }

        public static string Explain(RpDeny deny) => deny switch {
            RpDeny.None => "",
            RpDeny.TooFar => "Слишком далеко",
            RpDeny.NoTarget => "Нет цели",
            RpDeny.TargetDead => "Человек не реагирует",
            RpDeny.SelfBusy => "Вы заняты или без сознания",
            RpDeny.SelfBound => "У вас связаны руки",
            RpDeny.NoItem => "Выберите свой предмет",
            RpDeny.HandsFull => "У человека заняты руки",
            RpDeny.Resisting => "Человек сопротивляется: нужно, чтобы он сдался, был связан или без сознания",
            RpDeny.NoCuffs => "Нужны ваши наручники",
            RpDeny.NotBound => "Человек не связан",
            RpDeny.AlreadyBound => "Уже связан",
            RpDeny.NotHideout => "Здесь не спрятаться",
            RpDeny.Occupied => "Здесь уже кто-то есть",
            RpDeny.NotHidden => "Вы не прячетесь",
            RpDeny.Hidden => "Сначала выйдите из укрытия",
            RpDeny.Self => "Это вы",
            RpDeny.NoSkill => "Нужны медицинские навыки или сканер здоровья в руке",
            RpDeny.NoRoom => "Человеку некуда это положить",
            RpDeny.Unconscious => "Человек без сознания",
            _ => ""
        };

        public static string Title(RpAct act) => act switch {
            RpAct.Examine => "Осмотреть",
            RpAct.Introduce => "Представиться",
            RpAct.Give => "Передать предмет",
            RpAct.Search => "Обыскать",
            RpAct.Detain => "Связать",
            RpAct.Free => "Освободить",
            RpAct.Hide => "Спрятаться",
            RpAct.Leave => "Выйти",
            RpAct.Surrender => "Сдаться",
            RpAct.Diagnose => "Диагностика",
            RpAct.Present => "Показать предмет",
            _ => ""
        };
    }
}
