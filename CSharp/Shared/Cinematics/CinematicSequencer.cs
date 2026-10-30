using System;
using System.Collections.Generic;

namespace ScpItdMod.Cinematics {
    public enum CinematicStatus : byte { Running, Completed, Cancelled, Failed }

    public interface ICinematicStageSink {
        bool Execute(CinematicCue cue);
        bool Test(string condition);
        void EnterStage(int stage);
    }

    public sealed class CinematicSequencer {
        private readonly CinematicPlan plan;
        private readonly HashSet<string> signals = new HashSet<string>(StringComparer.Ordinal);
        private bool entered;
        public CinematicStatus Status { get; private set; }
        public int Stage { get; private set; }
        public uint Revision { get; private set; } = 1;
        public ulong Fired { get; private set; }
        public double Elapsed { get; private set; }
        public double StageElapsed { get; private set; }
        public string Failure { get; private set; } = "";
        public bool PresentationSkipped { get; private set; }
        public bool Condition(string key, ICinematicStageSink sink) => key == "always" || signals.Contains(key) || sink.Test(key);
        public CinematicSequencer(CinematicPlan plan) => this.plan = plan ?? throw new ArgumentNullException(nameof(plan));
        public void Signal(string signal) {
            if (!plan.AcceptsSignal(signal)) throw new ArgumentException("The scene does not declare this external event", nameof(signal));
            if (Status == CinematicStatus.Running) signals.Add(signal);
        }
        public void SkipPresentation() { if (!PresentationSkipped) { PresentationSkipped = true; Revision++; } }
        public void Cancel(string reason) { if (Status == CinematicStatus.Running) { Failure = reason ?? "cancelled"; Status = CinematicStatus.Cancelled; Revision++; } }
        public void Fail(string reason) { if (Status == CinematicStatus.Running) { Failure = reason ?? "failed"; Status = CinematicStatus.Failed; Revision++; } }
        internal void FailPhysical(string reason) {
            if (Status == CinematicStatus.Completed) { Failure = reason ?? "failed"; Status = CinematicStatus.Failed; Revision++; }
            else Fail(reason);
        }
        public void Advance(double seconds, ICinematicStageSink sink) {
            if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
            if (sink == null) throw new ArgumentNullException(nameof(sink));
            if (Status != CinematicStatus.Running) return;
            double remaining = seconds;
            int budget = 1024;
            while (Status == CinematicStatus.Running && --budget > 0) {
                if (!entered) { entered = true; sink.EnterStage(Stage); }
                CinematicStage stage = plan.Stages[Stage];
                for (int i = 0; i < stage.Cues.Length; i++) {
                    ulong bit = 1UL << i;
                    if ((Fired & bit) != 0 || stage.Cues[i].At > StageElapsed + 1e-9 || (Fired & stage.Dependencies[i]) != stage.Dependencies[i]) continue;
                    CinematicCue cue = stage.Cues[i];
                    if (cue.Action == CinematicAction.Signal) signals.Add(cue.Value);
                    if (!sink.Execute(cue)) { Fail("Action rejected: " + cue.Action + " " + cue.Id); return; }
                    Fired |= bit;
                    if (Status != CinematicStatus.Running) return;
                }
                bool transitioned = false;
                if (StageElapsed + 1e-9 >= stage.Minimum) {
                    for (int i = 0; i < stage.Transitions.Length; i++) {
                        CinematicTransition transition = stage.Transitions[i];
                        string key = transition.Condition;
                        bool value = Condition(key, sink);
                        if (value == transition.Negate) continue;
                        Change(transition.Stage); transitioned = true; break;
                    }
                }
                if (transitioned) continue;
                if (StageElapsed + 1e-9 >= stage.Maximum) { Change(stage.Timeout); continue; }
                if (Elapsed + 1e-9 >= plan.MaximumSeconds) { Fail("Scene time budget exceeded"); return; }
                if (remaining <= 1e-9) break;
                double step = Math.Min(remaining, Math.Min(stage.Maximum - StageElapsed, plan.MaximumSeconds - Elapsed));
                if (StageElapsed < stage.Minimum) step = Math.Min(step, stage.Minimum - StageElapsed);
                for (int i = 0; i < stage.Cues.Length; i++) if ((Fired & (1UL << i)) == 0 && stage.Cues[i].At > StageElapsed + 1e-9) step = Math.Min(step, stage.Cues[i].At - StageElapsed);
                Elapsed += step; StageElapsed += step; remaining -= step;
            }
            if (budget <= 0) Fail("Sequencer transition budget exceeded");
        }
        private void Change(int next) {
            Revision++;
            if (next < 0) { Status = next == -1 ? CinematicStatus.Completed : CinematicStatus.Failed; if (next == -2) Failure = "Aborted at stage: " + plan.Stages[Stage].Id; return; }
            Stage = next; StageElapsed = 0; Fired = 0; entered = false;
        }
    }
}
