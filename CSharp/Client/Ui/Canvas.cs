using System;
using System.Runtime.CompilerServices;

namespace ScpItdMod.Ui {
    public readonly struct Rgba : IEquatable<Rgba> {
        public readonly uint Packed;

        public Rgba(uint packed) => Packed = packed;

        public Rgba(int r, int g, int b, int a = 255) =>
            Packed = (uint)(Clamp(r) | Clamp(g) << 8 | Clamp(b) << 16 | Clamp(a) << 24);

        public byte R => (byte)Packed;
        public byte G => (byte)(Packed >> 8);
        public byte B => (byte)(Packed >> 16);
        public byte A => (byte)(Packed >> 24);

        public static Rgba Hex(uint rgb) => new Rgba((int)(rgb >> 16) & 255, (int)(rgb >> 8) & 255, (int)rgb & 255);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Rgba Fade(float alpha) => new Rgba(Packed & 0x00FFFFFFu | (uint)Clamp((int)(A * alpha + 0.5f)) << 24);

        public Rgba WithAlpha(int alpha) => new Rgba(Packed & 0x00FFFFFFu | (uint)Clamp(alpha) << 24);

        public static Rgba Mix(Rgba a, Rgba b, float t) => new Rgba(
            (int)(a.R + (b.R - a.R) * t + 0.5f), (int)(a.G + (b.G - a.G) * t + 0.5f),
            (int)(a.B + (b.B - a.B) * t + 0.5f), (int)(a.A + (b.A - a.A) * t + 0.5f));

        public bool Equals(Rgba other) => Packed == other.Packed;

        public override bool Equals(object obj) => obj is Rgba other && other.Packed == Packed;

        public override int GetHashCode() => (int)Packed;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;
    }

    public struct Box {
        public float X;
        public float Y;
        public float W;
        public float H;

        public Box(float x, float y, float w, float h) {
            X = x;
            Y = y;
            W = w < 0.0f ? 0.0f : w;
            H = h < 0.0f ? 0.0f : h;
        }

        public float Right => X + W;
        public float Bottom => Y + H;
        public float CenterX => X + W * 0.5f;
        public float CenterY => Y + H * 0.5f;
        public bool Empty => W <= 0.0f || H <= 0.0f;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(float x, float y) => x >= X && y >= Y && x < X + W && y < Y + H;

        public Box Inset(float d) => new Box(X + d, Y + d, W - d * 2.0f, H - d * 2.0f);

        public Box Inset(float left, float top, float right, float bottom) => new Box(X + left, Y + top, W - left - right, H - top - bottom);

        public Box Offset(float dx, float dy) => new Box(X + dx, Y + dy, W, H);

        public Box TakeTop(float h) => new Box(X, Y, W, Math.Min(h, H));

        public Box TakeBottom(float h) => new Box(X, Bottom - Math.Min(h, H), W, Math.Min(h, H));

        public Box TakeLeft(float w) => new Box(X, Y, Math.Min(w, W), H);

        public Box TakeRight(float w) => new Box(Right - Math.Min(w, W), Y, Math.Min(w, W), H);

        public Box CutTop(float h) => new Box(X, Y + h, W, H - h);

        public Box CutBottom(float h) => new Box(X, Y, W, H - h);

        public Box CutLeft(float w) => new Box(X + w, Y, W - w, H);

        public Box CutRight(float w) => new Box(X, Y, W - w, H);

        public static Box Intersect(in Box a, in Box b) {
            float x = Math.Max(a.X, b.X);
            float y = Math.Max(a.Y, b.Y);
            return new Box(x, y, Math.Min(a.Right, b.Right) - x, Math.Min(a.Bottom, b.Bottom) - y);
        }

        public Box Snap() {
            float x = MathF.Round(X);
            float y = MathF.Round(Y);
            return new Box(x, y, MathF.Round(X + W) - x, MathF.Round(Y + H) - y);
        }
    }

    public sealed class Ramp {
        private static int nextId;

        public readonly float[] At;
        public readonly Rgba[] Colors;
        public readonly int Id;

        public Ramp(params (float at, uint rgb)[] stops) {
            if (stops == null || stops.Length == 0) { throw new ArgumentException("a ramp needs stops"); }
            At = new float[stops.Length];
            Colors = new Rgba[stops.Length];
            for (int i = 0; i < stops.Length; i++) {
                if (i > 0 && stops[i].at < stops[i - 1].at) { throw new ArgumentException("ramp stops must ascend"); }
                At[i] = stops[i].at;
                Colors[i] = Rgba.Hex(stops[i].rgb);
            }
            Id = System.Threading.Interlocked.Increment(ref nextId);
        }

