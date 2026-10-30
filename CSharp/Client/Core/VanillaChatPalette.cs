using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace ScpItdMod.Core {
    public static class VanillaChatPalette {
        private const int ServerMessage = (int)ChatMessageType.Server;

        private static Color held;
        private static bool taken;

        public static void Attach() {
            if (taken) { return; }
            held = ChatMessage.MessageColor[ServerMessage];
            ChatMessage.MessageColor[ServerMessage] = HudPaint.Ink;
            taken = true;
        }

        public static void Release() {
            if (!taken) { return; }
            ChatMessage.MessageColor[ServerMessage] = held;
            taken = false;
        }
    }
}
