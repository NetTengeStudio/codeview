namespace ScpItdMod.Ui {
    public sealed class ClickLatch {
        private bool pressed;
        private bool released;
        private bool alt;
        private int wheel;

        public void Feed(bool press, bool release, bool altClick, int wheelDelta) {
            pressed |= press;
            released |= release;
            alt |= altClick;
            wheel += wheelDelta;
        }

        public void Take(bool held, ref UiInput input) {
            input.Pressed = pressed;
            input.Released = released && !pressed;
            input.Down = pressed || held;
            input.AltPressed = alt;
            input.Wheel = wheel == 0 ? 0.0f : System.MathF.Sign(wheel) * System.MathF.Max(1.0f, System.MathF.Abs(wheel) / 120.0f);
            released &= pressed;
            pressed = false;
            alt = false;
            wheel = 0;
        }

        public void Clear() {
            pressed = released = alt = false;
            wheel = 0;
        }
    }
}
