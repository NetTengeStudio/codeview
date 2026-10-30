using System.Collections.Generic;
using Barotrauma.LuaCs;

namespace ScpItdMod.Core {
    public static class ModLog {
        private static readonly HashSet<string> spoken = new HashSet<string>();

        private static ILoggerService service;

        public static void Mount(ILoggerService logger) => service = logger;

        public static void Release() {
            spoken.Clear();
            service = null;
        }

        public static void Message(string text) => service?.LogMessage(text);

        public static void Error(string text) =>
            service?.LogError(text, LuaCsMessageOrigin.CSharpMod);

        public static void Once(string text) {
            if (!spoken.Add(text)) { return; }
            Error(text);
        }
    }
}
