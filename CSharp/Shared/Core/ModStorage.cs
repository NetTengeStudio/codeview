using System;
using System.Collections.Generic;
using System.IO;

namespace ScpItdMod.Core {
    public sealed class StorageRoots {
        public const double RecheckSeconds = 300;
        public const double CoolDownSeconds = 120;

        private readonly string[] candidates;
        private readonly Func<string, bool> writable;
        private readonly double[] blockedUntil;
        private int active = -1;
        private double checkedAt = double.NegativeInfinity;

        public StorageRoots(IEnumerable<string> roots, Func<string, bool> writable) {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots) {
                if (string.IsNullOrWhiteSpace(root)) { continue; }
                string full = Normalize(root);
                if (seen.Add(full)) { list.Add(full); }
            }
            if (list.Count == 0) { throw new InvalidOperationException("No storage folder is available"); }
            candidates = list.ToArray();
            blockedUntil = new double[candidates.Length];
            this.writable = writable ?? throw new ArgumentNullException(nameof(writable));
        }

        public int Count => candidates.Length;

        public IReadOnlyList<string> Candidates => candidates;

        public string Active => active < 0 ? null : candidates[active];

        public string WriteRoot(double now) {
            if (active >= 0 && now - checkedAt < RecheckSeconds && now >= blockedUntil[active]) { return candidates[active]; }
            for (int i = 0; i < candidates.Length; i++) {
                if (now < blockedUntil[i]) { continue; }
                if (!writable(candidates[i])) {
                    blockedUntil[i] = now + CoolDownSeconds;
                    continue;
                }
                active = i;
                checkedAt = now;
                return candidates[i];
            }
            active = -1;
            checkedAt = now;
            return candidates[0];
        }

        public bool Demote(string root, double now) {
            for (int i = 0; i < candidates.Length; i++) {
                if (!string.Equals(candidates[i], Normalize(root), StringComparison.OrdinalIgnoreCase)) { continue; }
                blockedUntil[i] = now + CoolDownSeconds;
                if (active == i) { active = -1; checkedAt = double.NegativeInfinity; }
                break;
            }
            for (int i = 0; i < candidates.Length; i++) {
                if (now >= blockedUntil[i]) { return true; }
            }
            return false;
        }

        public string NewestPath(string file, Func<string, DateTime?> stamp) {
            string best = null;
            DateTime bestStamp = DateTime.MinValue;
            for (int i = 0; i < candidates.Length; i++) {
                string path = Path.Combine(candidates[i], file);
                DateTime? main = stamp(path), backup = stamp(path + ".bak");
                DateTime? newest = main.HasValue && backup.HasValue ? (main.Value >= backup.Value ? main : backup) : main ?? backup;
                if (!newest.HasValue || newest.Value <= bestStamp && best != null) { continue; }
                best = path;
                bestStamp = newest.Value;
            }
            return best ?? Path.Combine(candidates[0], file);
        }

        private static string Normalize(string root) => root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static class ModStorage {
        public const string ModFolder = "scpitd";
        public const string FallbackFolder = "scpitd-data";
        private const string Company = "Daedalic Entertainment GmbH";
        private const string Game = "Barotrauma";

        private static StorageRoots shared;
        private static string sharedKey, announced;
        private static readonly Func<string, DateTime?> fileStamp = path => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;

        public static IReadOnlyList<string> Candidates(string gameSaveFolder, IEnumerable<string> environmentBases, string installFolder, string temporaryFolder) {
            var list = new List<string>();
            if (!string.IsNullOrWhiteSpace(gameSaveFolder)) { list.Add(Path.Combine(gameSaveFolder, ModFolder)); }
            foreach (string root in environmentBases) {
                if (!string.IsNullOrWhiteSpace(root)) { list.Add(Path.Combine(root, Company, Game, ModFolder)); }
            }
            if (!string.IsNullOrWhiteSpace(installFolder)) { list.Add(Path.Combine(installFolder, FallbackFolder)); }
            if (!string.IsNullOrWhiteSpace(temporaryFolder)) { list.Add(Path.Combine(temporaryFolder, ModFolder)); }
            return list;
        }

        public static bool ProbeWritable(string directory) {
            string probe = null;
            try {
                Directory.CreateDirectory(directory);
                probe = Path.Combine(directory, ".probe." + Environment.ProcessId.ToString("x") + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
                using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    stream.WriteByte(1);
                    stream.Flush(true);
                }
                return true;
            } catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is NotSupportedException || error is ArgumentException) {
                return false;
            } finally {
                try { if (probe != null && File.Exists(probe)) { File.Delete(probe); } } catch (Exception) { }
            }
        }

        public static StorageRoots For(string gameSaveFolder) {
            string key = gameSaveFolder ?? string.Empty;
            if (shared != null && sharedKey == key) { return shared; }
            shared = new StorageRoots(
                Candidates(gameSaveFolder, EnvironmentBases(), AppContext.BaseDirectory, Path.GetTempPath()),
                ProbeWritable);
            sharedKey = key;
            return shared;
        }

        public static string ReadPath(string gameSaveFolder, string file) => For(gameSaveFolder).NewestPath(file, fileStamp);

        public static bool Moved(string root) {
            string previous = announced;
            announced = root;
            return previous != null && !string.Equals(previous, root, StringComparison.OrdinalIgnoreCase);
        }

        public static void Reset() {
            shared = null;
            sharedKey = null;
            announced = null;
        }

        private static IEnumerable<string> EnvironmentBases() {
            yield return Environment.GetEnvironmentVariable("LOCALAPPDATA");
            string profile = Environment.GetEnvironmentVariable("USERPROFILE");
            if (!string.IsNullOrWhiteSpace(profile)) { yield return Path.Combine(profile, "AppData", "Local"); }
        }
    }
}
