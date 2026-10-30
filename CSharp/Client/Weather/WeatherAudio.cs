using System;
using Barotrauma;
using Barotrauma.Sounds;
using ScpItdMod.Core;

namespace ScpItdMod.Weather {
    internal static class WeatherAudio {
        private const int Wind = 0, Gust = 1, Whistle = 2, Siren = 3, Count = 4;
        private const float RetrySeconds = 1;
        private static readonly SoundChannel[] channels = new SoundChannel[Count];
        private static readonly float[] levels = new float[Count];
        private static readonly float[] retry = new float[Count];
        private static bool alarm;
        // Loops are (re)started from Step, not once here: the sound manager may not be ready when the weather starts.
        internal static void Start() => Release();
        internal static void Step(float dt) {
            if (!SoundStore.Ready || WeatherService.Grid == null) return;
            WeatherState s = WeatherService.Current; WeatherConfig c = WeatherService.Config;
            var listener = Character.Controlled?.WorldPosition ?? GameMain.GameScreen.Cam.Position;
            int at = WeatherService.Grid.Index(listener.X / 100, listener.Y / 100);
            // Wind is always present while the weather runs; outside the grid the listener is in open air.
            float exposure = WeatherService.InZone(listener) ? WeatherService.Grid.ExposureShown(at) : 1, cabin = WeatherBov.EnvironmentGain(out bool cabinMuffled);
            float transmission = c.IndoorTransmission + (1 - c.IndoorTransmission) * exposure;
            float speed = Math.Clamp(s.WindSpeed / 27, 0, 1);
            float gust = .5f + .5f * WeatherNoise.Value(WeatherService.Director.Seed ^ 0x672f14a9u, WeatherService.Clock.Seconds / 4);
            bool warning = !WeatherService.OutsideWorkAllowed || s.Warning > .5f;
            if (warning && !alarm) { channels[Siren]?.Dispose(); channels[Siren] = SoundStore.File(c.SirenSound)?.Play(c.SirenGain); }
            alarm = warning;
            for (int i = 0; i < Siren; i++) Keep(i, LoopPath(i, c), dt);
            Span<float> gains = stackalloc float[Count];
            gains[Wind] = WeatherSoundMix.Wind(s.WindSpeed, c.WindGain, transmission);
            gains[Gust] = WeatherSoundMix.Gust(s.WindSpeed, s.Gustiness, gust, c.GustGain, transmission);
            gains[Whistle] = speed * speed * speed * c.WhistleGain * transmission * transmission;
            gains[Siren] = c.SirenGain;
            float slew = 1 - MathF.Exp(-dt * 12);
            for (int i = 0; i < channels.Length; i++) {
                SoundChannel channel = channels[i]; if (channel?.IsPlaying != true) continue;
                levels[i] += (gains[i] * cabin - levels[i]) * slew;
                channel.Gain = levels[i]; channel.Muffled = cabinMuffled || i != Siren && exposure < c.MuffleBelowExposure;
            }
        }
        // Wind is always on during the weather: a missing or ended loop is started again, at most once per RetrySeconds.
        private static void Keep(int i, string path, float dt) {
            if (channels[i]?.IsPlaying == true) return;
            retry[i] -= dt;
            if (retry[i] > 0) return;
            retry[i] = RetrySeconds;
            Sound sound = SoundStore.File(path);
            if (sound == null) return;
            channels[i]?.Dispose();
            channels[i] = sound.Play(0);
            if (channels[i] != null) channels[i].Looping = true;
        }
        private static string LoopPath(int i, WeatherConfig c) => i == Wind ? c.WindSound : i == Gust ? c.GustSound : c.WhistleSound;
        internal static void Release() {
            for (int i = 0; i < channels.Length; i++) { channels[i]?.Dispose(); channels[i] = null; levels[i] = 0; retry[i] = 0; }
            alarm = false;
        }
    }
}
