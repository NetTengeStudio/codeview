using System;

namespace ScpItdMod.Thermal {
    public enum ThermalRegion : byte { Head, Trunk, Arms, Hands, Legs, Feet }

    public struct ThermalContact {
        public float Head, Trunk, Arms, Hands, Legs, Feet;
        public readonly float For(int region) => region switch {
            0 => Head, 1 => Trunk, 2 => Arms, 3 => Hands, 4 => Legs, 5 => Feet,
            _ => throw new ArgumentOutOfRangeException(nameof(region))
        };
        public readonly double TotalWatts => (double)Head + Trunk + Arms + Hands + Legs + Feet;
        public void Add(ThermalRegion region, float watts) {
            switch (region) {
                case ThermalRegion.Head: Head += watts; break;
                case ThermalRegion.Trunk: Trunk += watts; break;
                case ThermalRegion.Arms: Arms += watts; break;
                case ThermalRegion.Hands: Hands += watts; break;
                case ThermalRegion.Legs: Legs += watts; break;
                case ThermalRegion.Feet: Feet += watts; break;
                default: throw new ArgumentOutOfRangeException(nameof(region));
            }
        }
        public void Add(in ThermalContact other) {
            Head += other.Head; Trunk += other.Trunk; Arms += other.Arms;
            Hands += other.Hands; Legs += other.Legs; Feet += other.Feet;
        }
    }

    public struct ThermalBalance {
        public double MetabolicWatts, ContactWatts, ConvectiveWatts, RadiativeWatts, SourceRadiationWatts;
        public double ConductiveWatts, EvaporativeWatts, InsensibleWatts, RespiratoryWatts, CoreToShellWatts;
        public double NetStoredWatts, ResidualWatts, PacingWatts;
    }

    public struct ThermalEnvironment {
        public float Air, Radiant, Water, Wind, Humidity, Immersion, Activity, Perfusion, Oxygen, RecoveryPace;
        public ThermalContact Contact;
        public ThermalContact Irradiance;
    }

    public struct ThermalProtection {
        public float Clo, Coverage, Windproof, Waterproof, Wetness;
        public void Add(float clo, float coverage, float windproof, float waterproof, float wetness, float condition) {
            float intact = .2f + .8f * Math.Clamp(condition, 0, 1);
            coverage = Math.Clamp(coverage, 0, 1);
            Clo += clo * coverage * intact * (1 - .7f * wetness);
            Coverage = 1 - (1 - Coverage) * (1 - coverage);
            Windproof = 1 - (1 - Windproof) * (1 - windproof * coverage * intact);
            Waterproof = 1 - (1 - Waterproof) * (1 - waterproof * coverage * intact);
            Wetness = MathF.Max(Wetness, wetness * coverage);
        }
    }

    public struct ThermalTissue {
        public double Enthalpy;
        public float Temperature, Frozen;
    }

