using System;
using System.Collections.Generic;
using Barotrauma;
using HarmonyLib;
using ScpItdMod.Core;

namespace ScpItdMod.Commands {
    public static class ClientCommands {
        private const string Feature = "client command router";
        private static readonly Dictionary<string, Action<string[]>> handlers = new Dictionary<string, Action<string[]>>(StringComparer.OrdinalIgnoreCase);

        public static void Attach(Harmony harmony) {
            harmony.Patch(AccessTools.Method(typeof(DebugConsole), nameof(DebugConsole.ExecuteCommand), new[] { typeof(string) }),
                prefix: ModuleHost.Bind(typeof(ClientCommands), nameof(ExecutePrefix)));
        }

        public static void Register(string name, string help, Action<string[]> onExecute) {
            if (string.IsNullOrWhiteSpace(name) || onExecute == null) { return; }
            handlers[name] = onExecute;
            ModCommands.Register(name, help, onExecute);
        }

        public static void Reset() => handlers.Clear();

        public static bool ExecutePrefix(string inputtedCommands) {
            if (Contained.IsDisabled(Feature) || GameMain.Client == null) { return true; }
            string[] words;
            try {
                if (string.IsNullOrWhiteSpace(inputtedCommands) || inputtedCommands.IndexOf('\n') >= 0) { return true; }
                words = ToolBox.SplitCommand(inputtedCommands);
            }
            catch (Exception error) {
                Contained.Fail(Feature, error);
                return true;
            }
            if (words.Length == 0 || !handlers.TryGetValue(words[0], out Action<string[]> handler)) { return true; }
            DebugConsole.NewCommand(inputtedCommands);
            string[] args = new string[words.Length - 1];
            Array.Copy(words, 1, args, 0, args.Length);
            try { handler(args); }
            catch (Exception error) {
                Contained.Fail(Feature, error);
                DebugConsole.NewMessage(words[0] + " failed: " + error.Message, Microsoft.Xna.Framework.Color.Red);
            }
            return false;
        }
    }
}
