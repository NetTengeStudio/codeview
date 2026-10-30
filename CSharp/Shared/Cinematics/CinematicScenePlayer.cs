using System;
using ScpItdMod.Core;

namespace ScpItdMod.Cinematics {
    public interface ICinematicWorld {
        Vec2 Anchor(int binding);
        bool Execute(CinematicCue cue);
        bool Test(string condition);
        void EnterStage(int stage);
        string Step(float dt);
    }

    public sealed class CinematicScenePlayer : ICinematicStageSink {
        public readonly CinematicPlan Plan;
        public readonly CinematicSequencer Sequencer;
        public readonly CinematicLifetime Lifetime;
        public readonly CinematicVehicleDirector[] Vehicles;
        private readonly ICinematicWorld world;
        public CinematicScenePlayer(CinematicPlan plan, CinematicVehicleDirector[] vehicles, ICinematicWorld world) {
            Plan = plan ?? throw new ArgumentNullException(nameof(plan));
            if (vehicles == null || vehicles.Length != plan.BindingCount) throw new ArgumentException("One director slot per binding is required", nameof(vehicles));
            Vehicles = vehicles; this.world = world ?? throw new ArgumentNullException(nameof(world));
            Sequencer = new CinematicSequencer(plan); Lifetime = new CinematicLifetime(plan.BindingCount);
        }
        public void Step(float dt) {
            Sequencer.Advance(0, this);
            if (Sequencer.Status != CinematicStatus.Running && !(Sequencer.Status == CinematicStatus.Completed && Lifetime.Pending)) return;
            for (int i = 0; i < Vehicles.Length; i++) {
                CinematicVehicleDirector vehicle = Vehicles[i];
                if (vehicle != null && !vehicle.Step(dt, world.Anchor(vehicle.Route?.RelativeTo ?? -1))) { Fail("Vehicle command or telemetry rejected: " + Plan.BindingAt(i).Id); return; }
            }
            string failure = world.Step(dt);
            if (failure != null) { Fail(failure); return; }
            if (world is ICinematicRemovalWorld removal) Lifetime.Step(dt, this, removal);
            Sequencer.Advance(dt, this);
        }
        public void Fail(string reason) {
            if (Lifetime.Pending) Sequencer.FailPhysical(reason);
            else Sequencer.Fail(reason);
        }
        public bool Execute(CinematicCue cue) {
            switch (cue.Action) {
                case CinematicAction.Despawn:
                    return world is ICinematicRemovalWorld && (Vehicles[cue.Target] != null || (Lifetime.Removed & (1u << cue.Target)) != 0) && Lifetime.Request(cue);
                case CinematicAction.Flight:
                    if (Vehicles[cue.Target] == null) return true;
                    Vehicles[cue.Target].Fly(cue, world.Anchor(cue.RelativeTo)); return true;
                case CinematicAction.Engine:
                    if (Vehicles[cue.Target] == null) return true;
                    Vehicles[cue.Target].Master = cue.Enabled;
                    return !(cue.Enabled && cue.Value == "warm") || Vehicles[cue.Target].WarmStart(cue.Velocity);
                default: return world.Execute(cue);
            }
        }
        public bool Test(string condition) {
            if (condition == "despawn.done") return !Lifetime.Pending && Lifetime.Removed != 0;
            bool value = CinematicVehicleConditions.Test(Plan, Vehicles, condition, out bool known);
            return known ? value : world.Test(condition);
        }
        public void EnterStage(int stage) => world.EnterStage(stage);
        public string VehicleFailure() {
            foreach (CinematicVehicleDirector vehicle in Vehicles) if (vehicle?.Controller.Unsafe == true) return vehicle.Controller.Failure;
            return "";
        }
    }
}
