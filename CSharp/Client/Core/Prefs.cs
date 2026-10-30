using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Barotrauma;

namespace ScpItdMod.Core {
    public static class Prefs {
        private const string File = "client.cfg";
        private const int MaxEntries = 64;
        private const int MaxValue = 64;

        private static readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private const double RetryStep = 5, MaxRetrySeconds = 300;
        private static bool loaded;
        private static int failures;
        private static double retryAt = double.NegativeInfinity;

        public static int Revision { get; private set; }
        public static void Reload() { values.Clear(); loaded = false; Revision++; }

        public static float Number(string key, float fallback, float min, float max) {
            Load();
            if (!values.TryGetValue(key, out string text) ||
                !float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) || float.IsNaN(v)) {
                return fallback;
            }
            return v < min ? min : v > max ? max : v;
        }

        public static bool Flag(string key, bool fallback) {
            Load();
            return values.TryGetValue(key, out string text) ? text == "1" || text.Equals("on", StringComparison.OrdinalIgnoreCase) : fallback;
        }

        public static string Text(string key, string fallback) {
            Load();
            return values.TryGetValue(key, out string text) ? text : fallback;
        }

        public static void Set(string key, string value) {
            Load();
            if (string.IsNullOrWhiteSpace(key) || key.IndexOfAny(new[] { '=', '\n', '\r' }) >= 0) { return; }
            value = (value ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
            if (value.Length > MaxValue) { value = value.Substring(0, MaxValue); }
            if (!values.ContainsKey(key) && values.Count >= MaxEntries) { return; }
            values[key] = value;
            Revision++;
            Save();
        }

        private static string PathOf() => ModStorage.ReadPath(SaveUtil.DefaultSaveFolder, File);

        private static void Load() {
            if (loaded) { return; }
            loaded = true;
            string path = PathOf();
            try {
                if (!System.IO.File.Exists(path)) { return; }
                foreach (string line in System.IO.File.ReadAllLines(path, Encoding.UTF8)) {
                    int eq = line.IndexOf('=');
                    if (eq <= 0 || values.Count >= MaxEntries) { continue; }
                    string value = line.Substring(eq + 1).Trim();
                    if (value.Length > MaxValue) { continue; }
                    values[line.Substring(0, eq).Trim()] = value;
                }
            } catch (Exception error) {
                ModLog.Error($"{File} cannot be read: {error.Message}");
            }
        }

        private static void Save() {
            if (Timing.TotalTime < retryAt) { return; }
            try {
                var text = new StringBuilder();
                foreach (KeyValuePair<string, string> pair in values) { text.Append(pair.Key).Append('=').Append(pair.Value).Append((char)10); }
                ScpItdMod.Docs.DocFile.WriteStored(ModStorage.For(SaveUtil.DefaultSaveFolder), File, new UTF8Encoding(false).GetBytes(text.ToString()), () => Timing.TotalTime);
                failures = 0;
                retryAt = double.NegativeInfinity;
            } catch (Exception error) {
                failures++;
                retryAt = Timing.TotalTime + Math.Min(MaxRetrySeconds, RetryStep * failures);
                if (failures == 1) { ModLog.Error($"{File} cannot be saved, settings stay in memory and saving retries later: {error.Message}"); }
            }
        }
    }
}
