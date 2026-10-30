using System.Collections.Generic;
using Microsoft.Xna.Framework;
using ScpItdMod.Core;

namespace ScpItdMod.Ambient {
    public static class AmbientAudio {
        private static readonly SoundSwitch Zones = new SoundSwitch(
            new SoundCue(AmbientConfig.Bank("general", AmbientConfig.GeneralCount)),
            new SoundCue(AmbientConfig.Bank("zone1", AmbientConfig.InteriorCount)),
            new SoundCue(AmbientConfig.Bank("zone2", AmbientConfig.MachineryCount)),
            new SoundCue(AmbientConfig.Bank("zone3", AmbientConfig.OutpostCount)));

        private static readonly List<SoundCue> layers = new List<SoundCue>();

        public static bool Playing {
            get {
                for (int i = layers.Count - 1; i >= 0; i--) {
                    if (layers[i].Playing) { return true; }
                    layers.RemoveAt(i);
                }
                return false;
            }
        }

        public static void Warm() => Zones.Warm();

        public static void Play(AmbientZone zone, Vector2 worldPosition) {
            SoundCue cue = Zones.Of((int)zone);
            cue.Play(AmbientConfig.Gain, worldPosition, AmbientConfig.Range);
            if (!layers.Contains(cue)) { layers.Add(cue); }
        }

        public static void Release() {
            Zones.Release();
            layers.Clear();
        }
    }
}
