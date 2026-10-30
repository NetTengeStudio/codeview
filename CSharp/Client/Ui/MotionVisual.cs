using System;
using Vector2 = ScpItdMod.Core.Vec2;
using ScpItdMod.Motion;

namespace ScpItdMod.Ui {
    public readonly struct VisualPose {
        public readonly Box Bounds;
        public readonly float Angle, Opacity, Shadow, Glow, Depth;
        public readonly IMotionPath2D Path;
        public readonly float PathScale;
        public VisualPose(in Box bounds, float angle = 0, float opacity = 1, float shadow = 0, float glow = 0, float depth = 0, IMotionPath2D path = null, float pathScale = 1) {
            Bounds = bounds; Angle = angle; Opacity = Math.Clamp(opacity, 0, 1); Shadow = MathF.Max(0, shadow); Glow = MathF.Max(0, glow); Depth = depth;
            Path = path; PathScale = pathScale;
        }
        public bool Contains(float x, float y) {
            float dx = x - Bounds.CenterX, dy = y - Bounds.CenterY, cosine = MathF.Cos(Angle), sine = MathF.Sin(Angle);
            float localX = dx * cosine + dy * sine, localY = -dx * sine + dy * cosine;
            return Opacity > 0.01f && MathF.Abs(localX) <= Bounds.W * 0.5f && MathF.Abs(localY) <= Bounds.H * 0.5f;
        }
    }
    public sealed class MotionVisual : IDisposable, IMotionPath2D {
        private readonly MotionScope scope;
        public readonly MotionHandle<Vector2> Position, Size, Offset;
        public readonly MotionHandle<float> Rotation, Scale, Opacity, Lift, Press, Shadow, Glow, Depth;
        public bool Alive => scope.Alive;
        public MotionVisual(MotionRuntime runtime, in Box bounds, bool hidden = false) {
            scope = runtime.Scope();
            Position = scope.Vector(new Vector2(bounds.CenterX, bounds.CenterY)).Track(); Offset = scope.Vector().Track(); Size = scope.Vector(new Vector2(bounds.W, bounds.H)).Track();
            Rotation = scope.Angle().Track(); Scale = scope.Scalar(1).Track(); Opacity = scope.Scalar(hidden ? 0 : 1).Track();
            Lift = scope.Scalar().Track(); Press = scope.Scalar(1).Track(); Shadow = scope.Scalar(); Glow = scope.Scalar(); Depth = scope.Scalar();
        }
        public void Place(in Box target, MotionTransition transition, bool direct = false) {
            var position = new Vector2(target.CenterX, target.CenterY); var size = new Vector2(target.W, target.H);
            if (direct) { Position.Snap(position); Size.Snap(size); }
            else { Position.To(position, transition); Size.To(size, transition); }
        }
        public void Interact(bool hover, bool selected, bool down) {
            Lift.To(selected ? -12 : hover ? -6 : 0, MotionPresets.Hover);
            Press.To(down ? 0.96f : 1, MotionPresets.Hover);
            Shadow.To(selected ? 7 : hover ? 4 : 2, MotionPresets.Hover);
            Glow.To(selected ? 1 : hover ? 0.35f : 0, MotionPresets.Hover);
            Depth.To(selected ? 2 : hover ? 1 : 0, MotionPresets.Hover);
        }
        public VisualPose Pose {
            get {
                MotionPose2D pose = Sample(0); Vector2 position = pose.Position, size = pose.Size;
                return new VisualPose(new Box(position.X - size.X * 0.5f, position.Y - size.Y * 0.5f, size.X, size.Y), pose.Angle, pose.Opacity, Shadow.Value, Glow.Value, Depth.Value, this);
            }
        }
        public MotionPose2D Sample(double secondsAgo) {
            Vector2 position = Position.Sample(secondsAgo) + Offset.Sample(secondsAgo), size = Size.Sample(secondsAgo);
            float scale = MathF.Max(.001f, Scale.Sample(secondsAgo) * Press.Sample(secondsAgo));
            return new MotionPose2D(new Vector2(position.X, position.Y + Lift.Sample(secondsAgo)), size * scale, Rotation.Sample(secondsAgo), Opacity.Sample(secondsAgo));
        }
        public MotionPlayback Play(MotionSequence sequence, float speed = 1, int repeats = 0) => scope.Play(sequence, speed, repeats);
        public void Dispose() => scope.Dispose();
    }
    public readonly struct VisualMotionPath : IMotionPath2D {
        private readonly IMotionPath2D path;
        private readonly Vector2 offset, extraSize;
        private readonly float scale, angle, opacity;
        public bool Alive => path?.Alive == true;
        public VisualMotionPath(in VisualPose pose) {
            path = pose.Path ?? throw new ArgumentException("A visual motion source is required.", nameof(pose)); MotionPose2D current = path.Sample(0); scale = pose.PathScale;
            offset = new Vector2(pose.Bounds.CenterX, pose.Bounds.CenterY) - current.Position * scale;
            extraSize = new Vector2(pose.Bounds.W, pose.Bounds.H) - current.Size * scale;
            angle = pose.Angle - current.Angle; opacity = current.Opacity > 0 ? pose.Opacity / current.Opacity : 0;
        }
        public MotionPose2D Sample(double secondsAgo) {
            MotionPose2D sample = path.Sample(secondsAgo);
            return new MotionPose2D(sample.Position * scale + offset, sample.Size * scale + extraSize, sample.Angle + angle, sample.Opacity * opacity);
        }
    }
    public interface IMotionCanvas : ICanvas {
        void MotionImage(string key, in VisualPose pose, Rgba tint);
    }
    public static class MotionPaint {
        public static void Image(ICanvas canvas, string key, in VisualPose pose, Rgba tint) {
            if (canvas is IMotionCanvas motion) { motion.MotionImage(key, pose, tint); }
            else { canvas.Image(key, pose.Bounds, tint.Fade(pose.Opacity)); }
        }
    }
    public sealed class OpacityCanvas : IMotionCanvas, IImageCanvas {
        public ICanvas Inner;
        public float Alpha = 1;
        public float Scale => Inner.Scale;
        public void Fill(in Box box, Rgba color) => Inner.Fill(box, color.Fade(Alpha));
        public void Shade(in Box box, Ramp ramp, bool vertical, float alpha) => Inner.Shade(box, ramp, vertical, alpha * Alpha);
        public void Glyph(IFace face, int codepoint, float x, float y, Rgba color) => Inner.Glyph(face, codepoint, x, y, color.Fade(Alpha));
        public void Art(PixelArt art, in Box box, Rgba tint) => Inner.Art(art, box, tint.Fade(Alpha));
        public void Image(string key, in Box box, Rgba tint) => Inner.Image(key, box, tint.Fade(Alpha));
        public bool Image(ICanvasImage image, in Box box, Rgba tint) => CanvasImages.Draw(Inner, image, box, tint.Fade(Alpha));
        public void PushClip(in Box box) => Inner.PushClip(box);
        public void PopClip() => Inner.PopClip();
        public void MotionImage(string key, in VisualPose pose, Rgba tint) => MotionPaint.Image(Inner, key, pose, tint.Fade(Alpha));
    }
}
