using System;
using ScpItdMod.Core;
using ScpItdMod.Weather.Material;

namespace ScpItdMod.Weather {
    // Portions of snow thrown from a shovel: they fly under gravity and drag in the wind and land as particles on the surface they meet.
    public sealed class SnowFlights {
        private struct Flight { internal Vec2 Position, Velocity; internal SnowPayload Payload; internal float Age, Width; }
        public const int Capacity = 64;
        private readonly Flight[] flights = new Flight[Capacity];
        public int Count { get; private set; }
        public double Mass { get { double mass = 0; for (int i = 0; i < Count; i++) mass += flights[i].Payload.Mass; return mass; } }
        public void Clear() { Array.Clear(flights); Count = 0; }
        public bool Launch(Vec2 point, Vec2 velocity, ref SnowPayload payload, float width = 1) {
            if (!float.IsFinite(width) || width <= 0 || !double.IsFinite(payload.Mass) || !double.IsFinite(payload.Volume)) throw new ArgumentException("Invalid snow payload geometry");
            if (Count == Capacity || payload.Mass <= 0 || payload.Volume <= 0) return false;
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(velocity.X) || !float.IsFinite(velocity.Y)) throw new ArgumentException("Invalid snow launch");
            flights[Count++] = new Flight { Position = point, Velocity = velocity, Payload = payload, Width = width }; payload = default; return true;
        }
        internal void Step(SnowPack snow, in WeatherState weather, float dt) {
            if (snow == null) throw new ArgumentNullException(nameof(snow));
            if (!float.IsFinite(dt) || dt <= 0 || dt > 1) throw new ArgumentOutOfRangeException(nameof(dt));
            int steps = Math.Max(1, (int)MathF.Ceiling(dt * 60));
            for (int i = 0; i < steps; i++) Integrate(snow, weather, dt / steps);
        }
        private static bool Blocked(SnowPack snow, Vec2 from, Vec2 to) => !snow.Grid.SegmentClear(from, to) || snow.FindSurface(to, 0) >= 0;
        private void Integrate(SnowPack snow, in WeatherState weather, float dt) {
            float airDensity = weather.Pressure * 100 / (287.05f * (weather.Temperature + 273.15f));
            for (int i = 0; i < Count;) {
                ref Flight flight = ref flights[i]; Vec2 old = flight.Position;
                float radius = MathF.Cbrt((float)flight.Payload.Volume * flight.Width * (3 / (4 * MathF.PI)));
                float drag = .5f * .8f * airDensity * MathF.PI * radius * radius * (flight.Velocity - weather.Wind).Length() / MathF.Max(.01f, (float)flight.Payload.Mass * flight.Width);
                Vec2 velocity = (flight.Velocity + (weather.Wind * drag - new Vec2(0, SnowPhysics.Gravity)) * dt) / (1 + drag * dt);
                Vec2 next = old + (flight.Velocity + velocity) * (.5f * dt);
                int steps = Math.Max(1, (int)MathF.Ceiling(Vec2.Distance(old, next) / (snow.Grid.Cell * .25f)));
                bool impact = false; Vec2 landing = next;
                for (int k = 1; k <= steps; k++) {
                    Vec2 point = Vec2.Lerp(old, next, k / (float)steps);
                    if (Blocked(snow, old, point)) {
                        float lo = (k - 1f) / steps, hi = k / (float)steps;
                        for (int n = 0; n < 12; n++) {
                            float t = (lo + hi) * .5f; Vec2 probe = Vec2.Lerp(old, next, t);
                            if (Blocked(snow, old, probe)) hi = t; else lo = t;
                        }
                        landing = Vec2.Lerp(old, next, lo); impact = true; break;
                    }
                }
                flight.Age += dt;
                bool done = false;
                if (impact) {
                    GeometrySample contact = snow.Grid.Geometry.Sample(landing, snow.Grid.Cell);
                    int layer = snow.FindSurface(landing, .005f);
                    Vec2 normal = contact.Normal;
                    if (layer >= 0 && MathF.Abs(landing.Y - snow.Top(layer, landing.X)) < .01f) {
                        float slope = (snow.Top(layer, landing.X + .1f) - snow.Top(layer, landing.X - .1f)) / .2f;
                        normal = Vec2.Normalize(new Vec2(-slope, 1));
                    }
                    if (normal.Y > .2f) done = SnowShovel.Pour(snow, weather.Temperature, landing, MathF.Max(.25f, radius * 2), ref flight.Payload) && flight.Payload.Mass <= 0;
                    if (!done) { next = landing + normal * .004f; velocity -= normal * MathF.Min(0, Vec2.Dot(velocity, normal)); }
                }
                if (!done && (!snow.Grid.Bounds.Contains(next.X, next.Y) || flight.Age > 30)) { SnowShovel.Export(ref flight.Payload); done = true; }
                if (done) { flights[i] = flights[--Count]; flights[Count] = default; continue; }
                flight.Position = next; flight.Velocity = velocity; i++;
            }
        }
    }
}
