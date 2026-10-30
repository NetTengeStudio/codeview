using System;

namespace ScpItdMod.Fire {
    // Pool-fire physics shared by the GPU flame (Shaders/fire.fx), its sparks and smoke, and the barrel's light. Lengths are metres,
    // temperatures kelvin. Nothing here knows about the game, so Tests/Fire can pin every law the shader mirrors.
    public static class FlameModel {
        // Heskestad (1983): mean flame length L = D (3.7 Q*^(2/5) - 1.02) for a pool of diameter D and dimensionless heat release Q*.
        // A drum of burning wood and paper is a small fire: Q* near a quarter gives a flame about as tall as the drum is wide.
        public const float FuelQStar = .28f;
        public static float Length(float diameter, float qStar) => MathF.Max(.5f * diameter, diameter * (3.7f * MathF.Pow(qStar, .4f) - 1.02f));

        // Cetegen and Ahmed (1993): a pool fire pulses at f = 1.5 / sqrt(D) hertz; a puff of hot gas rolls up the flame each cycle.
        public static float PuffHertz(float diameter) => 1.5f / MathF.Sqrt(MathF.Max(.05f, diameter));

        // Wind leans the flame; a drum in a closed room has none. Metres of lean at the tip per metre per second of wind, per flame length.
        public const float LeanPerWind = .045f;

        // The barrel's clock wraps here so floats keep their resolution; every period the shader uses divides into it closely enough to hide the seam.
        public const int ClockWrap = 1024;
        public const float EmberCoolKelvin = 450f, FlameBaseKelvin = 1750f, FlameTipKelvin = 900f;

        // Light level of the flame as a multiplier around 1: the same puff cycle the shader draws, a slow breath and a fast crackle.
        // `clock` is the barrel's own time (seconds plus its offset) so two barrels never pulse together.
        public static float Flicker(uint seed, double clock, float puffHertz) {
            float puff = MathF.Cos(MathF.Tau * puffHertz * (float)(clock % ClockWrap));
            float slow = Weather.WeatherNoise.Value(seed ^ 0x3c6ef372u, clock * 1.6), fast = Weather.WeatherNoise.Value(seed ^ 0xa54ff53au, clock * 5.3);
            return .45f * puff + .35f * slow + .2f * fast;
        }

        // Blackbody colour as linear RGB in 0..1 (Helland's fit of the Planckian locus, 1000 K - 40000 K), converted from sRGB.
        public static void Blackbody(float kelvin, out float r, out float g, out float b) {
            float t = Math.Clamp(kelvin, 1000f, 40000f) / 100f;
            float sr = t <= 66 ? 255 : 329.698727446f * MathF.Pow(t - 60, -.1332047592f);
            float sg = t <= 66 ? 99.4708025861f * MathF.Log(t) - 161.1195681661f : 288.1221695283f * MathF.Pow(t - 60, -.0755148492f);
            float sb = t >= 66 ? 255 : t <= 19 ? 0 : 138.5177312231f * MathF.Log(t - 10) - 305.0447927307f;
            r = Linear(sr / 255); g = Linear(sg / 255); b = Linear(sb / 255);
        }
        private static float Linear(float c) { c = Math.Clamp(c, 0, 1); return c <= .04045f ? c / 12.92f : MathF.Pow((c + .055f) / 1.055f, 2.4f); }

        // Radiant exitance goes as T^4 (Stefan-Boltzmann); the flame is drawn relative to its yellow core.
        public static float Radiance(float kelvin) { float x = kelvin / 1500f; x *= x; return x * x; }

        // An ember leaving the flame cools toward the room: T(t) = Tamb + (T0 - Tamb) e^(-t/tau).
        public static float EmberKelvin(float launch, float tau, float age) => EmberCoolKelvin + (launch - EmberCoolKelvin) * MathF.Exp(-age / tau);

        // Drag on a particle in still air relaxes it toward the air's velocity: v(t) = air + (v0 - air) e^(-kt); the path is the integral.
        public static float Drift(float air, float v0, float drag, float age) => air * age + (v0 - air) * (1 - MathF.Exp(-drag * age)) / drag;
    }
}
