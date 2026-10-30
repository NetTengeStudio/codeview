using System;
using System.Collections.Generic;
using ScpItdMod.Core;
using ScpItdMod.Motion;

namespace ScpItdMod.Inv {
    // Every Motion handle of one open container deck, created together so the deck has a single
    // owner scope: closing the deck disposes the scope and nothing outlives it.
    internal sealed class DeckMotion : IDisposable {
        public const int Cells = OrbitDeck.MaxCells, Tabs = OrbitDeck.MaxChips;

        private readonly MotionScope scope;

        // Queued handles bloom to 1 on their own offsets. Each Play starts its own timeline over the handles queued
        // for it; timelines already running are never interrupted by a later one, so a bloom in flight finishes
        // instead of snapping back. Cancel is the only way to stop them all, used when the whole deck is reset.
        public sealed class Wave {
            private readonly MotionScope scope;
            private readonly List<(MotionHandle<float> Handle, double Delay)> queue = new List<(MotionHandle<float>, double)>(Cells);
            private readonly List<MotionPlayback> playbacks = new List<MotionPlayback>(4);

            internal Wave(MotionScope scope) { this.scope = scope; }

            public bool Active {
                get {
                    for (int i = 0; i < playbacks.Count; i++) { if (playbacks[i].Active) { return true; } }
                    return false;
                }
            }

            public void Queue(MotionHandle<float> handle, double delay) => queue.Add((handle, delay));

            public void Cancel() {
                for (int i = 0; i < playbacks.Count; i++) { playbacks[i].Cancel(); }
                playbacks.Clear(); queue.Clear();
            }

            public void Play(MotionTransition transition, double maximumDelay = 0.14) {
                if (queue.Count == 0) { return; }
                if (!double.IsFinite(maximumDelay) || maximumDelay < 0) { throw new ArgumentOutOfRangeException(nameof(maximumDelay)); }
                if (scope.Runtime.ReducedMotion) {
                    for (int i = 0; i < queue.Count; i++) { queue[i].Handle.Snap(1); }
                    queue.Clear(); return;
                }
                var timeline = new MotionSequenceBuilder();
                double last = 0;
                for (int i = 0; i < queue.Count; i++) { last = Math.Max(last, queue[i].Delay); }
                double scale = last > maximumDelay ? maximumDelay / last : 1;
                for (int i = 0; i < queue.Count; i++) { timeline.To(queue[i].Handle, 1f, transition, queue[i].Delay * scale); }
                queue.Clear();
                playbacks.RemoveAll(p => !p.Active);
                playbacks.Add(scope.Play(timeline.Compile()));
            }
        }

        public readonly Wave TabWave, ItemWave;
        public readonly MotionHandle<float> Reveal, PillAngle, PillOn, PillBump, DotAngle, DotBump;
        public readonly MotionHandle<float>[] TabBloom = new MotionHandle<float>[Tabs], TabHover = new MotionHandle<float>[Tabs], TabAngle = new MotionHandle<float>[Tabs];
        public readonly MotionHandle<float>[] Bloom = new MotionHandle<float>[Cells], Hover = new MotionHandle<float>[Cells], Press = new MotionHandle<float>[Cells], CellAngle = new MotionHandle<float>[Cells];
        public readonly MotionHandle<Vec2>[] Shift = new MotionHandle<Vec2>[Cells];
        public readonly bool[] TabPlaced = new bool[Tabs], CellPlaced = new bool[Cells];
        public bool PillPlaced, DotPlaced;

        public bool Alive => scope.Alive;

        public DeckMotion(MotionRuntime runtime) {
            scope = runtime.Scope();
            TabWave = new Wave(scope); ItemWave = new Wave(scope);
            Reveal = scope.Scalar().Track(); PillOn = scope.Scalar(); PillBump = scope.Scalar(); DotBump = scope.Scalar();
            PillAngle = scope.Angle(); DotAngle = scope.Angle();
            for (int i = 0; i < Tabs; i++) { TabBloom[i] = scope.Scalar(); TabHover[i] = scope.Scalar(); TabAngle[i] = scope.Angle(); }
            for (int i = 0; i < Cells; i++) {
                Bloom[i] = scope.Scalar().Track(); Hover[i] = scope.Scalar().Track(); Press[i] = scope.Scalar(1).Track(); CellAngle[i] = scope.Angle().Track(); Shift[i] = scope.Vector().Track();
            }
        }

        public void ResetPlacement() {
            Array.Clear(TabPlaced, 0, TabPlaced.Length); Array.Clear(CellPlaced, 0, CellPlaced.Length);
            PillPlaced = DotPlaced = false;
        }

        public void Dispose() { TabWave.Cancel(); ItemWave.Cancel(); scope.Dispose(); }
    }
}