        public Rgba Sample(float t) {
            if (t <= At[0]) { return Colors[0]; }
            int last = At.Length - 1;
            if (t >= At[last]) { return Colors[last]; }
            int i = 1;
            while (At[i] < t) { i++; }
            float span = At[i] - At[i - 1];
            return span <= 0.0f ? Colors[i] : Rgba.Mix(Colors[i - 1], Colors[i], (t - At[i - 1]) / span);
        }
    }

    public sealed class PixelArt {
        private static int nextId;

        public readonly int Width;
        public readonly int Height;
        public readonly uint[] Pixels;
        public readonly int Id;
        public readonly string Name;

        public PixelArt(string name, int width, int height, uint[] pixels) {
            if (width <= 0 || height <= 0 || pixels == null || pixels.Length != width * height) { throw new ArgumentException("pixel art size"); }
            Name = name;
            Width = width;
            Height = height;
            Pixels = pixels;
            Id = System.Threading.Interlocked.Increment(ref nextId);
        }
    }

    public interface IFace {
        string Key { get; }

        float Size { get; }

        float LineHeight { get; }

        float Advance(int codepoint);
    }

    public interface ICanvas {
        float Scale { get; }

        void Fill(in Box box, Rgba color);

        void Shade(in Box box, Ramp ramp, bool vertical, float alpha);

        void Glyph(IFace face, int codepoint, float x, float y, Rgba color);

        void Art(PixelArt art, in Box box, Rgba tint);

        void Image(string key, in Box box, Rgba tint);

        void PushClip(in Box box);

        void PopClip();
    }

    public static class Paint {
        public static void Frame(ICanvas c, in Box b, float t, Rgba color) {
            if (t <= 0.0f || b.Empty) { return; }
            c.Fill(new Box(b.X, b.Y, b.W, t), color);
            c.Fill(new Box(b.X, b.Bottom - t, b.W, t), color);
            c.Fill(new Box(b.X, b.Y + t, t, b.H - t * 2.0f), color);
            c.Fill(new Box(b.Right - t, b.Y + t, t, b.H - t * 2.0f), color);
        }

        public static void Bevel(ICanvas c, in Box b, float t, Rgba light, Rgba dark) {
            c.Fill(new Box(b.X, b.Y, b.W - t, t), light);
            c.Fill(new Box(b.X, b.Y + t, t, b.H - t * 2.0f), light);
            c.Fill(new Box(b.X, b.Bottom - t, b.W, t), dark);
            c.Fill(new Box(b.Right - t, b.Y, t, b.H - t), dark);
        }

        public static float Measure(IFace face, ReadOnlySpan<char> text) {
            float w = 0.0f;
            for (int i = 0; i < text.Length; i++) {
                int cp = Next(text, ref i);
                w += face.Advance(cp);
            }
            return w;
        }

        public static float Text(ICanvas c, IFace face, ReadOnlySpan<char> text, float x, float y, Rgba color) {
            float pen = x;
            for (int i = 0; i < text.Length; i++) {
                int cp = Next(text, ref i);
                if (cp != ' ') { c.Glyph(face, cp, pen, y, color); }
                pen += face.Advance(cp);
            }
            return pen - x;
        }

        public static void Text(ICanvas c, IFace face, ReadOnlySpan<char> text, in Box box, Rgba color, Align align) {
            float width = Measure(face, text);
            float x = align switch {
                Align.Center => box.X + (box.W - width) * 0.5f,
                Align.Right => box.Right - width,
                _ => box.X
            };
            float y = box.Y + (box.H - face.LineHeight) * 0.5f;
            Text(c, face, text, MathF.Round(x), MathF.Round(y), color);
        }

        public static int Fit(IFace face, ReadOnlySpan<char> text, float width) {
            float pen = 0.0f;
            for (int i = 0; i < text.Length; i++) {
                int start = i;
                int cp = Next(text, ref i);
                pen += face.Advance(cp);
                if (pen > width) { return start; }
            }
            return text.Length;
        }

        public static void Clipped(ICanvas c, IFace face, string text, in Box box, Rgba color, Align align) {
            if (string.IsNullOrEmpty(text)) { return; }
            ReadOnlySpan<char> span = text;
            if (Measure(face, span) <= box.W) {
                Text(c, face, span, box, color, align);
                return;
            }
            float dots = face.Advance('.') * 3.0f;
            int n = Fit(face, span, Math.Max(0.0f, box.W - dots));
            float y = box.Y + (box.H - face.LineHeight) * 0.5f;
            float pen = Text(c, face, span.Slice(0, n), MathF.Round(box.X), MathF.Round(y), color);
            Text(c, face, "...", MathF.Round(box.X + pen), MathF.Round(y), color);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Next(ReadOnlySpan<char> text, ref int i) {
            char ch = text[i];
            if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) {
                i++;
                return char.ConvertToUtf32(ch, text[i]);
            }
            return ch;
        }
    }

    public enum Align : byte { Left, Center, Right }
}
