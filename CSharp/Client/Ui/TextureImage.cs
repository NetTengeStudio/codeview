using Microsoft.Xna.Framework.Graphics;

namespace ScpItdMod.Ui {
    public sealed class TextureImage : ICanvasImage {
        public Texture2D Texture { get; }
        public int Width => Texture.Width;
        public int Height => Texture.Height;
        public TextureImage(Texture2D texture) => Texture = texture ?? throw new System.ArgumentNullException(nameof(texture));
    }
}
