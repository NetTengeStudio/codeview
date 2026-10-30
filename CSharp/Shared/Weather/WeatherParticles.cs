using System;
using ScpItdMod.Core;

namespace ScpItdMod.Weather {
    public struct SnowParticle { public Vec2 Position, Previous; public float Size, Alpha; public byte Layer; }

    public sealed class WeatherParticles {
        public const int DrawBudget = 20480;
        public const float Margin = 3f, MaximumDriftHeight = SnowPhysics.SuspensionCeiling, SuspendedShare = .35f;
        public const float MeanDiameter = 3f, MaximumDiameter = 12f, Flutter = .12f;
        public const float ShortestLife = 8, LongestLife = 16;
        private struct Motion { internal float Settling, Flutter, Phase; internal bool Suspended; }
        private readonly Motion[] motion;
        private readonly Vec2[] position, previous, velocity;
        private readonly float[] age, diameter, lifetime, visibility;
        private readonly uint[] births;
        private readonly bool[] alive;
        private readonly uint seed;
        private int initialized;
        private WeatherRect box, retained;
        private bool fillNewArea;
        private uint reframes;
        private readonly Material.SnowFork fork = new Material.SnowFork(Math.Min(MaximumHelpers, Material.SnowFork.DefaultHelpers));
        private readonly Material.SnowFork.Range advanceRange;
        private WeatherState stepState; private double stepTime; private float stepSeconds, stepTurbulence; private Material.SnowPack stepSnow; private WeatherAirflow stepAir;
        public const int MaximumHelpers = 3, ParallelGrain = 2048;
        public int Helpers { get => fork.Helpers; init => fork = new Material.SnowFork(Math.Max(0, value)); }
        public int Capacity => position.Length;
        public static float DriftHeight(float wind) => SnowPhysics.HopHeight(wind);

        public WeatherParticles(int capacity, uint seed) {
            if (capacity <= 0 || capacity > DrawBudget) throw new ArgumentOutOfRangeException(nameof(capacity));
            this.seed = seed; advanceRange = Advance;
            position = new Vec2[capacity]; previous = new Vec2[capacity]; velocity = new Vec2[capacity];
            age = new float[capacity]; diameter = new float[capacity]; lifetime = new float[capacity]; visibility = new float[capacity];
            births = new uint[capacity]; alive = new bool[capacity];
            motion = new Motion[capacity];
            for (int i = 0; i < capacity; i++) {
                diameter[i] = DiameterOf(seed, i);
                motion[i] = new Motion {
                    Settling = LayerOf(i) == 2 ? SnowPhysics.GrainSettling : SnowPhysics.FlakeFallSpeed(diameter[i]),
                    Flutter = Flutter * MathF.Sqrt(diameter[i] / MeanDiameter),
                    Phase = WeatherNoise.Unit(seed ^ unchecked((uint)i * 2891336453u)) * MathF.Tau,
                    Suspended = WeatherNoise.Unit(seed ^ unchecked((uint)i * 3266489917u)) < SuspendedShare
                };
            }
        }

        private static byte LayerOf(int i) => (byte)(i % 3);
        private bool Suspended(int i) => motion[i].Suspended;
        public static float DiameterOf(uint seed, int i) => Math.Clamp(-MeanDiameter * MathF.Log(1 - .98f * WeatherNoise.Unit(seed ^ unchecked((uint)i * 747796405u) ^ 0xe3294af1u)), .5f, MaximumDiameter);

        private void Spawn(int i, in WeatherState state, Material.SnowPack snow, WeatherAirflow air, bool distribute) {
            uint hash = seed ^ unchecked((uint)i * 747796405u) ^ unchecked(++births[i] * 2654435761u);
            Vec2 p = SpawnPosition(hash);
            byte layer = LayerOf(i);
            lifetime[i] = WeatherNoise.Mix(ShortestLife, LongestLife, WeatherNoise.Unit(hash ^ 0xa511e9b3u));
            age[i] = distribute ? WeatherNoise.Unit(hash ^ 0x3c6ef372u) * lifetime[i] : 0;
            alive[i] = true;
            if (layer == 2) {
                int ground = snow.FindGround(p, MaximumDriftHeight + snow.MaximumDepth, box.Top - box.Bottom);
                if (ground < 0 || snow.AirborneFraction(ground) <= .001f) {
                    alive[i] = false; age[i] = lifetime[i] - .2f - .3f * WeatherNoise.Unit(hash ^ 0x632be5abu);
                    position[i] = previous[i] = p; velocity[i] = default; return;
                }
                float surfaceTop = snow.Top(ground, p.X);
                Vec2 wind = air?.Sample(new Vec2(p.X, surfaceTop + .05f)) ?? state.Wind;
                if (Suspended(i)) {
                    float band = SnowPhysics.SuspendedHeight(wind.X, WeatherNoise.Unit(hash ^ 0x9e3779b9u));
                    p = new Vec2(p.X, surfaceTop + MathF.Max(.02f, band));
                    velocity[i] = wind - new Vec2(0, SnowPhysics.GrainSettling);
                } else {
                    float hop = MathF.Min(MaximumDriftHeight, DriftHeight(wind.X) * WeatherNoise.Mix(.5f, 1.5f, WeatherNoise.Unit(hash ^ 0x9e3779b9u)));
                    float lift = MathF.Sqrt(2 * SnowPhysics.Gravity * hop);
                    float elapsed = distribute ? WeatherNoise.Unit(hash ^ 0x68e31da4u) * 2 * lift / SnowPhysics.Gravity : 0;
                    p = new Vec2(p.X, surfaceTop + .002f + lift * elapsed - .5f * SnowPhysics.Gravity * elapsed * elapsed);
                    velocity[i] = new Vec2(wind.X * .85f, lift - SnowPhysics.Gravity * elapsed);
                }
            } else {
                velocity[i] = (air?.Sample(p) ?? state.Wind) - new Vec2(0, motion[i].Settling);
            }
            position[i] = previous[i] = p;
            if (snow.Grid.Exposure(snow.Grid.Index(p.X, p.Y)) <= 0 || snow.InSnow(p)) {
                alive[i] = false;
                age[i] = lifetime[i] - .2f - .3f * WeatherNoise.Unit(hash ^ 0x632be5abu);
            }
        }

