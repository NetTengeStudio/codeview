using System;
using System.Collections.Generic;
using Barotrauma;
using HarmonyLib;

namespace ScpItdMod.Core {
    public interface IModule {
        void Attach(Harmony harmony);

        void RoundStart();

        void Release();
    }

    public interface IRoundEnd {
        void RoundEnd();
    }

    public static class ModuleHost {
        private sealed class Entry {
            public readonly IModule Module;
            public readonly Harmony Harmony;

            public Entry(IModule module, Harmony harmony) {
                Module = module;
                Harmony = harmony;
            }
        }

        private static readonly List<Entry> modules = new List<Entry>();
        private static Harmony hostHarmony;
        private static readonly List<string> failures = new List<string>();
        public static IReadOnlyList<string> Failures => failures;
        public static int AttachedCount => modules.Count;
        public static void ReportFailure(string feature, Exception error) {
            string message = feature + ": " + error.Message;
            failures.Add(message); ModLog.Error(message);
        }

        public static HarmonyMethod Bind(Type owner, string name) =>
            new HarmonyMethod(AccessTools.Method(owner, name) ?? throw new MissingMethodException(owner.FullName, name));

        public static void Attach(string harmonyId, params IModule[] set) {
            if (hostHarmony != null) { return; }
            CharacterRuntime.Reset(); failures.Clear();
            hostHarmony = new Harmony(harmonyId + ".host");
            try {
                hostHarmony.Patch(
                    AccessTools.Method(typeof(GameSession), nameof(GameSession.StartRound),
                        new[] { typeof(LevelData), typeof(bool), typeof(SubmarineInfo), typeof(SubmarineInfo) }),
                    postfix: Bind(typeof(ModuleHost), nameof(StartRoundPostfix)));
            } catch (Exception error) {
                ReportFailure("round start hook", error);
            }
            try {
                hostHarmony.Patch(AccessTools.Method(typeof(GameSession), nameof(GameSession.EndRound)), postfix: Bind(typeof(ModuleHost), nameof(EndRoundPostfix)));
            } catch (Exception error) { ReportFailure("round end hook", error); }
            try { Tagged.Attach(hostHarmony); }
            catch (Exception error) { ReportFailure("tag registry", error); }
            try { Humans.Attach(hostHarmony); }
            catch (Exception error) { ReportFailure("human registry", error); }

            for (int i = 0; i < set.Length; i++) {
                IModule module = set[i];
                if (module == null) { continue; }
                bool duplicate = false;
                for (int j = 0; j < modules.Count; j++) {
                    if (ReferenceEquals(modules[j].Module, module)) { duplicate = true; break; }
                }
                if (duplicate) { ModLog.Once($"{Name(module)} is already attached"); continue; }
                Harmony feature = new Harmony(harmonyId + "." + module.GetType().FullName + "." + i);
                try {
                    module.Attach(feature);
                    modules.Add(new Entry(module, feature));
                    PatchGuard.Cover(feature);
                } catch (Exception error) {
                    ReportFailure(Name(module), error);
                    Rollback(feature);
                    Retreat(module);
                }
            }
            PatchGuard.Cover(hostHarmony);
            PatchGuard.WatchOverrides(hostHarmony);
        }

        public static void Release() {
            // Dispose in reverse ownership order while core registries are still available.
            for (int i = modules.Count - 1; i >= 0; i--) {
                Entry entry = modules[i];
                Rollback(entry.Harmony);
                Retreat(entry.Module);
            }
            modules.Clear();
            Humans.Release();
            Tagged.Release();
            Rollback(hostHarmony);
            hostHarmony = null;
            PatchGuard.Reset();
            CharacterRuntime.Reset();
        }

        public static void StartRoundPostfix() {
            CharacterRuntime.Reset();
            for (int i = 0; i < modules.Count; i++) {
                IModule module = modules[i].Module;
                try {
                    module.RoundStart();
                } catch (Exception error) {
                    ModLog.Once($"{Name(module)} does not start: {error.Message}");
                }
            }
        }

        public static void EndRoundPostfix() {
            for (int i = modules.Count - 1; i >= 0; i--) {
                if (!(modules[i].Module is IRoundEnd ending)) { continue; }
                try { ending.RoundEnd(); }
                catch (Exception error) { ModLog.Once($"{Name(modules[i].Module)} does not end: {error.Message}"); }
            }
        }

        private static void Rollback(Harmony harmony) {
            if (harmony == null) { return; }
            try {
                harmony.UnpatchSelf();
            } catch (Exception error) {
                ModLog.Error($"harmony rollback failed: {error.Message}");
            }
        }

        private static void Retreat(IModule module) {
            try {
                module.Release();
            } catch (Exception error) {
                ModLog.Error($"{Name(module)} does not release: {error.Message}");
            }
        }

        private static string Name(IModule module) => module.GetType().Name;
    }
}
