using System;
using Barotrauma;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using HarmonyLib;
using ScpItdMod.Core;

namespace ScpItdMod.Maps {
    public sealed class MapModule : IModule, IRoundEnd {
        private static PhysicsBody heldBody;
        private static BodyType originalType;

        public void Attach(Harmony harmony) {
            harmony.Patch(AccessTools.Method(typeof(FarseerPhysics.Dynamics.World), nameof(FarseerPhysics.Dynamics.World.Step), new[] { typeof(float) }),
                prefix: new HarmonyMethod(typeof(MapModule), nameof(BeforeStep)) { priority = Priority.First + 200 });
            Synchronize();
        }

        public void RoundStart() => Synchronize();
        public void RoundEnd() => Restore();
        public void Release() => Restore();

        private static void BeforeStep(FarseerPhysics.Dynamics.World __instance) {
            if (ReferenceEquals(__instance, GameMain.World)) Synchronize();
        }

        private static void Synchronize() {
            PhysicsBody next = GameMain.GameSession?.IsRunning == true ? Submarine.MainSub?.PhysicsBody : null;
            if (next != null && next.Removed) next = null;
            if (!ReferenceEquals(next, heldBody)) {
                Restore();
                heldBody = next;
                if (next != null) originalType = next.BodyType;
            }
            if (heldBody == null || heldBody.BodyType == BodyType.Static) return;
            heldBody.ResetDynamics();
            heldBody.BodyType = BodyType.Static;
        }

        private static void Restore() {
            PhysicsBody previous = heldBody;
            heldBody = null;
            if (previous == null || previous.Removed || previous.BodyType != BodyType.Static || originalType == BodyType.Static) return;
            previous.ResetDynamics();
            previous.BodyType = originalType;
        }
    }
}
