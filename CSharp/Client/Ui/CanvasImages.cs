namespace ScpItdMod.Ui {
    public interface ICanvasImage {
        int Width { get; }
        int Height { get; }
    }
    public interface IImageCanvas : ICanvas {
        bool Image(ICanvasImage image, in Box box, Rgba tint);
    }
    public static class CanvasImages {
        public static bool Draw(ICanvas canvas, ICanvasImage image, in Box box, Rgba tint) =>
            canvas is IImageCanvas images && images.Image(image, box, tint);
        public static Box Fit(ICanvasImage image, in Box box) {
            float scale = System.MathF.Min(box.W / image.Width, box.H / image.Height);
            float width = image.Width * scale, height = image.Height * scale;
            return new Box(box.CenterX - width * 0.5f, box.CenterY - height * 0.5f, width, height);
        }
    }
}
