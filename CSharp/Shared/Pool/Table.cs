using System;
using System.Globalization;
using Vector2 = ScpItdMod.Core.Vec2;
using System.Xml.Linq;
using ScpItdMod.Core;

namespace ScpItdMod.Pool {
    public readonly struct Rail {
        public readonly Vector2 A, B, Normal, Axis;
        public readonly float Length;
        public Rail(Vector2 a, Vector2 b, Vector2 normal) { A = a; B = b; Normal = normal; Length = Vector2.Distance(a, b); if (Length <= 0) { throw new FormatException("Empty rail"); } Axis = (b - a) / Length; }
    }

    public sealed class Table {
        public readonly string UiArt, WorldArt;
        public readonly float Length, Width, Radius, Mass, PixelsPerMeter, PocketRadius;
        public readonly float Slide, Roll, Spin, Restitution, Cushion, BallFriction, RailFriction;
        public readonly Vector2 Cloth, Image, UiImage, UiCloth, UiScale;
        public readonly Vector2 WorldImage, WorldOrigin, WorldLift, WorldBase, WorldX, WorldY;
        public readonly Plane WorldPlane;
        public readonly float WorldPixelsPerMeter, CueLength;
        public readonly Vector2 CueTip, CueButt;
        public readonly Rail[] Rails;
        public readonly Vector2[] Pockets;

        private Table(XElement e) {
            UiArt = Art(e, "art", "Content/Items/Pool/table.png");
            WorldArt = Art(e, "worldart", "Content/Items/Pool/world.png");
            float[] cloth = Numbers(e, "cloth", 4), image = Numbers(e, "image", 2);
            Length = Number(e, "length"); Radius = Number(e, "radius"); Mass = Number(e, "mass");
            if (Length <= Radius * 20 || Radius <= 0 || Mass <= 0 || cloth[2] <= 0 || cloth[3] <= 0 || image[0] <= 0 || image[1] <= 0 || cloth[0] < 0 || cloth[1] < 0 || cloth[0] + cloth[2] > image[0] || cloth[1] + cloth[3] > image[1]) { throw new FormatException("Invalid pool dimensions"); }
            PixelsPerMeter = cloth[2] / Length; Width = cloth[3] / PixelsPerMeter;
            Cloth = new Vector2(cloth[0], cloth[1]); Image = new Vector2(image[0], image[1]);
            float[] uiImage = e.Attribute("uiimage") == null ? image : Numbers(e, "uiimage", 2);
            float[] uiCloth = e.Attribute("uicloth") == null ? cloth : Numbers(e, "uicloth", 4);
            if (uiImage[0] <= 0 || uiImage[1] <= 0 || uiCloth[0] < 0 || uiCloth[1] < 0 || uiCloth[2] <= 0 || uiCloth[3] <= 0 ||
                uiCloth[0] + uiCloth[2] > uiImage[0] || uiCloth[1] + uiCloth[3] > uiImage[1]) { throw new FormatException("Invalid pool UI calibration"); }
            UiImage = new Vector2(uiImage[0], uiImage[1]); UiCloth = new Vector2(uiCloth[0], uiCloth[1]);
            UiScale = new Vector2(uiCloth[2] / Length, uiCloth[3] / Width);
            float[] worldImage = Numbers(e, "worldimage", 2), worldOrigin = Numbers(e, "worldorigin", 2), worldPlane = Numbers(e, "worldplane", 6), lift = Numbers(e, "worldlift", 2);
            WorldImage = new Vector2(worldImage[0], worldImage[1]); WorldOrigin = new Vector2(worldOrigin[0], worldOrigin[1]); WorldLift = new Vector2(lift[0], lift[1]);
            Vector2 basePoint = new Vector2(worldPlane[0], worldPlane[1]), axisX = new Vector2(worldPlane[2], worldPlane[3]) - basePoint, axisY = new Vector2(worldPlane[4], worldPlane[5]) - basePoint;
            if (WorldImage.X <= 0 || WorldImage.Y <= 0 || WorldOrigin.X < 0 || WorldOrigin.Y < 0 || WorldOrigin.X > WorldImage.X || WorldOrigin.Y > WorldImage.Y) { throw new FormatException("Invalid world table image"); }
            WorldPlane = new Plane(basePoint, axisX, axisY); WorldX = axisX / Length; WorldY = axisY / Width; WorldBase = basePoint - WorldImage / 2 - WorldLift;
            WorldPixelsPerMeter = axisX.Length() / Length; CueLength = Number(e, "cuelength");
            if (CueLength <= 0) { throw new FormatException("Invalid cue length"); }
            float[] tip = Numbers(e, "cuetip", 2), butt = Numbers(e, "cuebutt", 2);
            CueTip = new Vector2(tip[0], tip[1]); CueButt = new Vector2(butt[0], butt[1]);
            if (CueButt.X <= CueTip.X || MathF.Abs(CueButt.Y - CueTip.Y) > 0.0001f) { throw new FormatException("Cue image axis must run from tip to butt along positive X"); }
            PocketRadius = Number(e, "pocketradius") / PixelsPerMeter;
            Slide = Number(e, "slide"); Roll = Number(e, "roll"); Spin = Number(e, "spin");
            Restitution = Number(e, "restitution"); Cushion = Number(e, "cushion");
            BallFriction = Number(e, "ballfriction"); RailFriction = Number(e, "railfriction");
            if (Slide <= 0 || Roll <= 0 || Spin <= 0 || Restitution < 0 || Restitution > 1 || Cushion < 0 || Cushion > 1 || BallFriction < 0 || RailFriction < 0) { throw new FormatException("Invalid pool material"); }
            Pockets = new Vector2[6]; int i = 0;
            foreach (XElement p in e.Elements("Pocket")) {
                if (i == 6) { throw new FormatException("Too many pockets"); }
                Pockets[i++] = (new Vector2(Number(p, "x"), Number(p, "y")) - Cloth) / PixelsPerMeter;
            }
            if (i != 6 || PocketRadius <= 0) { throw new FormatException("Six pockets required"); }
            Rails = new Rail[6]; i = 0;
            foreach (XElement rail in e.Elements("Rail")) {
                if (i == Rails.Length) { throw new FormatException("Too many rails"); }
                float[] a = Numbers(rail, "a", 2), b = Numbers(rail, "b", 2), normal = Numbers(rail, "normal", 2);
                var segment = new Rail(new Vector2(a[0], a[1]), new Vector2(b[0], b[1]), new Vector2(normal[0], normal[1]));
                if (MathF.Abs(segment.Normal.LengthSquared() - 1) > 1e-5f || MathF.Abs(Vector2.Dot(segment.Normal, segment.Axis)) > 1e-5f) { throw new FormatException("Invalid rail normal"); }
                Rails[i++] = segment;
            }
            if (i != Rails.Length) { throw new FormatException("Six rails required"); }
        }

