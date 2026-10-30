using System;

namespace ScpItdMod.Hud {
    public struct TypeClickCursor {
        private int consumed;

        public bool Advance(TypeText text, int limit, float age) {
            if (text.FadeDuration <= 0.0f || !(age >= 0.0f)) { return false; }
            float onset = MathF.Min(0.025f, text.FadeDuration * 0.15f);
            int reached = Math.Min(limit, text.Revealed(age - onset));
            if (reached <= consumed) { return false; }
            int candidate = reached - 1;
            while (candidate >= consumed && char.IsWhiteSpace(text.Plain[text.Start(candidate)])) { candidate--; }
            bool fresh = candidate >= consumed && age - onset - text.At(candidate) <= TypeClickBudget.StaleSeconds;
            consumed = reached;
            return fresh;
        }
    }

    public struct TypeClickBudget {
        public const float IntervalSeconds = 0.065f;
        public const float StaleSeconds = 0.07f;
        private double next;

        public bool Try(double now) {
            if (!double.IsFinite(now) || now < next) { return false; }
            next = now + IntervalSeconds;
            return true;
        }
    }
}
