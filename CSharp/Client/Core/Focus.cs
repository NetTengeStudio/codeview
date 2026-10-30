using Barotrauma;

namespace ScpItdMod.Core {
    public struct Focus {
        private Character tracked;

        public bool Tracking => tracked != null;

        public bool Follow(Character character, out bool renewed) {
            if (!PlayView.Active(character)) {
                renewed = tracked != null;
                tracked = null;
                return false;
            }
            renewed = !ReferenceEquals(character, tracked);
            tracked = character;
            return true;
        }

        public void Clear() => tracked = null;
    }
}
