using System;
using System.Collections.Generic;

namespace ScpItdMod.Rec {
    public readonly struct ShadowEdge : IEquatable<ShadowEdge> {
        public readonly float AX, AY, BX, BY;
        public ShadowEdge(float ax, float ay, float bx, float by) { AX=ax; AY=ay; BX=bx; BY=by; }
        public bool Valid => float.IsFinite(AX) && float.IsFinite(AY) && float.IsFinite(BX) && float.IsFinite(BY);
        public bool Equals(ShadowEdge other) => AX==other.AX && AY==other.AY && BX==other.BX && BY==other.BY;
    }
    public sealed class SceneShadow : IEquatable<SceneShadow> {
        private readonly ShadowEdge[] edges;
        public int Count => edges.Length;
        public ReadOnlySpan<ShadowEdge> Edges => edges;
        private SceneShadow(ShadowEdge[] owned) { edges=owned; }
        internal static SceneShadow Adopt(ShadowEdge[] owned) => new SceneShadow(owned);
        public SceneShadow(IReadOnlyList<ShadowEdge> source) {
            if (source.Count > ushort.MaxValue) { throw new ArgumentOutOfRangeException(nameof(source)); }
            edges = new ShadowEdge[source.Count];
            for (int i=0; i<edges.Length; i++) {
                if (!source[i].Valid) { throw new ArgumentException("Nonfinite occluder"); }
                edges[i]=source[i];
            }
        }
        public bool Matches(IReadOnlyList<ShadowEdge> source) {
            if (source.Count != edges.Length) { return false; }
            for (int i=0; i<edges.Length; i++) { if (!edges[i].Equals(source[i])) { return false; } }
            return true;
        }
        public bool Equals(SceneShadow other) => ReferenceEquals(this,other) || other != null && edges.AsSpan().SequenceEqual(other.edges);
        public static bool Project(in ShadowEdge edge, float x, float y, float reach, out ShadowEdge far) {
            float ax=edge.AX-x, ay=edge.AY-y, bx=edge.BX-x, by=edge.BY-y;
            float a=MathF.Sqrt(ax*ax+ay*ay), b=MathF.Sqrt(bx*bx+by*by);
            if (a <= float.Epsilon || b <= float.Epsilon) { far=default; return false; }
            far=new ShadowEdge(edge.AX+ax/a*reach,edge.AY+ay/a*reach,edge.BX+bx/b*reach,edge.BY+by/b*reach);
            return far.Valid;
        }
    }
}