        public static Table Parse(string xml) => new Table(XElement.Parse(xml));
        public Vector2 UiPixel(Vector2 position) => UiCloth + new Vector2(position.X * UiScale.X, position.Y * UiScale.Y);
        public Vector2 UiPosition(Vector2 pixel) => new Vector2((pixel.X - UiCloth.X) / UiScale.X, (pixel.Y - UiCloth.Y) / UiScale.Y);
        public Vector2 Pixel(Vector2 position) => Cloth + position * PixelsPerMeter;
        public Vector2 Position(Vector2 pixel) => (pixel - Cloth) / PixelsPerMeter;
        private static string Art(XElement element, string key, string fallback) {
            string path = ((string)element.Attribute(key) ?? fallback).Replace('\\', '/');
            if (!path.StartsWith("Content/", StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal) || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) { throw new FormatException("Pool art must be an explicit PNG path inside Content."); }
            return path;
        }
        private static float Number(XElement e, string name) {
            if (!float.TryParse((string)e.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out float n) || !float.IsFinite(n)) { throw new FormatException("Invalid pool " + name); }
            return n;
        }
        private static float[] Numbers(XElement e, string name, int count) {
            string[] values = ((string)e.Attribute(name) ?? "").Split(',');
            if (values.Length != count) { throw new FormatException("Invalid pool " + name); }
            var result = new float[count];
            for (int i = 0; i < count; i++) {
                if (!float.TryParse(values[i], NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]) || !float.IsFinite(result[i])) { throw new FormatException("Invalid pool " + name); }
            }
            return result;
        }
    }
}
