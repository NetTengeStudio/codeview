using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Sounds;
using HarmonyLib;
using OpenAL;

namespace ScpItdMod.Core {
    public static class VanillaAudioDevices {
        private const double PollInterval = 1.0;
        private const string DisconnectedDevice = "<disconnected>";

        private const string CommandName = "audiofollowdevice";
        private const string CommandHelp =
            "audiofollowdevice [true/false]: keeps playback and microphone on the system default device, "
            + "and reports what the game and the system currently disagree about.";

        private static string outputDefault = string.Empty;
        private static string captureDefault = string.Empty;
        private static double nextPoll;

        public static bool Following { get; set; } = true;

        public static void Attach(Harmony harmony) {
            harmony.Patch(
                AccessTools.Method(typeof(SoundManager), nameof(SoundManager.Update)),
                postfix: ModuleHost.Bind(typeof(VanillaAudioDevices), nameof(UpdatePostfix)));
            ModCommands.Register(CommandName, CommandHelp, Execute);
        }

        public static void Release() {
            outputDefault = string.Empty;
            captureDefault = string.Empty;
            nextPoll = 0.0;
        }

        public static void UpdatePostfix() {
            if (!Following || !SoundStore.Ready || Timing.TotalTime < nextPoll) { return; }
            nextPoll = Timing.TotalTime + PollInterval;

            string output = Name(Alc.DefaultDeviceSpecifier);
            string capture = Name(Alc.CaptureDefaultDeviceSpecifier);
            GameSettings.Config config = GameSettings.CurrentConfig;
            bool changed = false;

            if (Adopt(output, outputDefault, config.Audio.AudioOutputDevice, Alc.OutputDevicesSpecifier)) {
                ModLog.Message($"playback \"{config.Audio.AudioOutputDevice}\" -> \"{output}\"");
                config.Audio.AudioOutputDevice = output;
                changed = true;
            }
            if (Adopt(capture, captureDefault, config.Audio.VoiceCaptureDevice, Alc.CaptureDeviceSpecifier)) {
                ModLog.Message($"microphone \"{config.Audio.VoiceCaptureDevice}\" -> \"{capture}\"");
                config.Audio.VoiceCaptureDevice = capture;
                changed = true;
            }

            outputDefault = output;
            captureDefault = capture;
            if (changed) { GameSettings.SetCurrentConfig(config); }
        }

        public static void Execute(string[] args) {
            Following = args.Length > 0 && bool.TryParse(args[0], out bool wanted) ? wanted : !Following;
            nextPoll = 0.0;
            ModLog.Message($"{CommandName} {Following}");
            ModLog.Message($"  playback   system \"{Name(Alc.DefaultDeviceSpecifier)}\""
                + $" game \"{GameSettings.CurrentConfig.Audio.AudioOutputDevice}\"");
            ModLog.Message($"  microphone system \"{Name(Alc.CaptureDefaultDeviceSpecifier)}\""
                + $" game \"{GameSettings.CurrentConfig.Audio.VoiceCaptureDevice}\"");
        }

        private static bool Adopt(string systemDefault, string knownDefault, string configured, int listSpecifier) {
            if (systemDefault.Length == 0) { return false; }
            if (string.IsNullOrEmpty(configured) || configured == DisconnectedDevice) { return true; }
            if (Same(configured, systemDefault)) { return false; }
            if (!Same(systemDefault, knownDefault)) { return true; }
            return !Listed(listSpecifier, configured);
        }

        private static bool Listed(int specifier, string device) {
            IReadOnlyList<string> devices;
            try {
                devices = Alc.GetStringList(IntPtr.Zero, specifier);
            } catch (Exception error) {
                ModLog.Error($"cannot list audio devices: {error.Message}");
                return true;
            }
            for (int i = 0; i < devices.Count; i++) {
                if (Same(devices[i], device)) { return true; }
            }
            return false;
        }

        private static string Name(int specifier) {
            try {
                return Alc.GetString(IntPtr.Zero, specifier) ?? string.Empty;
            } catch {
                return string.Empty;
            }
        }

        private static bool Same(string left, string right) =>
            string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
