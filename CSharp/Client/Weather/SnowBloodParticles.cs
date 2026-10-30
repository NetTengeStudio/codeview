using System;
using ScpItdMod.Core;

namespace ScpItdMod.Weather {
    internal sealed class SnowBloodParticles {
        private readonly SnowBloodDrop[] drops = new SnowBloodDrop[SnowBlood.Capacity];
        private readonly double[] clocks = new double[SnowBlood.Capacity];
        private int count;
        internal ReadOnlySpan<SnowBloodDrop> Active => drops.AsSpan(0, count);
        internal void Spawn(ScpItdMod.Weather.Material.SnowPack snow, Vec2 position, Vec2 velocity, double born, double now) {
            if (count == drops.Length || !double.IsFinite(born) || !double.IsFinite(now) || now - born >= SnowBlood.Lifetime) return;
            drops[count] = new SnowBloodDrop { Position = position, Previous = position, Velocity = velocity };
            clocks[count] = Math.Min(now, born);
            if (Advance(count, snow, now)) count++; else drops[count] = default;
        }
        private bool Advance(int i, ScpItdMod.Weather.Material.SnowPack snow, double now) {
            double elapsed = Math.Max(0, now - clocks[i]);
            while (elapsed > 0) {
                float dt = (float)Math.Min(elapsed, SnowBlood.StepSeconds);
                if (!(dt > 0)) break;
                elapsed -= dt;
                if (SnowBlood.Advance(snow, ref drops[i], dt) != SnowBloodContact.Airborne) return false;
            }
            clocks[i] = now; return true;
        }
        internal void Step(ScpItdMod.Weather.Material.SnowPack snow, double now) {
            for (int i = 0; i < count;) {
                if (Advance(i, snow, now)) { i++; continue; }
                drops[i] = drops[--count]; clocks[i] = clocks[count]; drops[count] = default; clocks[count] = 0;
            }
        }
        internal void Clear() { Array.Clear(drops); Array.Clear(clocks); count = 0; }
    }
}
