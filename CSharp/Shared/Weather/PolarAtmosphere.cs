using System;

namespace ScpItdMod.Weather {
    public readonly struct Radiance {
        public readonly float R, G, B;
        public Radiance(float r, float g, float b) { R = r; G = g; B = b; }
        public float Luma => .2126f * R + .7152f * G + .0722f * B;
    }

    public static class PolarAtmosphere {
        public const float EarthRadius = 6360, AtmosphereRadius = 6420, RayleighHeight = 8, MieHeight = 1.2f, MieAsymmetry = .76f;
        public const float RayleighR = 5.8e-3f, RayleighG = 13.5e-3f, RayleighB = 33.1e-3f, CleanMie = 4e-3f, HazyMie = 40e-3f;
        public const float OzoneR = .65e-3f, OzoneG = 1.881e-3f, OzoneB = .085e-3f, OzonePeak = 25, OzoneWidth = 15;
        private static float OzoneDensity(float h) => MathF.Max(0, 1 - MathF.Abs(h - OzonePeak) / OzoneWidth);
        public const float SunIntensity = 20, ExposureKey = .38f, NightExposure = 900;
        public const int ViewSamples = 8, LightSamples = 4;

        public static float Mie(float humidity, float snowfall) => CleanMie + (HazyMie - CleanMie) * Math.Clamp(.3f * humidity + .7f * snowfall, 0, 1);

        private static void Direction(float azimuth, float elevation, out float x, out float y, out float z) {
            x = MathF.Sin(azimuth) * MathF.Cos(elevation); y = MathF.Sin(elevation); z = MathF.Cos(azimuth) * MathF.Cos(elevation);
        }

        private static bool Sphere(float ox, float oy, float oz, float dx, float dy, float dz, float radius, out float near, out float far) {
            float b = ox * dx + oy * dy + oz * dz, c = ox * ox + oy * oy + oz * oz - radius * radius, d = b * b - c;
            if (d < 0) { near = far = -1; return false; }
            d = MathF.Sqrt(d); near = -b - d; far = -b + d; return true;
        }

        public static Radiance Scatter(float azimuth, float elevation, float sunAzimuth, float sunElevation, float altitudeKm, float mie) {
            Direction(azimuth, elevation, out float dx, out float dy, out float dz);
            Direction(sunAzimuth, sunElevation, out float sx, out float sy, out float sz);
            float oy = EarthRadius + altitudeKm;
            Sphere(0, oy, 0, dx, dy, dz, AtmosphereRadius, out _, out float length);
            if (Sphere(0, oy, 0, dx, dy, dz, EarthRadius, out float ground, out _) && ground > 0) length = ground;
            float step = length / ViewSamples, depthO = 0, depthR = 0, depthM = 0, sumR0 = 0, sumR1 = 0, sumR2 = 0, sumM0 = 0, sumM1 = 0, sumM2 = 0;
            for (int i = 0; i < ViewSamples; i++) {
                float t = step * (i + .5f), px = dx * t, py = oy + dy * t, pz = dz * t;
                float h = MathF.Sqrt(px * px + py * py + pz * pz) - EarthRadius;
                float hr = MathF.Exp(-h / RayleighHeight) * step, hm = MathF.Exp(-h / MieHeight) * step;
                depthR += hr; depthM += hm; depthO += OzoneDensity(h) * step;
                if (Sphere(px, py, pz, sx, sy, sz, EarthRadius, out float shade, out _) && shade > 0) continue;
                Sphere(px, py, pz, sx, sy, sz, AtmosphereRadius, out _, out float light);
                float lightStep = light / LightSamples, lightR = 0, lightM = 0, lightO = 0;
                for (int j = 0; j < LightSamples; j++) {
                    float u = lightStep * (j + .5f), qx = px + sx * u, qy = py + sy * u, qz = pz + sz * u;
                    float hl = MathF.Sqrt(qx * qx + qy * qy + qz * qz) - EarthRadius;
                    lightR += MathF.Exp(-hl / RayleighHeight) * lightStep; lightM += MathF.Exp(-hl / MieHeight) * lightStep; lightO += OzoneDensity(hl) * lightStep;
                }
                float r = depthR + lightR, m = 1.1f * mie * (depthM + lightM), o = depthO + lightO;
                float a0 = MathF.Exp(-(RayleighR * r + m + OzoneR * o)), a1 = MathF.Exp(-(RayleighG * r + m + OzoneG * o)), a2 = MathF.Exp(-(RayleighB * r + m + OzoneB * o));
                sumR0 += a0 * hr; sumR1 += a1 * hr; sumR2 += a2 * hr; sumM0 += a0 * hm; sumM1 += a1 * hm; sumM2 += a2 * hm;
            }
            float mu = dx * sx + dy * sy + dz * sz, g = MieAsymmetry;
            float phaseR = 3f / (16 * MathF.PI) * (1 + mu * mu), phaseM = 3f / (8 * MathF.PI) * (1 - g * g) * (1 + mu * mu) / ((2 + g * g) * MathF.Pow(1 + g * g - 2 * g * mu, 1.5f));
            return new Radiance(SunIntensity * (sumR0 * RayleighR * phaseR + sumM0 * mie * phaseM), SunIntensity * (sumR1 * RayleighG * phaseR + sumM1 * mie * phaseM), SunIntensity * (sumR2 * RayleighB * phaseR + sumM2 * mie * phaseM));
        }

        public static Radiance Sunlight(float sunElevation, float altitudeKm, float mie) {
            float sy = MathF.Sin(sunElevation), sz = MathF.Cos(sunElevation), oy = EarthRadius + altitudeKm;
            if (Sphere(0, oy, 0, 0, sy, sz, EarthRadius, out float hit, out _) && hit > 0) return new Radiance(0, 0, 0);
            Sphere(0, oy, 0, 0, sy, sz, AtmosphereRadius, out _, out float light);
            float step = light / 6, r = 0, m = 0, o = 0;
            for (int j = 0; j < 6; j++) { float u = step * (j + .5f), qy = oy + sy * u, qz = sz * u, h = MathF.Sqrt(qy * qy + qz * qz) - EarthRadius; r += MathF.Exp(-h / RayleighHeight) * step; m += MathF.Exp(-h / MieHeight) * step; o += OzoneDensity(h) * step; }
            float d = 1.1f * mie * m;
            return new Radiance(SunIntensity * MathF.Exp(-(RayleighR * r + d + OzoneR * o)), SunIntensity * MathF.Exp(-(RayleighG * r + d + OzoneG * o)), SunIntensity * MathF.Exp(-(RayleighB * r + d + OzoneB * o)));
        }
        public static float Exposure(float sunAzimuth, float sunElevation, float altitudeKm, float mie) {
            float zenith = Scatter(0, 1.5f, sunAzimuth, sunElevation, altitudeKm, mie).Luma, low = Scatter(sunAzimuth, .1f, sunAzimuth, sunElevation, altitudeKm, mie).Luma;
            return MathF.Min(NightExposure, ExposureKey / MathF.Max(1e-6f, .3f * zenith + .7f * low));
        }
    }
}
