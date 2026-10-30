using HarmonyLib;
using ScpItdMod.Core;
using System;
using Barotrauma;

namespace ScpItdMod.Ambient {
    public sealed class AmbientModule : IModule {
        private const string ZoneName = "ambientzone";
        private const string ZoneHelp =
            "ambientzone [general/interior/machinery/outpost/none]: reports the ambience zone the player is in, "
            + "or pins it to one for testing.";

        private const string AmbientName = "scpitdambient";
        private const string AmbientHelp =
            "scpitdambient [true/false]: turns the mod's ambience (zone sounds and bed) on or off. Off by default.";

        private const string BedName = "generalambient";
        private const string BedHelp =
            "generalambient [true/false]: turns the continuous zone ambience on or off.";

        public void Attach(Harmony harmony) {
            harmony.Patch(
                AccessTools.Method(typeof(SoundPlayer), nameof(SoundPlayer.UpdateRandomAmbience)),
                prefix: ModuleHost.Bind(typeof(AmbientModule), nameof(UpdateRandomAmbiencePrefix)));
            harmony.Patch(
                AccessTools.Method(typeof(SoundPlayer), nameof(SoundPlayer.Update)),
                postfix: ModuleHost.Bind(typeof(AmbientModule), nameof(UpdatePostfix)));
            ModCommands.Register(ZoneName, ZoneHelp, ZoneExecute);
            ModCommands.Register(BedName, BedHelp, BedExecute);
            ModCommands.Register(AmbientName, AmbientHelp, AmbientExecute);
        }

        public void RoundStart() {
            AmbientRotation.Reset();
            AmbientAudio.Warm();
            AmbientBed.Warm();
        }

        public void Release() {
            AmbientZoneClassifier.Supplied = null;
            AmbientRotation.Reset();
        }

        public static bool UpdateRandomAmbiencePrefix() => !AmbientRotation.Active;

        public static void UpdatePostfix(float deltaTime) => AmbientRotation.Advance(deltaTime);

        public static void ZoneExecute(string[] args) {
            if (args != null && args.Length > 0) {
                AmbientZoneClassifier.Supplied =
                    Enum.TryParse(args[0], true, out AmbientZone wanted) ? wanted : (AmbientZone?)null;
            }
            Hull hull = Character.Controlled?.CurrentHull;
            ModLog.Message($"{ZoneName} pinned "
                + $"{(AmbientZoneClassifier.Forced ? AmbientZoneClassifier.Supplied.Value.ToString() : "none")}");
            ModLog.Message($"  hull \"{hull?.RoomName}\" zone {AmbientZoneClassifier.Classify(hull)}");
        }

        public static void AmbientExecute(string[] args) {
            AmbientRotation.Enabled = args != null && args.Length > 0 && bool.TryParse(args[0], out bool wanted)
                ? wanted
                : !AmbientRotation.Enabled;
            ModLog.Message($"{AmbientName} {AmbientRotation.Enabled}");
        }

        public static void BedExecute(string[] args) {
            AmbientBed.Enabled = args != null && args.Length > 0 && bool.TryParse(args[0], out bool wanted)
                ? wanted
                : !AmbientBed.Enabled;
            ModLog.Message($"{BedName} {AmbientBed.Enabled}");
        }
    }
}