    public sealed class ThermalBody {
        public const int Regions = 6;
        public const double CoreCapacity = 218000, ShellCapacity = 24900;
        public const float Area = 1.8f, ReserveJoules = 180000, MaxFlux = 4000, Absorbed = .29f;
        private readonly ThermalTissue[] tissue = new ThermalTissue[Regions];
        private readonly float[] injury = new float[6];
        private readonly ThermalTraumaState[] trauma = new ThermalTraumaState[6];
        private readonly float[] circulation = new float[Regions], coldExposure = new float[Regions];
        private float previousImmersion, previousSkin = float.MinValue, skinTrend, warmDrive, ava;
        public ReadOnlySpan<ThermalTraumaState> Trauma => trauma;
        public ReadOnlySpan<float> Circulation => circulation;
        public float Dexterity => MathF.Min(trauma[2].NerveFunction, trauma[3].NerveFunction);
        public float Gait => MathF.Min(trauma[4].NerveFunction, trauma[5].NerveFunction);
        public float Pain { get; private set; }
        public float HeatStress { get; private set; }
        public float MeanSkin { get; private set; } = 32.5f;
        public float Sweat { get; private set; }
        public float WetCold { get; private set; }
        public float ColdShock { get; private set; }
        public ReadOnlySpan<ThermalTissue> Tissue => tissue;
        public ReadOnlySpan<float> Injury => injury;
        private double coreTemperature = 37;
        public float Core => (float)coreTemperature;
        public float Reserve { get; private set; } = 1;
        public float Shivering { get; private set; }
        public float Wetness { get; private set; }
        public double NetWatts { get; private set; }
        public double EnvironmentWatts { get; private set; }
        public ThermalBalance LastBalance { get; private set; }
        public double Energy => coreTemperature * CoreCapacity + TissueEnergy();
        public float ColdStrength => Math.Clamp((35 - Core) * (100f / 10), 0, 100);
        public float ChilledSkin {
            get {
                float skin = MathF.Min(Tissue[0].Temperature, MathF.Min(Tissue[3].Temperature, Tissue[5].Temperature));
                return Math.Clamp(MathF.Max((24 - skin) * 2, (36 - Core) * 12), 0, 100);
            }
        }
        private static readonly float[] AvaMax = { 18f, 0f, 4f, 50f, 0f, 45f };
        private static double Conductance(int i, float vascular, float shunt, float perfusion) =>
            Area * Share(i) * (5.28 + 12 * vascular * (i == 3 || i == 5 ? .55 : 1) + AvaMax[i] * shunt) * (.35 + .65 * perfusion);
        private static readonly float[] VasoGain = { .45f, .25f, .45f, 1f, .45f, 1f };
        public static float Share(int i) => i switch { 0 => .08f, 1 => .36f, 2 => .14f, 3 => .05f, 4 => .29f, 5 => .08f, _ => throw new ArgumentOutOfRangeException(nameof(i)) };
        public ThermalBody() {
            for (int i = 0; i < Regions; i++) {
                float t = i == 3 || i == 5 ? 30 : 33;
                circulation[i] = 1; trauma[i] = ThermalTraumaState.Healthy;
                tissue[i] = new ThermalTissue { Temperature = t, Enthalpy = ShellCapacity * Share(i) * t };
            }
        }
        private double TissueEnergy() { double e = 0; for (int i = 0; i < Regions; i++) e += Tissue[i].Enthalpy; return e; }
        private static double Latent(int i) => Area * Share(i) * .1 * .65 * 334000;
        private static double Enthalpy(double temperature, int i) => ShellCapacity * Share(i) * temperature + Latent(i) * Math.Clamp(temperature / 2, -1, 0);
        private static float Temperature(double energy, int i) {
            double c = ShellCapacity * Share(i), latent = Latent(i);
            return (float)(energy >= 0 ? energy / c : energy >= -2 * c - latent ? energy / (c + latent / 2) : (energy + latent) / c);
        }

