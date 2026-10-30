using System;
using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace ScpItdMod.Weather {
    public sealed class WeatherProfile {
        public readonly WeatherState State;
        public readonly float Minimum, Maximum;
        public readonly float[] Transitions;
        internal WeatherProfile(XElement e) {
            WeatherRegime id = Enum.Parse<WeatherRegime>((string)e.Attribute("id"), false);
            State = new WeatherState { Regime = id, Next = id, Temperature = Number(e, "temperature", -80, -15), WindSpeed = Number(e, "wind", 0, 60),
                WindDirection = Number(e, "direction", 0, 360) * (MathF.PI / 180), Gustiness = Number(e, "gustiness", 0, 1),
                Snowfall = Number(e, "snowfall", 0, 1), Drifting = Number(e, "drifting", 0, 1), Visibility = Number(e, "visibility", .25f, 100000),
                Cloud = Number(e, "cloud", 0, 1), Humidity = Number(e, "humidity", 0, 1), Frost = Number(e, "frost", 0, 1), Pressure = Number(e, "pressure", 500, 1100) };
            Minimum = Number(e, "minimum", 10, 86400); Maximum = Number(e, "maximum", Minimum, 86400);
            Transitions = new float[6]; float sum = 0;
            foreach (XElement edge in e.Elements("To")) {
                int target = (int)Enum.Parse<WeatherRegime>((string)edge.Attribute("regime"), false);
                if (target == (int)id || Transitions[target] != 0) throw new InvalidDataException("Self or duplicate weather transition");
                float weight = Number(edge, "weight", .0001f, 1); Transitions[target] = weight; sum += weight;
            }
            if (MathF.Abs(sum - 1) > .0001f) throw new InvalidDataException("Weather transition row must sum to one: " + id);
        }
        internal static float Number(XElement e, string name, float min, float max) {
            if (!float.TryParse((string)e.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out float n) || !float.IsFinite(n) || n < min || n > max)
                throw new InvalidDataException(e.Name + "." + name + " outside [" + min + "," + max + "]");
            return n;
        }
    }

    public readonly struct WeatherSchedule {
        public readonly double At;
        public readonly WeatherRegime Regime;
        public readonly float Blend;
        internal WeatherSchedule(XElement e) { At = WeatherProfile.Number(e, "at", 0, 604800); Blend = WeatherProfile.Number(e, "blend", 0, 3600); Regime = Enum.Parse<WeatherRegime>((string)e.Attribute("regime"), false); }
    }

    public sealed class WeatherConfig {
        public readonly WeatherProfile[] Profiles = new WeatherProfile[6];
        public readonly WeatherSchedule[] Schedule;
        public readonly string[] Maps;
        public readonly string Map, WindSound, GustSound, SirenSound, WhistleSound;
        public readonly float Moonlight, Altitude, Latitude, DayOfYear, MinutesPerDay, StartHour, BlendSeconds, WarningSeconds;
        public readonly bool LockTime, SnowSupport;
        public readonly float CellMetres, MarginMetres, SnowRate, InitialDepth, InitialDensity, MaximumDepth, CompactionRate, TransportRate, MeltRate, WindThreshold, CarryDepth, Repose, SnowFallSpeed, SpinUpSeconds, SpinUpWind;
        public readonly float IndoorTemperature, StormWind, FlightVisibility, DoorDepth, DeepSnow;
        public readonly float WindGain, GustGain, SirenGain, WhistleGain, IndoorTransmission, MuffleBelowExposure;
        public readonly int Particles, MaskWidth, MaximumCells;
        public readonly PolarSeason Season;
        public readonly WeatherRegime StartRegime;
        public readonly ulong Fingerprint;
        // The attribute lists every submarine the weather belongs to, separated by "|": a copy of the station under another name keeps its weather. "*" means every submarine.
        public bool Applies(string submarine) => Array.Exists(Maps, m => m == "*" || string.Equals(m, submarine, StringComparison.OrdinalIgnoreCase));
        private WeatherConfig(XElement root) {
            Map = (string)root.Attribute("map");
            Maps = Map.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            XElement cycle = root.Element("Cycle"), snow = root.Element("Snow"), play = root.Element("Gameplay"), quality = root.Element("Quality"), audio = root.Element("Audio");
            Latitude = N(cycle, "latitude", -89.9f, -66.6f); Altitude = cycle.Attribute("altitude") == null ? 0 : N(cycle, "altitude", 0, 5000); DayOfYear = N(cycle, "dayOfYear", 1, 366); MinutesPerDay = N(cycle, "minutesPerDay", 1, 10080);
            Moonlight = cycle.Attribute("moonlight") == null ? .03f : N(cycle, "moonlight", .03f, .4f);
            StartRegime = root.Attribute("startRegime") == null ? WeatherRegime.Clear : Enum.Parse<WeatherRegime>((string)root.Attribute("startRegime"), false);
            LockTime = (string)cycle.Attribute("lockTime") == "true";
            StartHour = N(cycle, "startHour", 0, 24); Season = Enum.Parse<PolarSeason>((string)cycle.Attribute("season"), false);
            BlendSeconds = N(root, "blendSeconds", 0, 3600); WarningSeconds = N(root, "warningSeconds", 0, 3600);
            CellMetres = N(snow, "cellMetres", .08f, 2); MarginMetres = N(snow, "marginMetres", 10, 200);
            SnowRate = N(snow, "depositionMetresPerSecond", 0, .01f); InitialDepth = N(snow, "initialDepth", 0, 5); MaximumDepth = N(snow, "maximumDepth", InitialDepth, 5);
            InitialDensity = snow.Attribute("initialDensity") == null ? SnowPhysics.FreshDensity : N(snow, "initialDensity", SnowPhysics.FreshDensity, SnowPhysics.PackedDensity);
            CompactionRate = N(snow, "compactionPerSecond", 0, 1); TransportRate = N(snow, "transportPerSecond", 0, 100); MeltRate = N(snow, "meltMetresPerSecond", 0, .1f);
            WindThreshold = N(snow, "windThreshold", 1, 15); CarryDepth = N(snow, "carryDepth", .000001f, 1); Repose = N(snow, "reposeSlope", .2f, 1.5f);
            SnowFallSpeed = snow.Attribute("fallSpeedMetresPerSecond") == null ? .8f : N(snow, "fallSpeedMetresPerSecond", .1f, 5);
            SpinUpSeconds = snow.Attribute("spinUpSeconds") == null ? 0 : N(snow, "spinUpSeconds", 0, 3600); SpinUpWind = snow.Attribute("spinUpWind") == null ? 0 : N(snow, "spinUpWind", 0, 40);
            IndoorTemperature = N(play, "indoorTemperature", 0, 30);
            StormWind = N(play, "flightWindLimit", 5, 50); FlightVisibility = N(play, "flightVisibility", 5, 5000);
            DoorDepth = N(play, "blockedDoorDepth", .1f, 2); DeepSnow = N(play, "deepSnowDepth", .1f, 2); SnowSupport = (string)play.Attribute("snowSupport") != "false";
            Particles = Integer(quality, "particles", 128, 16384); MaskWidth = Integer(quality, "maskWidth", 64, 1024); MaximumCells = Integer(quality, "maximumCells", 65536, 16000000);
            WindSound = Asset(audio, "wind"); GustSound = Asset(audio, "gust"); SirenSound = Asset(audio, "siren"); WhistleSound = Asset(audio, "whistle");
            WindGain = N(audio, "windGain", 0, 1); GustGain = N(audio, "gustGain", 0, 1); SirenGain = N(audio, "sirenGain", 0, 1); WhistleGain = N(audio, "whistleGain", 0, 1);
            IndoorTransmission = N(audio, "indoorTransmission", 0, 1); MuffleBelowExposure = N(audio, "muffleBelowExposure", 0, 1);
            int count = 0;
            foreach (XElement e in root.Elements("Profile")) {
                var p = new WeatherProfile(e); int id = (int)p.State.Regime;
                if (Profiles[id] != null) throw new InvalidDataException("Duplicate weather profile"); Profiles[id] = p; count++;
            }
            if (count != 6) throw new InvalidDataException("Six weather profiles are required");
            for (int start = 0; start < 6; start++) {
                int reached = 1 << start;
                for (int pass = 0; pass < 6; pass++) for (int i = 0; i < 6; i++) if ((reached & 1 << i) != 0)
                    for (int j = 0; j < 6; j++) if (Profiles[i].Transitions[j] > 0) reached |= 1 << j;
                if (reached != 63) throw new InvalidDataException("Disconnected weather transition graph");
            }
            var entries = root.Element("Schedule").Elements("Change"); int length = 0; foreach (var e in entries) length++;
            Schedule = new WeatherSchedule[length]; int s = 0; double last = -1;
            foreach (XElement e in entries) { var entry = new WeatherSchedule(e); if (entry.At <= last) throw new InvalidDataException("Schedule must be ordered"); Schedule[s++] = entry; last = entry.At; }
            ulong hash = 14695981039346656037;
            foreach (char c in root.ToString(SaveOptions.DisableFormatting)) hash = (hash ^ c) * 1099511628211;
            Fingerprint = hash;
        }
        private static float N(XElement e, string name, float min, float max) => WeatherProfile.Number(e, name, min, max);
        private static int Integer(XElement e, string name, int min, int max) { float x = N(e, name, min, max); if (x != MathF.Truncate(x)) throw new InvalidDataException(name + " must be integral"); return (int)x; }
        private static string Asset(XElement e, string name) { string p = (string)e.Attribute(name); if (string.IsNullOrWhiteSpace(p) || Path.IsPathRooted(p) || p.Contains("..") || !p.StartsWith("Content/Weather/", StringComparison.Ordinal)) throw new InvalidDataException("Invalid weather asset"); return p; }
        public static WeatherConfig Load(string xml, string schema) {
            var schemas = new XmlSchemaSet { XmlResolver = null };
            using (var sr = XmlReader.Create(schema, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) schemas.Add(null, sr);
            XDocument doc;
            using (var xr = XmlReader.Create(xml, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) doc = XDocument.Load(xr);
            doc.Validate(schemas, (sender, error) => throw new InvalidDataException(error.Message));
            return new WeatherConfig(doc.Root);
        }
    }
}
