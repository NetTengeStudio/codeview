using System;
using System.Runtime.CompilerServices;
using FarseerPhysics.Dynamics;

namespace ScpItdMod.Weather {
    internal static class SnowVisibility {
        private sealed class Combined {
            private readonly Predicate<Fixture> original;
            internal readonly Predicate<Fixture> Predicate;
            internal Combined(Predicate<Fixture> original) { this.original = original; Predicate = Accept; }
            private bool Accept(Fixture fixture) => Visible(fixture) && original(fixture);
        }
        private static readonly Predicate<Fixture> visible = Visible;
        private static readonly ConditionalWeakTable<Predicate<Fixture>, Combined> combined = new ConditionalWeakTable<Predicate<Fixture>, Combined>();
        private static readonly ConditionalWeakTable<Predicate<Fixture>, Combined>.CreateValueCallback create = Original => new Combined(Original);
        private static bool Visible(Fixture fixture) => !SnowSupportBodies.Owns(fixture?.Body);
        internal static void Filter(ref Predicate<Fixture> blocksVisibilityPredicate) {
            if (blocksVisibilityPredicate == null) blocksVisibilityPredicate = visible;
            else if (!ReferenceEquals(blocksVisibilityPredicate, visible)) blocksVisibilityPredicate = combined.GetValue(blocksVisibilityPredicate, create).Predicate;
        }
    }
}
