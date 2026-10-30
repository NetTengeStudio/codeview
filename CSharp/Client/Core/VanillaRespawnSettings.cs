using System.Globalization;
using Barotrauma;
using Barotrauma.Networking;

namespace ScpItdMod.Core {
    public static class VanillaRespawnSettings {
        public static void Attach() {
            ModCommands.Register("respawninterval", "respawn interval in seconds", SetInterval);
            ModCommands.Register("respawnratio", "share of dead players needed to respawn, 0 to 1",
                SetRatio);
        }

        private static void SetInterval(string[] args) {
            ServerSettings settings = Settings();
            if (settings == null) { return; }
            if (args.Length == 0) {
                ModLog.Message($"respawninterval {settings.RespawnInterval}");
                return;
            }
            if (!Parse(args[0], out float seconds) || seconds <= 0.0f) {
                ModLog.Error($"{args[0]} is not a positive number of seconds");
                return;
            }
            settings.RespawnInterval = seconds;
            Push(settings);
            ModLog.Message($"respawninterval {settings.RespawnInterval}");
        }

        private static void SetRatio(string[] args) {
            ServerSettings settings = Settings();
            if (settings == null) { return; }
            if (args.Length == 0) {
                ModLog.Message($"respawnratio {settings.MinRespawnRatio}");
                return;
            }
            if (!Parse(args[0], out float ratio) || ratio < 0.0f || ratio > 1.0f) {
                ModLog.Error($"{args[0]} is not a share between 0 and 1");
                return;
            }
            settings.MinRespawnRatio = ratio;
            Push(settings);
            ModLog.Message($"respawnratio {settings.MinRespawnRatio}");
        }

        private static ServerSettings Settings() {
            ServerSettings settings = GameMain.Client?.ServerSettings;
            if (settings == null) {
                ModLog.Error("no server connected");
                return null;
            }
            if (!GameMain.Client.HasPermission(ClientPermissions.ManageSettings)) {
                ModLog.Error("no permission to manage settings");
                return null;
            }
            return settings;
        }

        private static void Push(ServerSettings settings) =>
            settings.ClientAdminWrite(ServerSettings.NetFlags.Properties);

        private static bool Parse(string text, out float value) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
