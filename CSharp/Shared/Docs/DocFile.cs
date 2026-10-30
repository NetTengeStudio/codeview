using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using ScpItdMod.Core;

namespace ScpItdMod.Docs {
    // Thrown by DocFile when the disk refuses a step. It names the step, the file and the Win32 code,
    // so the caller logs one line with context and decides whether to retry later (see WriteBehind).
    public sealed class DocIOException : IOException {
        public DocIOException(string step, string filePath, int win32, Exception inner)
            : base($"DocFile {step} failed for {filePath} (Win32 {win32}): {inner.Message}", inner) {
            Step = step;
            FilePath = filePath;
            Win32 = win32;
        }

        public string Step { get; }
        public string FilePath { get; }
        public int Win32 { get; }
    }

    public static class DocFile {
        // Only transient errors are retried, and only on the game thread for a few milliseconds.
        private static readonly int[] RetryMilliseconds = { 5, 15, 40, 90 };
        private static int sequence;

        // Writers and readers of one file in this process take the same gate, so they never race on the temp,
        // the .bak file or the replace. Cross-process contention (scanner, indexer) is what the retries are for.
        private static readonly ConcurrentDictionary<string, object> gates = new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        private static object GateOf(string path) => gates.GetOrAdd(Path.GetFullPath(path), _ => new object());

        public static void Write(string path, ReadOnlySpan<byte> data) {
            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            lock (GateOf(path)) {
                Directory.CreateDirectory(directory);
                string temp = path + "." + Environment.ProcessId.ToString("x") + "." + (Interlocked.Increment(ref sequence)).ToString("x") + ".tmp";
                try {
                    WriteTemp(temp, data);
                    Publish(temp, path);
                } finally {
                    try { if (File.Exists(temp)) { File.Delete(temp); } } catch (Exception) { }
                }
                Sweep(directory, Path.GetFileName(path));
            }
        }

        public static string WriteStored(StorageRoots roots, string file, ReadOnlySpan<byte> data, Func<double> clock) {
            Exception last = null;
            for (int attempt = 0; attempt < roots.Count; attempt++) {
                double now = clock();
                string root = roots.WriteRoot(now);
                try {
                    Write(Path.Combine(root, file), data);
                    return root;
                } catch (Exception error) when (IsIo(error)) {
                    last = error;
                    if (!roots.Demote(root, now)) { break; }
                }
            }
            throw last ?? new IOException("No storage folder accepts writes");
        }

        private static void WriteTemp(string temp, ReadOnlySpan<byte> data) {
            for (int attempt = 0; ; attempt++) {
                try {
                    using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) {
                        file.Write(data);
                        file.Flush(true);
                    }
                    return;
                } catch (Exception error) when (IsTransient(error) && attempt < RetryMilliseconds.Length) {
                    Thread.Sleep(RetryMilliseconds[attempt]);
                } catch (Exception error) when (IsIo(error)) {
                    throw new DocIOException("create temp", temp, Win32Of(error), error);
                }
            }
        }

        private static void Publish(string temp, string path) {
            for (int attempt = 0; ; attempt++) {
                try {
                    if (File.Exists(path)) { File.Replace(temp, path, path + ".bak", ignoreMetadataErrors: true); }
                    else { File.Move(temp, path); }
                    return;
                } catch (Exception error) when (IsTransient(error) && attempt < RetryMilliseconds.Length) {
                    Thread.Sleep(RetryMilliseconds[attempt]);
                } catch (Exception error) when (IsIo(error)) {
                    throw new DocIOException("publish", path, Win32Of(error), error);
                }
            }
        }

        private static bool IsIo(Exception error) => error is IOException || error is UnauthorizedAccessException;

        // Win32 32 (sharing violation) and 33 (lock violation): a scanner or the indexer holds the file for a moment.
        // Win32 183 (already exists): another writer published the file between our Exists check and the move; the next try replaces it.
        // Win32 5 (access denied) is not transient: the rights are wrong, and retrying only stalls the game thread.
        private static bool IsTransient(Exception error) {
            if (!IsIo(error)) { return false; }
            int win32 = Win32Of(error);
            return win32 == 32 || win32 == 33 || win32 == 183;
        }

        // For IOException and UnauthorizedAccessException the Win32 code is the low word of HResult.
        private static int Win32Of(Exception error) => error.HResult & 0xFFFF;

        private static void Sweep(string directory, string name) {
            try {
                foreach (string stale in Directory.EnumerateFiles(directory, name + ".*.tmp")) {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(stale) > TimeSpan.FromMinutes(2)) { File.Delete(stale); }
                }
            } catch (Exception) { }
        }

        public delegate DocError Accept(byte[] data);

        public static string Read(string path, Accept accept, out DocError last) {
            lock (GateOf(path)) {
                last = DocError.NotFound;
                foreach (string candidate in new[] { path, path + ".bak" }) {
                    if (!File.Exists(candidate)) { continue; }
                    byte[] data = ReadWithRetry(candidate);
                    if (data == null) {
                        last = DocError.Corrupt;
                        continue;
                    }
                    last = accept(data);
                    if (last == DocError.Ok) { return candidate; }
                }
                return null;
            }
        }

        // Returns null when the file cannot be read; the caller then falls back to the backup and never overwrites the file.
        private static byte[] ReadWithRetry(string candidate) {
            for (int attempt = 0; ; attempt++) {
                try {
                    return File.ReadAllBytes(candidate);
                } catch (Exception error) when (IsTransient(error) && attempt < RetryMilliseconds.Length) {
                    Thread.Sleep(RetryMilliseconds[attempt]);
                } catch (Exception error) when (IsIo(error)) {
                    return null;
                }
            }
        }
    }
}
