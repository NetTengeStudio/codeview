using System.Collections.Generic;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace ScpItdMod.Core {
    public static class Humans {
        private static readonly List<Character> characters = new List<Character>();
        private static readonly HashSet<Character> known = new HashSet<Character>(ReferenceEqualityComparer.Instance);
        public static IReadOnlyList<Character> All => characters;
        public static event System.Action Changed;
        public static void Attach(Harmony harmony) {
            harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.Create), new[] {
                typeof(CharacterPrefab), typeof(Vector2), typeof(string), typeof(CharacterInfo), typeof(ushort), typeof(bool), typeof(bool), typeof(bool), typeof(RagdollParams), typeof(bool)
            }), postfix: ModuleHost.Bind(typeof(Humans), nameof(Created)));
            harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.Remove), System.Type.EmptyTypes), postfix: ModuleHost.Bind(typeof(Humans), nameof(Removed)));
            foreach (Character c in Character.CharacterList) { Created(c); }
        }
        public static void Created(Character __result) {
            try {
                if (__result == null || __result.Removed || !__result.IsHuman || !known.Add(__result)) { return; }
                characters.Add(__result);
                Changed?.Invoke();
            } catch (System.Exception error) { ModLog.Once("human registry add failed: " + error.Message); }
        }
        public static void Removed(Character __instance) {
            try {
                if (known.Remove(__instance)) { characters.Remove(__instance); Changed?.Invoke(); }
            } catch (System.Exception error) { ModLog.Once("human registry remove failed: " + error.Message); }
        }
        public static void Release() { characters.Clear(); known.Clear(); Changed = null; }
    }
}
