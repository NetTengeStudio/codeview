using System;
using ScpItdMod.Weather;

namespace ScpItdMod.Meteo {
    public sealed class MeteoStationModel {
        public const float HeaterWatts = 600;
        public const double SampleInterval = 10;
        public const float StallIce = .72f, FoulIce = .3f;
        private const float AccretionScale = 1f / 1600, HeaterMelt = .005f, DeiceMelt = .45f;
        private readonly uint seed;
        private float lastDirection;
        public float Ice { get; private set; }

        public MeteoStationModel(uint seed) { this.seed = seed; }

        public void SetIce(float ice) => Ice = Math.Clamp(ice, 0, 1);

        public void Deice(float amount) => Ice = MathF.Max(0, Ice - amount);

        public static float Accretion(in WeatherState truth, float exposure) {
            float cold = Math.Clamp((-22 - truth.Temperature) / 18, 0, 1);
            float moisture = Math.Clamp(truth.Humidity * 1.2f + truth.Snowfall + truth.Drifting * .5f, 0, 1.6f);
            float wind = Math.Clamp(truth.WindSpeed / 10, .2f, 2f);
            return moisture * wind * cold * Math.Clamp(exposure, 0, 1) * AccretionScale;
        }

        public static float HeaterEffect(in WeatherState truth) =>
            Math.Clamp(1 - (-truth.Temperature - 30) / 50 - truth.WindSpeed / 45, .1f, 1);

        public void Advance(in WeatherState truth, float exposure, bool heated, float dt) {
            if (!float.IsFinite(dt) || dt < 0) throw new ArgumentOutOfRangeException(nameof(dt));
            float accretion = Accretion(truth, exposure);
            float melt = heated ? HeaterMelt * HeaterEffect(truth) : 0;
            Ice = Math.Clamp(Ice + (accretion - melt) * dt, 0, 1);
        }

        public MeteoSample Measure(double time, ushort station, in WeatherState truth, WeatherConfig config, float exposure, float snowDepth, bool heated, float dt) {
            Advance(truth, exposure, heated, dt);
            return Read(time, station, truth, config, exposure, snowDepth);
        }

        public static double NextSample(double now, double previous) {
            if (!double.IsFinite(now) || !double.IsFinite(previous) || now < 0 || previous < 0) throw new ArgumentOutOfRangeException(nameof(now));
            return previous + (Math.Floor(Math.Max(0, now - previous) / SampleInterval) + 1) * SampleInterval;
        }

        public MeteoSample Read(double time, ushort station, in WeatherState truth, WeatherConfig config, float exposure, float snowDepth) {
            exposure = Math.Clamp(exposure, 0, 1);
            MeteoFlags flags = MeteoFlags.None;
            if (exposure < .35f) flags |= MeteoFlags.Indoor;
            if (Ice >= FoulIce) flags |= MeteoFlags.Iced;
            float ambient = WeatherNoise.Mix(config.IndoorTemperature, truth.Temperature, exposure);
            float t = ambient + Noise(time, 1) * (.12f + .004f * MathF.Max(0, -ambient - 30)) + Ice * 3f;
            float p = truth.Pressure + Noise(time, 2) * .1f;
            float windTrue = truth.WindSpeed * exposure;
            float wind;
            if (Ice >= StallIce) { flags |= MeteoFlags.WindStalled; wind = 0; }
            else {
                float k = 1 - Math.Clamp((Ice - .5f) * 2.2f, 0, 1);
                wind = MathF.Max(0, windTrue * k + Noise(time, 3) * (.15f + .03f * windTrue));
            }
            float direction = truth.WindDirection + Noise(time, 4) * (.025f + .12f / (1 + windTrue));
            if ((flags & MeteoFlags.WindStalled) != 0) direction = lastDirection; else lastDirection = direction;
            float humidity = Math.Clamp(WeatherNoise.Mix(.2f, truth.Humidity, exposure) + Noise(time, 5) * .03f, 0, 1);
            if (ambient < -40) flags |= MeteoFlags.HumidityInvalid;
            float visibility = MathF.Max(1, truth.Visibility * MathF.Exp(Noise(time, 6) * .08f) * (1 - .5f * Ice));
            if (exposure < .35f) visibility = 30000;
            float snow = MathF.Max(0, snowDepth + Noise(time, 7) * .006f);
            return new MeteoSample(time, station, MeteoPhysics.Quantize(t, .1f), MeteoPhysics.Quantize(p, .1f), MeteoPhysics.Quantize(wind, .1f), direction,
                MeteoPhysics.Quantize(humidity, .01f), MeteoPhysics.VisibilityStep(visibility), MeteoPhysics.Quantize(snow, .01f), Ice, flags);
        }

        private float Noise(double time, int channel) {
            uint k = seed ^ (uint)(time * 4) * 2654435761u ^ (uint)channel * 40503u;
            float a = WeatherNoise.Unit(k) * .99998f + .00001f, b = WeatherNoise.Unit(k ^ 0x9e3779b9u);
            return MathF.Sqrt(-2 * MathF.Log(a)) * MathF.Cos(MathF.Tau * b);
        }

        public static float DeiceAmount(float seconds) => DeiceMelt * seconds;
    }
}