        public void Step(in WeatherState state, double time, float dt, int active, in WeatherRect view, Material.SnowPack snow, WeatherAirflow air) {
            if (snow == null) throw new ArgumentNullException(nameof(snow));
            if (!float.IsFinite(dt) || dt < 0 || dt > 1) throw new ArgumentOutOfRangeException(nameof(dt));
            if (!float.IsFinite(view.Right - view.Left) || !float.IsFinite(view.Top - view.Bottom) || view.Right <= view.Left || view.Top <= view.Bottom) throw new ArgumentOutOfRangeException(nameof(view));
            active = Math.Clamp(active, 0, Capacity);
            WeatherRect previousBox = box;
            box = new WeatherRect(view.Left - Margin, view.Bottom - Margin, view.Right + Margin, view.Top + Margin);
            float oldWidth = previousBox.Right - previousBox.Left, oldHeight = previousBox.Top - previousBox.Bottom;
            float newWidth = box.Right - box.Left, newHeight = box.Top - box.Bottom;
            if (initialized > 0 && (MathF.Abs(oldWidth - newWidth) > .001f || MathF.Abs(oldHeight - newHeight) > .001f)) {
                retained = box.Intersect(previousBox); reframes++;
                float retain = MathF.Min(1, oldWidth * oldHeight / (newWidth * newHeight));
                fillNewArea = retain < 1 && retained.Right > retained.Left && retained.Top > retained.Bottom;
                for (int i = 0, end = Math.Min(initialized, active); i < end; i++) {
                    uint hash = seed ^ unchecked((uint)i * 747796405u) ^ unchecked(reframes * 277803737u);
                    if (box.Contains(position[i].X, position[i].Y) && WeatherNoise.Unit(hash) < retain) continue;
                    Spawn(i, state, snow, air, true);
                }
                fillNewArea = false;
            }
            for (int i = initialized; i < active; i++) Spawn(i, state, snow, air, true);
            initialized = active;
            snow.Grid.Geometry.Seal(); snow.RefreshTopBounds(box);
            if (dt == 0) { for (int i = 0; i < active; i++) visibility[i] = Visibility(i, snow); return; }
            stepState = state; stepTime = time; stepSeconds = dt; stepSnow = snow; stepAir = air;
            stepTurbulence = MathF.Min(2, state.WindSpeed * state.Gustiness * .12f);
            try {
                if (air == null || air.Concurrent) fork.Run(active, ParallelGrain, advanceRange); else Advance(0, active);
            } finally { stepSnow = null; stepAir = null; }
        }

        private void Advance(int begin, int end) {
            WeatherState state = stepState; double time = stepTime; float dt = stepSeconds, turbulence = stepTurbulence; Material.SnowPack snow = stepSnow; WeatherAirflow air = stepAir;
            for (int i = begin; i < end; i++) {
                Move(i, state, time, dt, turbulence, snow, air);
                visibility[i] = Visibility(i, snow);
            }
        }

