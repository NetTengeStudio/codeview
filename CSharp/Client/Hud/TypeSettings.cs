using System;
using System.Globalization;
using ScpItdMod.Core;

namespace ScpItdMod.Hud {
    public static class TypeSettings {
        private const string SpeedKey = "type.speed";
        private const string InstantKey = "type.instant";
        private const string VolumeKey = "type.volume";
        private const float DefaultGain = 0.04f;
        private const float MaxGain = 0.15f;

        private static int seen = -1;
        private static TypePace pace = TypePace.Normal;
        private static float clickGain = DefaultGain;

        public static TypePace Pace {
            get {
                if (seen != Prefs.Revision) {
                    seen = Prefs.Revision;
                    pace = new TypePace {
                        Speed = Prefs.Number(SpeedKey, 1.0f, TypePace.MinSpeed, TypePace.MaxSpeed),
                        Instant = Prefs.Flag(InstantKey, false)
                    };
                    clickGain = Prefs.Number(VolumeKey, DefaultGain, 0.0f, MaxGain);
                }
                return pace;
            }
        }

        public static float ClickGain { get { _ = Pace; return clickGain; } }

        public static void Attach() {
            ModCommands.Register("scpitd_typespeed", "text printing speed: a factor 0.25-4, or 'instant', or 'normal'", Command);
            ModCommands.Register("scpitd_typevolume", "quiet typewriter click: 0..0.15; 0 mutes, normal restores 0.04", VolumeCommand);
            TypeSound.Warm();
        }

        private static void VolumeCommand(string[] args) {
            if (args.Length == 0) { ModLog.Message($"typewriter click gain {ClickGain.ToString("0.###", CultureInfo.InvariantCulture)}"); return; }
            string value = args[0].Trim();
            if (value.Equals("normal", StringComparison.OrdinalIgnoreCase)) { Prefs.Set(VolumeKey, DefaultGain.ToString(CultureInfo.InvariantCulture)); }
            else if (float.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float gain) && gain >= 0.0f && gain <= MaxGain) {
                Prefs.Set(VolumeKey, gain.ToString(CultureInfo.InvariantCulture));
            } else { ModLog.Message("scpitd_typevolume 0..0.15 | normal"); return; }
            VolumeCommand(Array.Empty<string>());
        }

        private static void Command(string[] args) {
            if (args.Length == 0) {
                TypePace p = Pace;
                ModLog.Message(p.Instant ? "text appears instantly" : $"text prints at x{p.Clamped.ToString("0.##", CultureInfo.InvariantCulture)}");
                return;
            }
            string a = args[0].Trim();
            if (a.Equals("instant", StringComparison.OrdinalIgnoreCase)) {
                Prefs.Set(InstantKey, "1");
            } else if (a.Equals("normal", StringComparison.OrdinalIgnoreCase)) {
                Prefs.Set(InstantKey, "0");
                Prefs.Set(SpeedKey, "1");
            } else if (float.TryParse(a.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && v >= TypePace.MinSpeed && v <= TypePace.MaxSpeed) {
                Prefs.Set(InstantKey, "0");
                Prefs.Set(SpeedKey, v.ToString(CultureInfo.InvariantCulture));
            } else {
                ModLog.Message("scpitd_typespeed 0.25..4 | instant | normal");
                return;
            }
            Command(Array.Empty<string>());
        }
    }
}
