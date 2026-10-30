using Barotrauma;
using Microsoft.Xna.Framework;
using ScpItdMod.Core;

namespace ScpItdMod.Footsteps {
    public static class FootstepAudio {
        private static readonly SoundSwitch Walking = new SoundSwitch(
            new SoundCue(FootstepConfig.Bank("step")),
            new SoundCue(FootstepConfig.Bank("stepmetal")),
            new SoundCue(FootstepConfig.Bank("snowstep")),
            new SoundCue(FootstepConfig.Bank("snowdeep")));

        private static readonly SoundSwitch Running = new SoundSwitch(
            new SoundCue(FootstepConfig.Bank("run")),
            new SoundCue(FootstepConfig.Bank("runmetal")),
            new SoundCue(FootstepConfig.Bank("snowrun")),
            new SoundCue(FootstepConfig.Bank("snowdeep")));

        public static void Warm() {
            Walking.Warm();
            Running.Warm();
        }

        public static bool Audible(Vector2 worldPosition) {
            if (GameMain.SoundManager == null) { return false; }
            Vector3 listener = GameMain.SoundManager.ListenerPosition;
            return Vector2.DistanceSquared(new Vector2(listener.X, listener.Y), worldPosition) <=
                FootstepConfig.Range * FootstepConfig.Range;
        }

        public static void Play(FootstepMaterial material, bool running, Vector2 worldPosition, Hull hullGuess) {
            SoundSwitch bank = running ? Running : Walking;
            float gain = material switch {
                FootstepMaterial.Snow => running ? FootstepConfig.SnowRunGain : FootstepConfig.SnowWalkGain,
                FootstepMaterial.DeepSnow => running ? FootstepConfig.DeepSnowRunGain : FootstepConfig.DeepSnowWalkGain,
                _ => running ? FootstepConfig.RunGain : FootstepConfig.WalkGain
            };
            bank.Of((int)material).PlayOnce(
                gain,
                worldPosition,
                FootstepConfig.Range,
                FootstepConfig.Pitch(),
                hullGuess);
        }

        public static void Release() {
            Walking.Release();
            Running.Release();
        }
    }
}