        private void Move(int i, in WeatherState state, double time, float dt, float turbulence, Material.SnowPack snow, WeatherAirflow air) {
            age[i] += dt;
            if (age[i] >= lifetime[i]) { Spawn(i, state, snow, air, false); return; }
            if (!box.Contains(position[i].X, position[i].Y)) {
                float width = box.Right - box.Left, height = box.Top - box.Bottom;
                Vec2 old = position[i];
                Vec2 wrapped = new Vec2(box.Left + Wrap(old.X - box.Left, width), box.Bottom + Wrap(old.Y - box.Bottom, height));
                position[i] = wrapped; previous[i] += wrapped - old;
            }
            if (!alive[i]) return;
            Vec2 p = position[i], v = velocity[i];
            previous[i] = p;
            byte layer = LayerOf(i);
            if (layer == 2 && !Suspended(i)) {
                Vec2 wind = air?.Sample(p) ?? state.Wind;
                float response = MathF.Max(.01f, SnowPhysics.GrainSettling / SnowPhysics.Gravity), decay = MathF.Exp(-dt / response);
                float target = wind.X * .85f, difference = v.X - target;
                p += new Vec2(target * dt + difference * response * (1 - decay), v.Y * dt - .5f * SnowPhysics.Gravity * dt * dt);
                v = new Vec2(target + difference * decay, v.Y - SnowPhysics.Gravity * dt);
            } else {
                ref readonly Motion particleMotion = ref motion[i];
                Vec2 midpoint = p + v * (dt * .5f);
                Vec2 wind = air?.Sample(midpoint) ?? state.Wind;
                Vec2 eddy = WeatherNoise.Curl(seed ^ 0xb5297a4du, midpoint.X * .7f, midpoint.Y * .7f, time + dt * .5f);
                float phase = particleMotion.Phase;
                float flutter = layer == 2 ? 0 : particleMotion.Flutter * MathF.Sin((float)(time * (5 + 4 * phase / MathF.Tau)) + phase);
                SnowPhysics.Advect(ref p, ref v, wind + eddy * turbulence + new Vec2(flutter, 0), particleMotion.Settling, dt);
            }
            if (!snow.Grid.SegmentClear(previous[i], p) || snow.InSnow(p)) { Spawn(i, state, snow, air, false); return; }
            position[i] = p; velocity[i] = v;
        }

        // What the particle contributes to the picture before the snowfall density is applied: its exposure, layer weight and fade, or for grains the final alpha. Zero hides it.
        private float Visibility(int i, Material.SnowPack snow) {
            if (!alive[i]) return 0;
            Vec2 world = position[i]; WeatherGrid grid = snow.Grid;
            float exposure = grid.Exposure(grid.Index(world.X, world.Y));
            if (exposure <= 0) return 0;
            byte layer = LayerOf(i);
            if (layer == 2) {
                int ground = snow.FindGround(world, 0, MaximumDriftHeight + snow.MaximumDepth);
                if (ground < 0) return 0;
                float top = snow.Top(ground, world.X);
                if (world.Y < top || world.Y > top + MaximumDriftHeight) return 0;
                return snow.AirborneFraction(ground) * exposure * .45f;
            }
            float fade = MathF.Min(1, MathF.Min(age[i], lifetime[i] - age[i]) / .2f);
            return exposure * (layer == 0 ? .75f : .45f) * MathF.Max(0, fade);
        }

        private Vec2 SpawnPosition(uint hash) {
            float u = WeatherNoise.Unit(hash), v = WeatherNoise.Unit(hash ^ 0x98573abfu);
            if (!fillNewArea) return new Vec2(WeatherNoise.Mix(box.Left, box.Right, u), WeatherNoise.Mix(box.Bottom, box.Top, v));
            Span<WeatherRect> bands = stackalloc WeatherRect[4];
            bands[0] = new WeatherRect(box.Left, box.Bottom, box.Right, retained.Bottom);
            bands[1] = new WeatherRect(box.Left, retained.Top, box.Right, box.Top);
            bands[2] = new WeatherRect(box.Left, retained.Bottom, retained.Left, retained.Top);
            bands[3] = new WeatherRect(retained.Right, retained.Bottom, box.Right, retained.Top);
            float total = (box.Right - box.Left) * (box.Top - box.Bottom) - (retained.Right - retained.Left) * (retained.Top - retained.Bottom);
            float choice = u * total;
            for (int k = 0; k < bands.Length; k++) {
                WeatherRect area = bands[k]; float size = (area.Right - area.Left) * (area.Top - area.Bottom);
                if (size <= 0) continue;
                if (choice <= size || k == bands.Length - 1) return new Vec2(WeatherNoise.Mix(area.Left, area.Right, Math.Clamp(choice / size, 0, 1)), WeatherNoise.Mix(area.Bottom, area.Top, v));
                choice -= size;
            }
            return new Vec2(WeatherNoise.Mix(box.Left, box.Right, u), WeatherNoise.Mix(box.Bottom, box.Top, v));
        }

        private static float Wrap(float value, float period) => value - MathF.Floor(value / period) * period;

        public int Collect(in WeatherState state, in WeatherRect view, int active, Span<SnowParticle> output) {
            active = Math.Clamp(active, 0, Math.Min(initialized, Capacity));
            int count = 0;
            float density = state.Snowfall + (1 - state.Cloud) * .035f;
            for (int i = 0; i < active && count < output.Length; i++) {
                float shown = visibility[i];
                if (shown <= 0) continue;
                Vec2 world = position[i];
                if (!view.Contains(world.X, world.Y)) continue;
                byte layer = LayerOf(i);
                float alpha = layer == 2 ? shown : density * shown;
                if (alpha <= 0) continue;
                output[count++] = new SnowParticle { Position = world, Previous = previous[i], Size = diameter[i] * .001f, Alpha = MathF.Min(alpha, .85f), Layer = layer };
            }
            return count;
        }
    }
}
