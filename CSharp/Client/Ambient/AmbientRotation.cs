using Barotrauma;
using Microsoft.Xna.Framework;
using ScpItdMod.Core;

namespace ScpItdMod.Ambient {
    public static class AmbientRotation {
        private static double quietUntil;

        // Off by default: the mod's own ambience plays only after `scpitdambient true`.
        public static bool Enabled { get; set; }

        public static bool Active {
            get {
                Character character = Character.Controlled;
                return Enabled && PlayView.Active(character) && character.CurrentHull != null && SoundStore.Ready;
            }
        }

        public static void Advance(float deltaTime) {
            Hull hull = Character.Controlled?.CurrentHull;
            bool active = Active;
            AmbientZone zone = AmbientZoneClassifier.Classify(hull);
            AmbientBed.Advance(active, zone, deltaTime);
            if (!active) { return; }
            if (AmbientAudio.Playing) {
                quietUntil = Timing.TotalTime + AmbientConfig.QuietSeconds;
                return;
            }
            if (Timing.TotalTime < quietUntil) { return; }
            if (!AmbientConfig.Triggered(deltaTime)) { return; }
            AmbientAudio.Play(Mixed(zone), Origin());
        }

        public static void Reset() {
            quietUntil = 0.0;
            AmbientBed.Release();
            AmbientAudio.Release();
        }

        private static AmbientZone Mixed(AmbientZone zone) =>
            !AmbientZoneClassifier.Forced && AmbientConfig.Mixes() ? AmbientZone.General : zone;

        private static Vector2 Origin() {
            Vector3 listener = GameMain.SoundManager.ListenerPosition;
            return new Vector2(listener.X, listener.Y) + Rand.Vector(AmbientConfig.Jitter);
        }
    }
}
