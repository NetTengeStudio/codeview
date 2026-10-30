using System;
using ScpItdMod.Ui;

namespace ScpItdMod.Pc {
    public sealed class ScaledCanvas : IMotionCanvas, IImageCanvas {
        public BatchCanvas Inner;
        public float K = 1;
        public float Ox;
        public float Oy;

        public float Scale => 1;

        public Box ToScreen(in Box b) => new Box(Ox + b.X * K, Oy + b.Y * K, b.W * K, b.H * K);

        public void Fill(in Box box, Rgba color) => Inner.Fill(ToScreen(box), color);

        public void Shade(in Box box, Ramp ramp, bool vertical, float alpha) => Inner.Shade(ToScreen(box), ramp, vertical, alpha);

        public void Glyph(IFace face, int codepoint, float x, float y, Rgba color) {
            Inner.Glyph(face, codepoint, MathF.Round(Ox + x * K), MathF.Round(Oy + y * K), color);
        }

        public void Art(PixelArt art, in Box box, Rgba tint) => Inner.Art(art, ToScreen(box), tint);

        public void Image(string key, in Box box, Rgba tint) => Inner.Image(key, ToScreen(box), tint);
        public bool Image(ICanvasImage image, in Box box, Rgba tint) => Inner.Image(image, ToScreen(box), tint);

        public void MotionImage(string key, in VisualPose pose, Rgba tint) {
            var screen = new VisualPose(ToScreen(pose.Bounds), pose.Angle, pose.Opacity, pose.Shadow * K, pose.Glow, pose.Depth, pose.Path, pose.PathScale * K);
            Inner.MotionImage(key, screen, tint);
        }

        public void PushClip(in Box box) => Inner.PushClip(ToScreen(box));

        public void PopClip() => Inner.PopClip();
    }
}
