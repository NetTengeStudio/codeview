using System;
using System.Globalization;
using Barotrauma;
using FarseerPhysics.Dynamics;
using HarmonyLib;
using ScpItdMod.Core;

namespace ScpItdMod.Cinematics {
    public sealed class CinematicModule : IModule, IRoundEnd {
        public void Attach(Harmony harmony) {
            harmony.Patch(AccessTools.Method(typeof(World), nameof(World.Step), new[] { typeof(float) }),
                prefix: new HarmonyMethod(typeof(CinematicModule), nameof(PhysicsStep)) { priority = Priority.First, before = new[] { "blackoctobervehicles.physics" } });
            harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.IsKeyDown), new[] { typeof(InputType) }), prefix: ModuleHost.Bind(typeof(CinematicModule), nameof(KeyDown)));
            harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.IsKeyHit), new[] { typeof(InputType) }), prefix: ModuleHost.Bind(typeof(CinematicModule), nameof(KeyHit)));
            harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.Control), new[] { typeof(float), typeof(Camera) }), prefix: ModuleHost.Bind(typeof(CinematicModule), nameof(ActorControl)), postfix: ModuleHost.Bind(typeof(CinematicModule), nameof(ActorControlled)));
#if CLIENT
            CinematicClient.Attach(harmony);
#else
            CinematicServer.Attach(harmony);
#endif
            ModCommands.Register("scpitd_cine_list", "cinematic scenes authored in loaded submarines", _ => {
                foreach (CinematicAuthoredScene scene in CinematicSubScenes.Scenes.Values) ModLog.Message(scene.Id + " trigger=" + scene.Trigger + " stages=" + scene.Plan.StageCount + " hash=" + scene.Plan.Hash);
            });
            ModCommands.Register("scpitd_cine_start", "scene [binding=id ...] [audience=characterId,...]: start an authored scene", args => Command(1, args));
            ModCommands.Register("scpitd_cine_trigger", "signal: start authored scenes waiting for this signal", args => Command(8, args));
            ModCommands.Register("scpitd_cine_stop", "instance: stop with recovery", args => Command(2, args));
            ModCommands.Register("scpitd_cine_skip", "[instance]: skip local presentation; world sequence continues", args => {
#if CLIENT
                CinematicClient.Skip(args);
#else
                Command(3, args);
#endif
            });
            ModCommands.Register("scpitd_cine_skipall", "instance: skip presentation for everyone; mandatory world actions continue", args => Command(3, args));
            ModCommands.Register("scpitd_cine_signal", "instance event: signal a game condition", args => Command(4, args));
            ModCommands.Register("scpitd_cine_reload", "recompile scenes from the markers of loaded submarines while idle", args => Command(5, args));
            ModCommands.Register("scpitd_cine_status", "scene, physics, controller errors and measured budgets", _ => {
                CinematicHost.Status();
#if CLIENT
                CinematicClient.Status();
#else
                CinematicServer.Status();
#endif
            });
        }
        internal static void Command(byte operation, string[] args) {
            try {
#if CLIENT
                if (!CinematicHost.Authority) { CinematicClient.Request(operation, args); return; }
#endif
                ExecuteCommand(operation, args);
            } catch (Exception error) { ModLog.Error("Cinematic: " + error.Message); }
        }
        internal static void ExecuteCommand(byte operation, string[] args) {
            if (operation == 1) { CinematicHost.StartCommand(args); return; }
            if (operation == 5) { CinematicHost.Reload(); return; }
            if (operation == 8) { if (args.Length != 1 || !CinematicPlan.IsId(args[0])) throw new ArgumentException("A signal id is required"); CinematicSubScenes.Signal(args[0]); return; }
            if (args.Length < 1 || !ulong.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out ulong id)) throw new ArgumentException("A valid cinematic instance id is required");
            bool accepted = operation switch {
                2 => CinematicHost.Stop(id),
                3 => CinematicHost.SkipPresentation(id),
                4 => args.Length == 2 && CinematicHost.Signal(id, args[1]),
                _ => false
            };
            if (!accepted) throw new InvalidOperationException("Cinematic command rejected");
        }
        private static void PhysicsStep(World __instance, float __0) {
            if (!ReferenceEquals(__instance, GameMain.World) || Screen.Selected is not GameScreen || GameMain.GameSession?.GameMode?.Paused == true) return;
            CinematicHost.Step(__0);
        }
        private static bool KeyDown(Character __instance, InputType __0, ref bool __result) => !CinematicActors.Input(__instance, __0, false, out __result);
        private static bool KeyHit(Character __instance, InputType __0, ref bool __result) => !CinematicActors.Input(__instance, __0, true, out __result);
        private static void ActorControl(Character __instance) => CinematicActors.BeforeControl(__instance);
        private static void ActorControlled(Character __instance) => CinematicActors.AfterControl(__instance);
        public void RoundStart() { Reset("Round changed"); CinematicHost.Removals.Reset(); CinematicSubScenes.RoundStart(); }
        public void RoundEnd() => Reset("Round ended");
        public void Release() {
            Reset("Cinematic module unloaded");
#if CLIENT
            CinematicClient.DetachBindings();
#endif
            CinematicHost.Catalog.Clear(); CinematicSubScenes.Scenes.Clear(); CinematicHost.ReleaseEvents();
        }
        private static void Reset(string reason) {
            CinematicHost.Reset(reason);
            CinematicSubScenes.Reset();
#if CLIENT
            CinematicClient.Reset();
#else
            CinematicServer.Reset();
#endif
        }
    }
}
