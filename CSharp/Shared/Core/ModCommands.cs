using System;
using System.Collections.Generic;
using Barotrauma.LuaCs;

namespace ScpItdMod.Core {
    public static class ModCommands {
        private static readonly List<string> registered = new List<string>();

        private static IConsoleCommandsService service;

        public static void Mount(IConsoleCommandsService commands) => service = commands;

        public static void Register(string name, string help, Action<string[]> onExecute) {
            if (service == null || registered.Contains(name)) { return; }
            service.RegisterCommand(name, help, onExecute);
            registered.Add(name);
        }

        public static void Release() {
            for (int i = registered.Count - 1; i >= 0; i--) { service?.RemoveCommand(registered[i]); }
            registered.Clear();
            service = null;
        }
    }
}
