using System;
using System.Collections.Generic;
using Barotrauma;
using ScpItdMod.Core;
using ScpItdMod.Weather.Material;

namespace ScpItdMod.Weather {
    internal static class WeatherBlood {
        private static readonly SnowBloodDrop[] drops = new SnowBloodDrop[SnowBlood.Capacity];
        private static readonly Dictionary<Character, float> clocks = new Dictionary<Character, float>();
        private static readonly List<Character> removed = new List<Character>();
        private static int count;

        internal static void Step(float dt) {
            if (!(dt > 0) || !WeatherService.Authority || !SnowRuntime.Ready) return;
            SnowPack snow = SnowRuntime.Pack;
            for (int i = 0; i < count;) {
                SnowBloodContact contact = SnowBlood.Advance(snow, ref drops[i], dt);
                if (contact == SnowBloodContact.Hit) SnowBlood.Deposit(snow, drops[i].Position, SnowPhysics.ActiveDepth);
                if (contact == SnowBloodContact.Airborne) { i++; continue; }
                drops[i] = drops[--count]; drops[count] = default;
            }
            IReadOnlyList<Character> people = Humans.All;
            for (int i = 0; i < people.Count; i++) {
                Character character = people[i];
                if (character.Removed || character.InWater || character.AnimController?.MainLimb is not { } limb || !WeatherService.InZone(limb.WorldPosition)) continue;
                float bleeding = character.Bleeding;
                if (!(bleeding > 0)) { clocks.Remove(character); continue; }
                clocks.TryGetValue(character, out float clock);
                clock += dt * bleeding / 10f;
                while (clock >= 1 && count < drops.Length) {
                    clock -= 1;
                    var position = new Vec2(limb.WorldPosition.X, limb.WorldPosition.Y) / WeatherService.UnitsPerMetre;
                    var worldVelocity = limb.LinearVelocity + (character.Submarine?.Velocity ?? Microsoft.Xna.Framework.Vector2.Zero);
                    var velocity = new Vec2(worldVelocity.X, worldVelocity.Y);
                    drops[count++] = new SnowBloodDrop { Position = position, Previous = position, Velocity = velocity };
                    WeatherNetwork.BloodDrop(position, velocity);
                }
                clocks[character] = MathF.Min(clock, 1);
            }
            removed.Clear();
            foreach (var pair in clocks) if (pair.Key.Removed) removed.Add(pair.Key);
            for (int i = 0; i < removed.Count; i++) clocks.Remove(removed[i]);
        }
        internal static void Clear() { Array.Clear(drops); count = 0; clocks.Clear(); removed.Clear(); }
    }
}
