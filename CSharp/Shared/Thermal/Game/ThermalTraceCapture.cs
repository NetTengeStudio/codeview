using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace ScpItdMod.Thermal {
    internal sealed class ThermalTraceCapture : IDisposable {
        private const long MaximumBytes = 64L * 1024 * 1024;
        private readonly StreamWriter writer;
        private readonly StringBuilder row = new StringBuilder(768);
        private long bytes;
        private double nextFlush;
        private bool disposed;
        private bool hasField;
        internal string Path { get; }
        internal string Failure { get; private set; }
        internal bool Active => !disposed && Failure == null;

        private ThermalTraceCapture(string path, string mapName) {
            Path = path;
            writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false), 16 * 1024);
            WriteHeader(mapName);
        }

        internal static ThermalTraceCapture Start(string mapName) {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root)) throw new IOException("LocalApplicationData path is unavailable");
            string directory = System.IO.Path.Combine(root, "ScpItd", "ThermalCaptures");
            Directory.CreateDirectory(directory);
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string path = System.IO.Path.Combine(directory, "thermal-" + stamp + "-" + Guid.NewGuid().ToString("N") + ".tsv");
            return new ThermalTraceCapture(path, mapName);
        }

        private void WriteHeader(string mapName) => Write("SCPITD-THERMAL\t2\t" + Convert.ToBase64String(Encoding.UTF8.GetBytes(mapName ?? string.Empty)) + "\t" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));

        internal void Character(double time, ushort id, float x, float y, int room, in ThermalBody body, in ThermalEnvironment environment) {
            ThermalBalance b = body.LastBalance;
            Begin('C');
            Add("C"); Add(time); Add(id); Add(x); Add(y); Add(room); Add(body.Core); Add(MinSkin(body)); Add(environment.Air); Add(environment.Wind);
            Add(body.Wetness); Add(environment.Activity); Add(body.Shivering); Add(b.MetabolicWatts);
            Add(environment.Contact.Head); Add(environment.Contact.Trunk); Add(environment.Contact.Arms); Add(environment.Contact.Hands); Add(environment.Contact.Legs); Add(environment.Contact.Feet);
            Add(b.ConvectiveWatts); Add(b.RadiativeWatts); Add(b.SourceRadiationWatts); Add(b.ConductiveWatts); Add(b.EvaporativeWatts); Add(b.InsensibleWatts);
            Add(b.RespiratoryWatts); Add(b.CoreToShellWatts); Add(b.NetStoredWatts); Add(b.ResidualWatts); Add(body.ColdStrength); Add(body.Reserve); Add(MaxInjury(body));
            Write(row.ToString());
        }

        internal void Room(double time, int index, float x, float y, in ThermalRoom room, bool sheltered) {
            Begin('R'); Add(time); Add(index); Add(x); Add(y); Add(room.Temperature); Add(room.SurfaceTemperature);
            Add(room.Capacity); Add(room.FabricCapacity); Add(room.OutsideConductance); Add(room.FabricConductance); Add(room.FabricOutsideConductance); Add(room.Heat); Add(sheltered ? 1 : 0);
            Write(row.ToString());
        }

        internal void Link(double time, int index, in ThermalLink link) {
            Begin('L'); Add(time); Add(index); Add(link.A); Add(link.B); Add(link.Fabric ? 1 : 0); Add(link.Conductance); Write(row.ToString());
        }

        internal void Source(double time, int itemId, string prefab, string mode, bool componentOn, float electricalLoad, float voltage, float minVoltage,
            float profileWatts, float effectiveWatts, float radius, float target, float radiantFraction, int room, float x, float y, float condition, bool submerged, bool carried) {
            Begin('S'); Add(time); Add(itemId); Add(Sanitize(prefab)); Add(Sanitize(mode)); Add(componentOn ? 1 : 0); Add(electricalLoad);
            Add(voltage); Add(minVoltage); Add(profileWatts); Add(effectiveWatts); Add(radius); Add(target); Add(radiantFraction); Add(room); Add(x); Add(y); Add(condition);
            Add(submerged ? 1 : 0); Add(carried ? 1 : 0); Write(row.ToString());
        }

        private static float MinSkin(in ThermalBody body) {
            float result = float.PositiveInfinity;
            for (int i = 0; i < ThermalBody.Regions; i++) result = MathF.Min(result, body.Tissue[i].Temperature);
            return result;
        }
        private static float MaxInjury(in ThermalBody body) {
            float result = 0;
            for (int i = 0; i < ThermalBody.Regions; i++) result = MathF.Max(result, body.Injury[i]);
            return result;
        }
        private void Begin(char type) { row.Clear(); row.Append(type); hasField = true; }
        private void Prefix() { if (hasField) row.Append('\t'); else hasField = true; }
        private void Add(string value) { Prefix(); row.Append(value); }
        private void Add(int value) { Prefix(); row.Append(value.ToString(CultureInfo.InvariantCulture)); }
        private void Add(ushort value) { Prefix(); row.Append(value.ToString(CultureInfo.InvariantCulture)); }
        private void Add(float value) { Prefix(); row.Append(float.IsFinite(value) ? value.ToString("R", CultureInfo.InvariantCulture) : "NA"); }
        private void Add(double value) { Prefix(); row.Append(double.IsFinite(value) ? value.ToString("R", CultureInfo.InvariantCulture) : "NA"); }
        private static string Sanitize(string value) => (value ?? string.Empty).Replace("\t", "%09").Replace("\r", "%0D").Replace("\n", "%0A");

        private void Write(string value) {
            if (!Active) return;
            long recordBytes = Encoding.UTF8.GetByteCount(value) + Encoding.UTF8.GetByteCount(Environment.NewLine);
            if (bytes + recordBytes > MaximumBytes) { Failure = "Capture stopped at the 64 MiB size limit"; Close(); return; }
            try { writer.WriteLine(value); bytes += recordBytes; }
            catch (Exception error) { Failure = "Capture write failed: " + error.Message; Close(); return; }
        }

        internal void Flush(double time) {
            if (!Active || time < nextFlush) return;
            nextFlush = time + 10;
            try { writer.Flush(); }
            catch (Exception error) { Failure = "Capture flush failed: " + error.Message; Close(); }
        }

        internal void Complete(string reason) {
            if (Active) {
                row.Clear(); Begin('E'); Add(Sanitize(reason)); Write(row.ToString());
            }
            Close();
        }

        public void Dispose() => Close();
        private void Close() {
            if (disposed) return;
            disposed = true;
            try { writer.Flush(); writer.Dispose(); }
            catch (Exception error) { Failure ??= "Capture close failed: " + error.Message; }
        }
    }
}
