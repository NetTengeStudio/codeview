using ScpItdMod.Ui;

namespace ScpItdMod.Pc {
    public static class MonitorFx {
        private static readonly Ramp Glass = new Ramp((0.0f, 0xBEDDF5), (1.0f, 0xFFFFFF));
        private static readonly Ramp Edge = new Ramp((0.0f, 0x000000), (1.0f, 0x102030));

        public static void Paint(ICanvas canvas, in Box screen) {
            canvas.PushClip(screen);
            for (int y = 1; y < screen.H; y += 4) {
                canvas.Fill(new Box(screen.X, screen.Y + y, screen.W, 1), new Rgba(0, 0, 0, 22));
            }
            canvas.Shade(screen.TakeTop(screen.H * 0.22f), Glass, true, 0.045f);
            const int edge = 8;
            for (int i = 0; i < edge; i++) {
                Rgba tint = new Rgba(0, 0, 0, (edge - i) * 2);
                Ui.Paint.Frame(canvas, screen.Inset(i), 1, tint);
            }
            canvas.Shade(screen.TakeBottom(2), Edge, true, 0.18f);
            canvas.PopClip();
        }
    }
}
