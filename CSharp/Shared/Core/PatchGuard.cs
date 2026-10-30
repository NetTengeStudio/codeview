using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Barotrauma.Items.Components;
using HarmonyLib;

namespace ScpItdMod.Core {
    public static class PatchGuard {
        private static readonly Assembly mine = typeof(PatchGuard).Assembly;
        private static readonly FaultBreaker breaker = new FaultBreaker(FaultPolicy.TripLimit);
        private static readonly Dictionary<MethodBase, List<Harmony>> covers = new Dictionary<MethodBase, List<Harmony>>();
        private static readonly HarmonyMethod finalizer = new HarmonyMethod(AccessTools.Method(typeof(PatchGuard), nameof(Contain)));

        public static int Covered { get; private set; }
        public static int Swallowed { get; private set; }
        public static int Tripped { get; private set; }
        public static int Skipped { get; private set; }

        public static void Cover(Harmony harmony) {
            MethodBase[] methods;
            try { methods = harmony.GetPatchedMethods().ToArray(); }
            catch (Exception error) {
                ModLog.Error("patch guard cannot list patched methods: " + error.Message);
                return;
            }
            for (int i = 0; i < methods.Length; i++) { Watch(harmony, methods[i]); }
        }

        public static void Watch(Harmony harmony, MethodBase method) {
            if (method == null) { return; }
            try {
                if (covers.TryGetValue(method, out List<Harmony> owners) && owners.Contains(harmony)) { return; }
                harmony.Patch(method, finalizer: finalizer);
                if (owners == null) { covers[method] = owners = new List<Harmony>(2); }
                owners.Add(harmony);
                Covered++;
            } catch (Exception error) {
                Skipped++;
                ModLog.Once("patch guard skips " + FaultPolicy.KeyOf(method) + ": " + error.Message);
            }
        }

        public static void WatchOverrides(Harmony harmony) {
            Type[] types;
            try { types = mine.GetTypes(); }
            catch (ReflectionTypeLoadException error) { types = error.Types.Where(type => type != null).ToArray(); }
            const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (int i = 0; i < types.Length; i++) {
                if (!typeof(ItemComponent).IsAssignableFrom(types[i]) || types[i].IsGenericTypeDefinition) { continue; }
                MethodInfo[] methods = types[i].GetMethods(Declared);
                for (int j = 0; j < methods.Length; j++) {
                    MethodInfo method = methods[j];
                    if (method.IsAbstract || !method.IsVirtual || method.GetBaseDefinition().DeclaringType == method.DeclaringType) { continue; }
                    Watch(harmony, method);
                }
            }
        }

        public static Exception Contain(Exception __exception, MethodBase __originalMethod) {
            if (__exception == null) { return null; }
            try {
                if (!FaultPolicy.Owns(__exception, mine)) { return __exception; }
                string key = FaultPolicy.KeyOf(__originalMethod);
                Swallowed++;
                bool tripped = breaker.Record(key);
                if (breaker.Count(key) == 1) { ModLog.Error("contained " + FaultPolicy.Describe(__exception, mine) + " in " + key); }
                if (tripped) { Trip(__originalMethod, key); }
                return null;
            } catch (Exception) {
                return __exception;
            }
        }

        public static void Reset() {
            covers.Clear();
            breaker.Reset();
            Covered = 0;
            Swallowed = 0;
            Tripped = 0;
            Skipped = 0;
        }

        private static void Trip(MethodBase method, string key) {
            Tripped++;
            if (method == null || !covers.TryGetValue(method, out List<Harmony> owners)) { return; }
            for (int i = 0; i < owners.Count; i++) {
                try { owners[i].Unpatch(method, HarmonyPatchType.All, owners[i].Id); }
                catch (Exception error) { ModLog.Error("patch guard cannot unpatch " + key + ": " + error.Message); }
            }
            covers.Remove(method);
            ModLog.Error(key + " failed " + FaultPolicy.TripLimit + " times; the mod's hooks on it are removed and the game keeps running");
        }
    }
}
