using Barotrauma;

namespace ScpItdMod.Core {
    public static class PlayView {
        public static bool Busy => GUI.InputBlockingMenuOpen || CharacterHealth.OpenHealthWindow != null;

        public static bool Typing => GUI.KeyboardDispatcher.Subscriber != null;

        public static bool Active(Character character) =>
            GameMain.GameSession != null && Screen.Selected == GameMain.GameScreen &&
            character != null && !character.Removed && !character.IsDead;
    }
}
