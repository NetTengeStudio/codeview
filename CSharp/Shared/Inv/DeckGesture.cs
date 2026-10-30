using System;

namespace ScpItdMod.Inv {
    public static class DeckGesture {
        public const float ClickTravel = 7f;

        public static bool IsClick(bool sameItem, float travel) => sameItem && travel < ClickTravel;

        // The press that opens a nested container: the bound Select key or a fresh secondary-button press. Neither counts while the
        // primary button, a drag or typing holds the input, because the primary press already means take or move.
        public static bool OpensNested(bool selectPressed, bool secondaryPressed, bool primaryHeld, bool dragging, bool typing) =>
            (selectPressed || secondaryPressed) && !primaryHeld && !dragging && !typing;

        public static (int Start, int Count) StackSpan(int stack, bool half, bool one) {
            if (stack <= 0) { return (0, 0); }
            if (one) { return (0, 1); }
            if (half) { return (stack / 2, stack - stack / 2); }
            return (0, stack);
        }
    }

    public struct DeckDwell {
        private int key;
        private double since;
        private bool fired;

        public void Reset() { key = 0; since = 0; fired = false; }

        public bool Step(int target, double now, double wait, bool suppressed) {
            if (target == 0 || suppressed) { Reset(); return false; }
            if (target != key) { key = target; since = now; fired = false; return false; }
            if (fired || now - since < wait) { return false; }
            fired = true;
            return true;
        }
    }

    public struct DeckNavigation {
        public const double RootHoverSeconds = .12;
        private DeckDwell root;

        public void Reset() => root.Reset();

        public bool Step(int target, double now, bool nested, bool activate, bool captured) {
            if (nested) {
                root.Reset();
                return target != 0 && activate && !captured;
            }
            return root.Step(target, now, RootHoverSeconds, captured);
        }
    }

    public struct DeckGrace {
        private double since;
        private bool away;

        public void Reset() { since = 0; away = false; }

        public bool Step(bool inside, double now, double grace) {
            if (inside) { Reset(); return false; }
            if (!away) { away = true; since = now; return false; }
            if (now - since <= grace) { return false; }
            Reset();
            return true;
        }
    }

    public enum DeckPhase : byte { Hidden, Entering, Shown, Leaving }

    public static class DeckPresence {
        public const float Epsilon = 0.01f, Full = 0.98f;

        public static DeckPhase Next(DeckPhase phase, bool wanted, float reveal) {
            if (wanted) { return reveal >= Full ? DeckPhase.Shown : DeckPhase.Entering; }
            return phase == DeckPhase.Hidden || reveal <= Epsilon ? DeckPhase.Hidden : DeckPhase.Leaving;
        }

        public static bool Draws(DeckPhase phase) => phase != DeckPhase.Hidden;

        public static bool Interactive(DeckPhase phase) => phase == DeckPhase.Entering || phase == DeckPhase.Shown;
    }
}

namespace ScpItdMod.Inv {
    public enum WorldAction : byte { None, Open, Attach, Close }

    public struct WorldDeckGate {
        private int dismissed, opened;

        public bool Owns => opened != 0;

        // The deck shows one root at a time and the latest intent wins: a world container that becomes selected takes the deck over
        // from whatever it was showing, however deep into the character's own containers it had gone.
        public WorldAction Step(int wanted, bool ringOpen, bool eligible) {
            if (dismissed != 0 && dismissed != wanted) { dismissed = 0; }
            if (ringOpen) {
                if (opened != 0 && opened != wanted) { opened = 0; return WorldAction.Close; }
                if (opened == 0 && wanted != 0) { opened = wanted; return WorldAction.Attach; }
                return WorldAction.None;
            }
            opened = 0;
            if (wanted == 0 || wanted == dismissed || !eligible) { return WorldAction.None; }
            opened = wanted;
            return WorldAction.Open;
        }

        public void Dismiss() { dismissed = opened; opened = 0; }

        public void Reset() { dismissed = 0; opened = 0; }
    }
}
