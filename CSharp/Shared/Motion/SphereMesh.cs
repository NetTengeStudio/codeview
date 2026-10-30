using System;
using Vector2 = ScpItdMod.Core.Vec2;
using Vector3 = ScpItdMod.Motion.Vec3;

namespace ScpItdMod.Motion {
    public readonly struct SpherePoint {
        public readonly Vector3 Normal;
        public readonly Vector2 Uv;
        public SpherePoint(Vector3 normal, Vector2 uv) { Normal = normal; Uv = uv; }
    }
    public sealed class SphereMesh {
        public readonly SpherePoint[] Points;
        public readonly short[] Indices;
        public SphereMesh(int segments = 32, int rings = 16) {
            if (segments < 8 || rings < 4 || (long)(segments + 1) * (rings + 1) > short.MaxValue) { throw new ArgumentOutOfRangeException(nameof(segments)); }
            Points = new SpherePoint[(segments + 1) * (rings + 1)]; Indices = new short[segments * (rings - 1) * 6];
            for (int y = 0; y <= rings; y++) {
                float latitude = y * MathF.PI / rings, sine = MathF.Sin(latitude), cosine = MathF.Cos(latitude);
                for (int x = 0; x <= segments; x++) {
                    float longitude = x * MathF.Tau / segments;
                    Points[y * (segments + 1) + x] = new SpherePoint(new Vector3(sine * MathF.Cos(longitude), cosine, sine * MathF.Sin(longitude)), new Vector2(x / (float)segments, y / (float)rings));
                }
            }
            int at = 0;
            for (int y = 0; y < rings; y++) {
                for (int x = 0; x < segments; x++) {
                    short a = (short)(y * (segments + 1) + x), b = (short)(a + segments + 1), c = (short)(a + 1), d = (short)(b + 1);
                    if (y > 0) { Indices[at++] = a; Indices[at++] = c; Indices[at++] = b; }
                    if (y < rings - 1) { Indices[at++] = c; Indices[at++] = d; Indices[at++] = b; }
                }
            }
        }
    }
}
