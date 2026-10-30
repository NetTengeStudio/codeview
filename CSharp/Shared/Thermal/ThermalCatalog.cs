using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace ScpItdMod.Thermal {
    public readonly struct ThermalLayer {
        public readonly float Clo, Coverage, Windproof, Waterproof;
        internal ThermalLayer(XElement e) {
            Clo = Number(e, "clo", 0, 10); Coverage = Number(e, "coverage", 0, 1);
            Windproof = Number(e, "windproof", 0, 1); Waterproof = Number(e, "waterproof", 0, 1);
        }
        internal static float Number(XElement e, string key, float min, float max) {
            if (!float.TryParse((string)e.Attribute(key), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value) || value < min || value > max)
                throw new InvalidDataException("Thermal " + e.Name + "." + key + " outside [" + min + "," + max + "]");
            return value;
        }
    }
    public sealed class ThermalGarment {
        public readonly string Identifier, Slot;
        private readonly ThermalLayer[] layers = new ThermalLayer[ThermalBody.Regions];
        public ReadOnlySpan<ThermalLayer> Layers => layers;
        internal ThermalGarment(XElement e) {
            Identifier = (string)e.Attribute("identifier"); Slot = (string)e.Attribute("slot");
            if (string.IsNullOrWhiteSpace(Identifier) || string.IsNullOrWhiteSpace(Slot)) throw new InvalidDataException("Thermal garment requires identifier and slot");
            int seen = 0;
            foreach (XElement part in e.Elements("Region")) {
                if (!Enum.TryParse((string)part.Attribute("part"), out ThermalRegion region) || (uint)region >= ThermalBody.Regions || (seen & 1 << (int)region) != 0) throw new InvalidDataException("Duplicate/invalid thermal region");
                seen |= 1 << (int)region; layers[(int)region] = new ThermalLayer(part);
            }
        }
    }
    public readonly struct ThermalSource {
        public readonly string Identifier, Mode;
        public readonly float Watts, Radius, Target, RadiantFraction, Size, Emission;
        public readonly bool Regulated;
        internal ThermalSource(XElement e) {
            Identifier = (string)e.Attribute("identifier"); Mode = (string)e.Attribute("mode");
            if (string.IsNullOrWhiteSpace(Identifier) || Mode is not ("light" or "reactor" or "powered" or "deconstructor")) throw new InvalidDataException("Invalid thermal source");
            Watts = ThermalLayer.Number(e, "watts", 0, 100000); Radius = ThermalLayer.Number(e, "radius", .1f, 20); Target = ThermalLayer.Number(e, "target", 0, 80);
            RadiantFraction = ThermalLayer.Number(e, "radiantFraction", 0, 1);
            Size = e.Attribute("size") == null ? .3f : ThermalLayer.Number(e, "size", .05f, 3);
            if (RadiantFraction > 0 && Radius < Size) throw new InvalidDataException("Thermal source radius cannot be smaller than its physical size");
            Emission = (string)e.Attribute("pattern") switch { null or "sphere" => 4, "panel" => 2, _ => throw new InvalidDataException("Invalid thermal source pattern") };
            Regulated = e.Attribute("regulated") != null && bool.Parse(e.Attribute("regulated").Value);
        }
    }
    public sealed class ThermalCatalog {
        private readonly Dictionary<string, ThermalGarment> garments = new Dictionary<string, ThermalGarment>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ThermalSource> sources = new Dictionary<string, ThermalSource>(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, ThermalGarment> Garments => garments;
        public IReadOnlyDictionary<string, ThermalSource> Sources => sources;
        public readonly float WallU, FabricCapacity, Depth, InitialIndoor, Leakage, TimeScale, RecoveryPace, PortableWatts, BatteryJoulesPerCondition;
        public readonly string PortableIdentifier;
        public readonly float Mixing, WindMixing, Buoyancy, WaterMixing;
        private ThermalCatalog(XElement root) {
            if (root.Name != "ThermalSystem" || (string)root.Attribute("version") != "1") throw new InvalidDataException("Unsupported thermal configuration");
            XElement pacing = root.Element("Pacing") ?? throw new InvalidDataException("Thermal pacing missing");
            TimeScale = ThermalLayer.Number(pacing, "timeScale", 1, 8);
            RecoveryPace = pacing.Attribute("recoveryPace") == null ? 1 : ThermalLayer.Number(pacing, "recoveryPace", 1, 12);
            XElement portable = root.Element("Portable") ?? throw new InvalidDataException("Portable heater configuration missing");
            PortableIdentifier = (string)portable.Attribute("identifier");
            if (string.IsNullOrWhiteSpace(PortableIdentifier)) throw new InvalidDataException("Portable heater identifier missing");
            PortableWatts = ThermalLayer.Number(portable, "watts", 0, 100);
            BatteryJoulesPerCondition = ThermalLayer.Number(portable, "joulesPerCondition", 1, 100000);
            XElement rooms = root.Element("Rooms") ?? throw new InvalidDataException("Thermal rooms missing");
            WallU = ThermalLayer.Number(rooms, "wallU", .01f, 2.5f); FabricCapacity = ThermalLayer.Number(rooms, "fabricCapacity", 1000, 500000);
            Depth = ThermalLayer.Number(rooms, "depth", .5f, 10); InitialIndoor = ThermalLayer.Number(rooms, "initialIndoor", 0, 30); Leakage = ThermalLayer.Number(rooms, "airChangesPerHour", 0, 10);
            XElement field = root.Element("Field") ?? throw new InvalidDataException("Thermal field missing");
            Mixing = ThermalLayer.Number(field, "mixing", .001f, 1); WindMixing = ThermalLayer.Number(field, "windMixing", 0, 1);
            Buoyancy = ThermalLayer.Number(field, "buoyancy", 0, 1); WaterMixing = ThermalLayer.Number(field, "waterMixing", 0, .1f);
            XElement garmentRoot = root.Element("Garments") ?? throw new InvalidDataException("Thermal garments missing");
            XElement sourceRoot = root.Element("Sources") ?? throw new InvalidDataException("Thermal sources missing");
            foreach (XElement e in garmentRoot.Elements("Garment")) { var g = new ThermalGarment(e); if (!garments.TryAdd(g.Identifier, g)) throw new InvalidDataException("Duplicate thermal garment " + g.Identifier); }
            foreach (XElement e in sourceRoot.Elements("Source")) { var source = new ThermalSource(e); if (!sources.TryAdd(source.Identifier, source)) throw new InvalidDataException("Duplicate thermal source " + source.Identifier); }
        }
        public static ThermalCatalog Load(string file) {
            using var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return new ThermalCatalog(XDocument.Load(reader).Root);
        }
    }
}
