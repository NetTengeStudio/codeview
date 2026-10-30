namespace ScpItdMod.Ui {
    public static class TooltipRoute {
        public const bool RunVanilla = true;
        public const bool SkipVanilla = false;
    }

    public enum TooltipPhase : byte { Idle, Held, ExitDue, Exiting }

    public static class TooltipTiming {
        public const double Grace = 0.1;

        public static bool Holds(double lastRequest, double now) => now - lastRequest <= Grace;

        public static TooltipPhase PhaseOf(bool hasVisual, bool exiting, double lastRequest, double now) =>
            !hasVisual ? TooltipPhase.Idle
            : exiting ? TooltipPhase.Exiting
            : Holds(lastRequest, now) ? TooltipPhase.Held
            : TooltipPhase.ExitDue;

        public static bool DrawsWithoutRequest(TooltipPhase phase) => phase != TooltipPhase.Idle;
    }
}
