using System;
using System.Collections.Generic;

namespace ScpItdMod.Motion {
    internal abstract class MotionCommand {
        internal abstract uint Apply(float speed);
        internal abstract void Cancel(uint writer, bool complete);
        internal abstract void Validate(MotionScope scope, float speed);
        internal abstract bool Running(uint writer);
    }
    internal sealed class MotionTarget<T> : MotionCommand where T : struct, IEquatable<T> {
        private readonly MotionHandle<T> property;
        private readonly T target;
        private readonly MotionTransition transition;
        internal MotionTarget(MotionHandle<T> property, T target, MotionTransition transition) { this.property = property; this.target = target; this.transition = transition; }
        internal override void Validate(MotionScope scope, float speed) { property.RequireOwner(scope); property.Channel.ValidateValue(target); if (!transition.AtSpeed(speed).Valid) { throw new ArgumentException("Invalid sequence transition."); } }
        internal override uint Apply(float speed) => property.Channel.Target(property.Index, property.Generation, target, transition.AtSpeed(speed), true) ? property.Channel.Writer(property.Index) : 0;
        internal override bool Running(uint writer) => writer != 0 && property.Alive && property.Channel.Writer(property.Index) == writer && property.Running;
        internal override void Cancel(uint writer, bool complete) {
            if (writer == 0 || !property.Alive || property.Channel.Writer(property.Index) != writer) { return; }
            if (complete) { property.Cancel(true); }
            else { property.Channel.CancelLease(property.Index, property.Generation, writer); }
        }
    }
    internal sealed class MotionMarker : MotionCommand {
        private readonly Action marker;
        internal MotionMarker(Action marker) => this.marker = marker ?? throw new ArgumentNullException(nameof(marker));
        internal override void Validate(MotionScope scope, float speed) { }
        internal override uint Apply(float speed) { marker(); return 0; }
        internal override void Cancel(uint writer, bool complete) { }
        internal override bool Running(uint writer) => false;
    }
    internal readonly struct MotionCue {
        internal readonly double At;
        internal readonly MotionCommand Command;
        internal MotionCue(double at, MotionCommand command) { At = at; Command = command; }
    }
    public sealed class MotionSequence {
        internal readonly MotionCue[] Cues;
        public double Duration { get; }
        internal MotionSequence(MotionCue[] cues, double duration) { Cues = cues; Duration = duration; }
        internal void Validate(MotionScope owner, float speed) {
            for (int i = 0; i < Cues.Length; i++) { Cues[i].Command.Validate(owner, speed); }
        }
    }
    public sealed class MotionSequenceBuilder {
        private readonly List<MotionCue> cues = new List<MotionCue>();
        public double Duration { get; private set; }
        public MotionSequenceBuilder To<T>(MotionHandle<T> property, T target, MotionTransition transition, double at = 0, double? span = null) where T : struct, IEquatable<T> {
            double duration = span ?? (transition.Kind == MotionKind.Spring ? 8 / (transition.Omega * Math.Max(0.1, transition.Damping)) : transition.Duration);
            Check(at); Check(duration);
            if (!transition.Valid || duration <= 0) { throw new ArgumentException("A transition and a positive sequence span are required."); }
            cues.Add(new MotionCue(at, new MotionTarget<T>(property, target, transition))); Duration = Math.Max(Duration, at + duration); return this;
        }
        public MotionSequenceBuilder Marker(double at, Action marker) { Check(at); cues.Add(new MotionCue(at, new MotionMarker(marker))); Duration = Math.Max(Duration, at); return this; }
        public MotionSequenceBuilder Wait(double seconds) { Check(seconds); Duration += seconds; return this; }
        public MotionSequenceBuilder Insert(MotionSequence sequence, double at) {
            if (sequence == null) { throw new ArgumentNullException(nameof(sequence)); }
            Check(at);
            for (int i = 0; i < sequence.Cues.Length; i++) { cues.Add(new MotionCue(at + sequence.Cues[i].At, sequence.Cues[i].Command)); }
            Duration = Math.Max(Duration, at + sequence.Duration); return this;
        }
        public MotionSequenceBuilder Append(MotionSequence sequence) => Insert(sequence, Duration);
        public MotionSequence Compile() {
            if (!double.IsFinite(Duration) || Duration <= 0) { throw new InvalidOperationException("Sequence must have a finite positive duration."); }
            MotionCue[] plan = cues.ToArray();
            for (int i = 1; i < plan.Length; i++) {
                MotionCue cue = plan[i]; int j = i;
                while (j > 0 && plan[j - 1].At > cue.At) { plan[j] = plan[j - 1]; j--; }
                plan[j] = cue;
            }
            return new MotionSequence(plan, Duration);
        }
        private static void Check(double seconds) { if (!double.IsFinite(seconds) || seconds < 0) { throw new ArgumentOutOfRangeException(nameof(seconds)); } }
    }
    public sealed class MotionPlayback {
        internal readonly MotionScope Owner;
        private MotionSequence sequence;
        private readonly double started;
        private readonly float speed;
        private readonly int repeats;
        private readonly uint[] writers;
        private int cursor, cycle;
        private bool endReached;
        private Action completed;
        public bool Active { get; private set; } = true;
        public bool Finished { get; private set; }
        internal MotionPlayback(MotionScope owner, MotionSequence sequence, double started, float speed, int repeats) {
            Owner = owner; this.sequence = sequence; this.started = started; this.speed = speed; this.repeats = repeats;
            writers = new uint[sequence.Cues.Length];
        }
        internal double Next => endReached ? double.PositiveInfinity : started + (cycle * sequence.Duration + (cursor < sequence.Cues.Length ? sequence.Cues[cursor].At : sequence.Duration)) / speed;
        public MotionPlayback OnCompleted(Action callback) { if (!Active) { throw new InvalidOperationException("Playback already ended."); } completed = callback; return this; }
        internal void Dispatch() {
            if (!Owner.Alive) { Cancel(); return; }
            if (cursor < sequence.Cues.Length) {
                int at = cursor++; MotionCommand command = sequence.Cues[at].Command;
                writers[at] = command.Apply(speed); return;
            }
            if (cycle++ < repeats) { cursor = 0; return; }
            endReached = true; TryComplete();
        }
        internal void TryComplete() {
            if (!Active || !endReached) { return; }
            for (int i = 0; i < sequence.Cues.Length; i++) { if (sequence.Cues[i].Command.Running(writers[i])) { return; } }
            Active = false; Finished = true; sequence = null;
            Action callback = completed; completed = null; callback?.Invoke();
        }
        public void Cancel(bool complete = false) {
            if (!Active) { return; }
            Active = false; completed = null;
            MotionSequence plan = sequence; sequence = null;
            for (int i = 0; i < plan.Cues.Length; i++) { plan.Cues[i].Command.Cancel(writers[i], complete); }
        }
    }
    public sealed class MotionStateMachine<TState> : IDisposable where TState : struct, Enum {
        private readonly MotionScope scope;
        private readonly Dictionary<TState, (MotionSequence sequence, TState? next)> definitions = new Dictionary<TState, (MotionSequence, TState?)>();
        private readonly Action completed;
        private MotionPlayback playback;
        private int priority;
        private bool entered;
        public TState Current { get; private set; }
        public bool Running => playback?.Active == true;
        public bool HasState => entered;
        public MotionStateMachine(MotionScope scope) { this.scope = scope ?? throw new ArgumentNullException(nameof(scope)); completed = CompleteState; }
        public void Define(TState state, MotionSequence sequence, TState? next = null) { if (sequence == null) { throw new ArgumentNullException(nameof(sequence)); } sequence.Validate(scope, 1); definitions.Add(state, (sequence, next)); }
        private void CompleteState() { if (scope.Alive && definitions.TryGetValue(Current, out var definition) && definition.next.HasValue) { Enter(definition.next.Value, priority, true); } }
        public bool Enter(TState state, int priority = 0, bool reenter = false) {
            if (entered && EqualityComparer<TState>.Default.Equals(Current, state) && !reenter || playback?.Active == true && priority < this.priority) { return false; }
            if (!definitions.TryGetValue(state, out var definition)) { throw new ArgumentException("Undefined motion state.", nameof(state)); }
            playback?.Cancel(); Current = state; entered = true; this.priority = priority; playback = scope.Play(definition.sequence).OnCompleted(completed); return true;
        }
        public void Dispose() { playback?.Cancel(); playback = null; definitions.Clear(); entered = false; }
    }
}