        public void Step(in ThermalEnvironment e, ReadOnlySpan<ThermalProtection> protection, float dt) {
            if (!float.IsFinite(dt) || dt <= 0 || dt > 1 || protection.Length != Regions ||
                !float.IsFinite(e.Air + e.Radiant + e.Water + e.Wind + e.Humidity + e.Immersion + e.Activity + e.Perfusion + e.Oxygen + e.RecoveryPace + e.Irradiance.Head + e.Irradiance.Trunk + e.Irradiance.Arms + e.Irradiance.Hands + e.Irradiance.Legs + e.Irradiance.Feet + e.Contact.Head + e.Contact.Trunk + e.Contact.Arms + e.Contact.Hands + e.Contact.Legs + e.Contact.Feet) ||
                e.Air < ThermalLimits.MinimumAmbient || e.Air > ThermalLimits.MaximumAmbient || e.Radiant < -120 || e.Radiant > ThermalLimits.MaximumAmbient || e.Water < ThermalLimits.MinimumAmbient || e.Water > 100 ||
                e.Wind < 0 || e.Wind > 200 || e.Immersion < 0 || e.Immersion > 1 || e.Activity < 0 || e.Activity > 2 ||
                e.Perfusion < 0 || e.Perfusion > 1 || e.Oxygen < 0 || e.Oxygen > 1 || e.RecoveryPace < 0 || e.RecoveryPace > 12 || e.Humidity < 0 || e.Humidity > 1 || e.Irradiance.Head < 0 || e.Irradiance.Trunk < 0 || e.Irradiance.Arms < 0 || e.Irradiance.Hands < 0 || e.Irradiance.Legs < 0 || e.Irradiance.Feet < 0 || MaxIrradiance(e.Irradiance) > MaxFlux ||
                e.Contact.Head < 0 || e.Contact.Trunk < 0 || e.Contact.Arms < 0 || e.Contact.Hands < 0 || e.Contact.Legs < 0 || e.Contact.Feet < 0 || e.Contact.TotalWatts > 500)
                throw new ArgumentOutOfRangeException(nameof(e));
            for (int i = 0; i < Regions; i++) {
                ThermalProtection p = protection[i];
                if (!float.IsFinite(p.Clo + p.Coverage + p.Windproof + p.Waterproof + p.Wetness) || p.Clo < 0 || p.Clo > 15 ||
                    p.Coverage < 0 || p.Coverage > 1 || p.Windproof < 0 || p.Windproof > 1 || p.Waterproof < 0 || p.Waterproof > 1 || p.Wetness < 0 || p.Wetness > 1)
                    throw new ArgumentOutOfRangeException(nameof(protection));
            }
            double before = Energy;
            ColdShock = MathF.Max(ColdShock * MathF.Exp(-dt / 60), MathF.Max(0, e.Immersion - previousImmersion) * Math.Clamp((15 - e.Water) / 15, 0, 1));
            previousImmersion = e.Immersion;
            float meanSkin = 0;
            for (int i = 0; i < Regions; i++) meanSkin += tissue[i].Temperature * Share(i);
            if (previousSkin == float.MinValue) previousSkin = meanSkin;
            skinTrend += ((meanSkin - previousSkin) * 60 / dt - skinTrend) * (1 - MathF.Exp(-dt / 40)); previousSkin = meanSkin;
            MeanSkin = meanSkin;
            float constrict = 1 - (1 - Math.Clamp((36.9f - Core) / 1.2f, 0, 1)) * (1 - .85f * Math.Clamp((33 - meanSkin) / 9, 0, 1));
            float dilate = MathF.Max(Math.Clamp((Core - 37.2f) / .8f, 0, 1), .6f * Math.Clamp((meanSkin - 35) / 4, 0, 1));
            warmDrive = MathF.Max(warmDrive * MathF.Exp(-dt / 150), Math.Clamp(skinTrend / .6f, 0, 1));
            if (Core > 34.5f) dilate = MathF.Max(dilate, .8f * warmDrive);
            constrict *= 1 - .45f * MathF.Min(1, e.Activity);
            float shuntTarget = Core > 34.5f ? dilate : 0;
            ava += (shuntTarget - ava) * (1 - MathF.Exp(-dt / (shuntTarget < ava ? 20 : 30)));
            for (int i = 0; i < Regions; i++) {
                float local = tissue[i].Temperature;
                coldExposure[i] = local > 0 && local < 15 ? coldExposure[i] + dt : 0;
                float flow = MathF.Max(1 - VasoGain[i] * constrict, dilate);
                float localLimit = MathF.Max(Math.Clamp((local - 10) / 23, 0, 1), Core > 34.5f ? .8f * warmDrive * Math.Clamp((local + 2) / 8, 0, 1) : 0);
                float target = Math.Clamp(flow * localLimit * Math.Clamp((Core - 28) / 7, 0, 1), 0, 1);
                if ((i == 3 || i == 5) && Core > 35 && e.Perfusion > .5f && coldExposure[i] >= 300) {
                    float phase = (coldExposure[i] - 300) % 600;
                    if (phase < 90) target = MathF.Max(target, .35f * MathF.Sin(MathF.PI * phase / 90));
                }
                float tau = target < circulation[i] ? 20 : 25;
                circulation[i] += (target - circulation[i]) * (1 - MathF.Exp(-dt / tau));
            }
            float skin = 0; Wetness = 0;
            for (int i = 0; i < Regions; i++) { skin += Tissue[i].Temperature * Share(i); Wetness += protection[i].Wetness * Share(i); }
            float coldDrive = Math.Clamp((36.8f - Core) / 1.5f + (30 - skin) / 25, 0, 1);
            float consciousHeat = Math.Clamp((Core - 28) / 5, 0, 1);
            Shivering = coldDrive * Math.Clamp((37.1f - Core) / .5f, 0, 1) * consciousHeat * MathF.Sqrt(Reserve) * e.Oxygen;
            float shiverWatts = 200 * Shivering;
            Reserve = Math.Clamp(Reserve + (e.Air > 15 && e.Activity < .2f ? .0006f : 0) * dt - shiverWatts * dt / ReserveJoules, 0, 1);
            float basal = 100 * Math.Clamp((Core - 20) / 15, .2f, 1);
            float work = 120 * e.Activity * consciousHeat;
            double metabolism = (basal + work) * e.Oxygen + shiverWatts;
            double respiratory = MathF.Max(0, .0014f * (float)metabolism * (34 - e.Air)) + .0173 * metabolism * Math.Max(0, 5.87 - e.Humidity * Saturation(e.Air));
            respiratory += 30 * ColdShock;
            double evaporative = Math.Clamp((Core - 37.05f) * 350 + MathF.Max(0, meanSkin - 34.5f) * 55, 0, 450);
            Sweat = (float)(evaporative / 450);
            HeatStress = Math.Clamp(MathF.Max((Core - 38.2f) / 1.8f, (meanSkin - 38) / 6), 0, 1);
            Span<double> coupling = stackalloc double[Regions];
            Span<double> diagonal = stackalloc double[Regions];
            Span<double> rhs = stackalloc double[Regions];
            Span<double> guess = stackalloc double[Regions];
            double coreOld = coreTemperature;
            double pace = e.RecoveryPace > 1 && NetWatts > 0 && coreTemperature < 36.6 ? e.RecoveryPace : 1;
            for (int i = 0; i < Regions; i++) guess[i] = Tissue[i].Temperature;
            double coreNew = coreOld;
            for (int iteration = 0; iteration < 8; iteration++) {
                double coreDiagonal = CoreCapacity / pace / dt, coreRhs = CoreCapacity / pace / dt * coreOld + metabolism - respiratory;
                for (int i = 0; i < Regions; i++) {
                    double area = Area * Share(i), t = guess[i]; ThermalProtection p = protection[i];
                    float localWater = Immersion(i, e.Immersion);
                    double localWind = e.Wind * (1 - .85 * p.Windproof);
                    double hConv = Math.Max(3.1, 8.3 * Math.Pow(Math.Max(.05, localWind), .6));
                    double hr = 4 * .97 * 5.670374419e-8 * Math.Pow((Math.Clamp(t, -80, 60) + e.Radiant) * .5 + 273.15, 3);
                    double airR = 1 / (hConv + hr), clothing = .155 * p.Clo;
                    double dry = (1 - p.Coverage) / airR + p.Coverage / (airR + clothing / Math.Max(.01, p.Coverage));
                    double water = (1 - p.Coverage) * 120 + p.Coverage / (1.0 / 120 + clothing * .25 / Math.Max(.01, p.Coverage));
                    double loss = area * ((1 - localWater) * dry + localWater * water);
                    double operative = (hConv * e.Air + hr * e.Radiant) / (hConv + hr);
                    double ambientFlux = area * ((1 - localWater) * dry * operative + localWater * water * e.Water);
                    double vascular = circulation[i];
                    double conductance = Conductance(i, (float)vascular, ava, e.Perfusion);
                    coupling[i] = conductance;
                    double capacity = ShellCapacity * Share(i) + (t < 0 && t > -2 ? Latent(i) / 2 : 0);
                    double intercept = t <= -2 ? -Latent(i) : 0;
                    diagonal[i] = capacity / dt + conductance + loss;
                    double insensible = 12 * Share(i);
                    double vapourGradient = Math.Max(0, Saturation((float)Math.Clamp(t, 0, 60)) - e.Humidity * Saturation(e.Air));
                    double vapourClothing = clothing / (16.5 * .38 * (1 - .95 * p.Waterproof));
                    double vapourConductance = (1 - p.Coverage) * 16.5 * hConv + p.Coverage / (1 / (16.5 * hConv) + vapourClothing / Math.Max(.01, p.Coverage));
                    double evaporation = Math.Min(area * vapourGradient * vapourConductance, evaporative * Share(i) + area * p.Wetness * 80);
                    rhs[i] = (Tissue[i].Enthalpy - intercept) / dt + ambientFlux + SourceHeat(area, e.Irradiance.For(i), p, airR, clothing) * (1 - localWater) - (evaporation + insensible) * (1 - localWater);
                    rhs[i] += e.Contact.For(i);
                    coreDiagonal -= conductance * conductance / diagonal[i];
                    coreRhs += conductance * rhs[i] / diagonal[i];
                    coreDiagonal += conductance;
                }
                coreNew = coreRhs / coreDiagonal;
                double error = 0;
                for (int i = 0; i < Regions; i++) {
                    double value = (rhs[i] + coupling[i] * coreNew) / diagonal[i];
                    error = Math.Max(error, Math.Abs(value - guess[i])); guess[i] = value;
                }
                if (iteration > 0 && error < 1e-7) break;
            }
            coreTemperature = coreNew;
            for (int i = 0; i < Regions; i++) {
                ref ThermalTissue region = ref tissue[i];
                region.Enthalpy = Enthalpy(guess[i], i);
                region.Temperature = Temperature(region.Enthalpy, i);
                region.Frozen = Math.Clamp(-region.Temperature / 2, 0, 1);
            }
            Pain = WetCold = 0;
            for (int site = 0; site < injury.Length; site++) {
                int region = site < 2 ? site : site < 4 ? 3 : 5;
                float frozen = Tissue[region].Frozen, temperature = Tissue[region].Temperature;
                if (region == 3 || region == 5) { frozen = MathF.Max(frozen, Tissue[region - 1].Frozen); temperature = MathF.Min(temperature, Tissue[region - 1].Temperature); }
                ThermalTrauma.Step(ref trauma[site], ref injury[site], temperature, frozen, protection[region].Wetness, circulation[region] * e.Perfusion, dt);
                Pain = MathF.Max(Pain, trauma[site].Pain);
                WetCold = MathF.Max(WetCold, Math.Clamp(trauma[site].WetColdSeconds / ThermalTrauma.WetColdLatency, 0, 1));
            }
            float hotPain = 0;
            for (int i = 0; i < Regions; i++) hotPain = MathF.Max(hotPain, Math.Clamp((tissue[i].Temperature - 42) / 5, 0, 1));
            Pain = MathF.Max(Pain, hotPain);
            NetWatts = (Energy - before) / dt;
            EnvironmentWatts = metabolism + e.Contact.TotalWatts - NetWatts;
            ThermalBalance measured = MeasureBalance(e, protection, metabolism, respiratory, evaporative, NetWatts);
            measured.PacingWatts = (coreTemperature - coreOld) * CoreCapacity / dt * (1 - 1 / pace);
            measured.ResidualWatts += measured.PacingWatts;
            LastBalance = measured;
        }
        private static double SourceHeat(double area, float incident, in ThermalProtection p, double airResistance, double clothing) {
            if (incident <= 0) return 0;
            double q = incident * Absorbed * area, coverage = p.Coverage;
            double covered = coverage <= .01 ? 0 : coverage * airResistance / (airResistance + clothing / coverage);
            return q * ((1 - coverage) + covered);
        }
        private static float MaxIrradiance(in ThermalContact c) => MathF.Max(MathF.Max(c.Head, c.Trunk), MathF.Max(MathF.Max(c.Arms, c.Hands), MathF.Max(c.Legs, c.Feet)));
        private ThermalBalance MeasureBalance(in ThermalEnvironment e, ReadOnlySpan<ThermalProtection> protection, double metabolism, double respiratory, double evaporative, double storedWatts) {
            var balance = new ThermalBalance {
                MetabolicWatts = metabolism,
                ContactWatts = e.Contact.TotalWatts,
                RespiratoryWatts = respiratory,
                NetStoredWatts = storedWatts
            };
            double perfusion = 0;
            for (int i = 0; i < Regions; i++) {
                double area = Area * Share(i), temperature = tissue[i].Temperature;
                ThermalProtection p = protection[i];
                float localWater = Immersion(i, e.Immersion);
                double localWind = e.Wind * (1 - .85 * p.Windproof);
                double hConv = Math.Max(3.1, 8.3 * Math.Pow(Math.Max(.05, localWind), .6));
                double hRad = 4 * .97 * 5.670374419e-8 * Math.Pow((Math.Clamp(temperature, -80, 60) + e.Radiant) * .5 + 273.15, 3);
                double airResistance = 1 / (hConv + hRad), clothing = .155 * p.Clo;
                double dry = (1 - p.Coverage) / airResistance + p.Coverage / (airResistance + clothing / Math.Max(.01, p.Coverage));
                double water = (1 - p.Coverage) * 120 + p.Coverage / (1.0 / 120 + clothing * .25 / Math.Max(.01, p.Coverage));
                balance.ConvectiveWatts += area * dry * hConv / (hConv + hRad) * (e.Air - temperature);
                balance.RadiativeWatts += area * dry * hRad / (hConv + hRad) * (e.Radiant - temperature);
                balance.SourceRadiationWatts += SourceHeat(area, e.Irradiance.For(i), p, airResistance, clothing) * (1 - localWater);
                balance.ConductiveWatts += area * localWater * water * (e.Water - temperature);
                double vapourGradient = Math.Max(0, Saturation((float)Math.Clamp(temperature, 0, 60)) - e.Humidity * Saturation(e.Air));
                double clothingResistance = clothing / (16.5 * .38 * (1 - .95 * p.Waterproof));
                double vapourConductance = (1 - p.Coverage) * 16.5 * hConv + p.Coverage / (1 / (16.5 * hConv) + clothingResistance / Math.Max(.01, p.Coverage));
                double skinEvaporation = Math.Min(area * vapourGradient * vapourConductance, evaporative * Share(i) + area * p.Wetness * 80);
                balance.EvaporativeWatts += skinEvaporation * (1 - localWater);
                balance.InsensibleWatts += 12 * Share(i) * (1 - localWater);
                double conductance = Conductance(i, circulation[i], ava, e.Perfusion);
                perfusion += conductance * (coreTemperature - temperature);
            }
            balance.CoreToShellWatts = perfusion;
            balance.ResidualWatts = balance.MetabolicWatts + balance.ContactWatts + balance.ConvectiveWatts + balance.RadiativeWatts + balance.SourceRadiationWatts + balance.ConductiveWatts - balance.EvaporativeWatts - balance.InsensibleWatts - balance.RespiratoryWatts - balance.NetStoredWatts;
            return balance;
        }
        public void Treat(int site, float reduction) {
            if ((uint)site >= injury.Length || !float.IsFinite(reduction) || reduction < 0 || reduction > 1) throw new ArgumentOutOfRangeException(nameof(reduction));
            injury[site] = MathF.Max(0, injury[site] - reduction);
        }
        public static float Immersion(int region, float fraction) => region switch {
            5 => Math.Clamp(fraction / .18f, 0, 1), 4 => Math.Clamp((fraction - .15f) / .4f, 0, 1),
            3 => Math.Clamp((fraction - .35f) / .4f, 0, 1), 1 or 2 => Math.Clamp((fraction - .4f) / .5f, 0, 1),
            _ => Math.Clamp((fraction - .85f) / .15f, 0, 1)
        };
        public static float Saturation(float temperature) => .61078f * MathF.Exp(17.27f * temperature / (temperature + 237.3f));
        public static float Moisture(float wetness, float immersion, float waterproof, float temperature, float humidity, float wind, float dt, float irradiance = 0) {
            float soak = immersion * (1 - .98f * waterproof) / 15;
            float dry = MathF.Max(0, Saturation(MathF.Max(0, temperature)) * (1 - humidity)) * (1 + MathF.Sqrt(MathF.Max(0, wind))) * (1 + MathF.Max(0, irradiance) / 25) / 10800;
            float rate = soak + dry;
            return rate <= 0 ? wetness : Math.Clamp(soak / rate + (wetness - soak / rate) * MathF.Exp(-rate * dt), 0, 1);
        }
    }
}
