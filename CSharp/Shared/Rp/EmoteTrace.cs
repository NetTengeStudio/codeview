using ScpItdMod.Core;

namespace ScpItdMod.Rp {
    // Follows one emote through the whole pipeline (menu -> request -> server verdict -> broadcast -> client playback) so a silent drop
    // names the stage that dropped it. Off by default; `scpitd_emote_trace` toggles it on the client and on the server.
    public static class EmoteTrace {
        public static bool On { get; private set; }

        public static void Register() =>
            ModCommands.Register("scpitd_emote_trace", "toggles the emote pipeline trace", _ => {
                On = !On;
                ModLog.Message("SCPITD emote trace " + (On ? "on" : "off"));
            });

        public static void Log(string stage, string detail) {
            if (On) { ModLog.Message("[emote] " + stage + ": " + detail); }
        }
    }
}
