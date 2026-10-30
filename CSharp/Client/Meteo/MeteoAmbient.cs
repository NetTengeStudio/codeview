using System;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Sounds;
using HarmonyLib;
using Microsoft.Xna.Framework;
using ScpItdMod.Core;
using ScpItdMod.Weather;

namespace ScpItdMod.Meteo {
    internal static class MeteoAmbient {
        private const float HumRange = 900, CupsRange = 1100, SeekRange = 700;
        private const string HumPath = "Content/Sound/Meteo/rack_hum.ogg", CupsPath = "Content/Sound/Meteo/cups_loop.ogg";
        private static readonly SoundCue seeks = new SoundCue("Content/Sound/Meteo/drive_seek1.ogg", "Content/Sound/Meteo/drive_seek2.ogg", "Content/Sound/Meteo/drive_seek3.ogg");
        private static int rackWatch = -1, stationWatch = -1;
        private static SoundChannel hum, cups;
        private static float humLevel, cupsLevel;
        private static double nextScan, nextSeek;
        private static Item humItem, cupsItem;
        private static float humDistance, cupsDistance;
        private static readonly Identifier RackTag = MeteoWire.RackTag.ToIdentifier(), StationTag = MeteoWire.StationTag.ToIdentifier();

        public static void Attach(Harmony harmony) {
            rackWatch = Tagged.Watch(RackTag); stationWatch = Tagged.Watch(StationTag);
            harmony.Patch(AccessTools.Method(typeof(SoundPlayer), nameof(SoundPlayer.Update)), postfix: ModuleHost.Bind(typeof(MeteoAmbient), nameof(Update)));
        }

        public static void Reset() {
            hum?.Dispose(); cups?.Dispose(); hum = cups = null; humItem = cupsItem = null; humLevel = cupsLevel = 0; nextScan = nextSeek = 0; seeks.Release();
        }

        private static void Update(float deltaTime) {
            if (rackWatch < 0 || !SoundStore.Ready || Screen.Selected != GameMain.GameScreen || GameMain.GameSession == null) { if (hum != null || cups != null) { Reset(); } return; }
            Vector2 listener = Character.Controlled?.WorldPosition ?? GameMain.GameScreen.Cam.Position;
            double now = Timing.TotalTime;
            if (now >= nextScan) { nextScan = now + .5; Scan(listener); }
            float slew = 1 - MathF.Exp(-deltaTime * 6);
            Voice(ref hum, HumPath, humItem != null && SwipeWorldPowered(humItem) ? Falloff(humDistance, HumRange) * .55f : 0, humItem, slew, ref humLevel, HumRange);
            float cupsTarget = 0;
            if (cupsItem != null && WeatherService.Active && SwipeWorldPowered(cupsItem)) {
                float exposure = WeatherService.Grid?.ExposureShown(WeatherService.Grid.Index(cupsItem.WorldPosition.X / 100, cupsItem.WorldPosition.Y / 100)) ?? 0;
                cupsTarget = Falloff(cupsDistance, CupsRange) * Math.Clamp(WeatherService.Current.WindSpeed / 12, 0, 1) * exposure * .5f;
            }
            Voice(ref cups, CupsPath, cupsTarget, cupsItem, slew, ref cupsLevel, CupsRange);
            if (humItem != null && hum != null && now >= nextSeek && SwipeWorldPowered(humItem)) {
                nextSeek = now + 3 + Rand.Range(0f, 11f, Rand.RandSync.Unsynced);
                if (humDistance < SeekRange) { seeks.PlayOnce(.45f, humItem.WorldPosition, SeekRange, 1f + Rand.Range(-.04f, .04f, Rand.RandSync.Unsynced), humItem.CurrentHull); }
            }
        }

        private static bool SwipeWorldPowered(Item item) => !item.Removed && item.Condition > 0 && (item.GetComponent<Powered>() is Powered p ? p.HasPower : true);

        private static float Falloff(float distance, float range) { float t = Math.Clamp(1 - distance / range, 0, 1); return t * t; }

        private static void Scan(Vector2 listener) {
            humItem = Nearest(rackWatch, listener, HumRange, out humDistance);
            cupsItem = Nearest(stationWatch, listener, CupsRange, out cupsDistance);
        }

        private static Item Nearest(int watch, Vector2 listener, float range, out float distance) {
            Item best = null; float bestD = range * range;
            var list = Tagged.Of(watch);
            for (int i = 0; i < list.Count; i++) {
                float d = Vector2.DistanceSquared(list[i].WorldPosition, listener);
                if (d < bestD) { bestD = d; best = list[i]; }
            }
            distance = MathF.Sqrt(bestD);
            return best;
        }

        private static void Voice(ref SoundChannel channel, string path, float target, Item at, float slew, ref float level, float range) {
            level += (target - level) * slew;
            if (level < .004f || at == null) {
                if (channel != null) { channel.Dispose(); channel = null; level = 0; }
                return;
            }
            if (channel == null || !channel.IsPlaying) {
                Sound sound = SoundStore.File(path);
                if (sound == null) { return; }
                channel = sound.Play(level, range, at.WorldPosition);
                if (channel != null) { channel.Looping = true; }
                return;
            }
            channel.Gain = level;
            channel.Position = new Vector3(at.WorldPosition.X, at.WorldPosition.Y, 0);
        }
    }
}
