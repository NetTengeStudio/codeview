using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;

namespace ScpItdMod.Core {
    public static class FaultPolicy {
        public const int TripLimit = 8;
        private const int DepthLimit = 8;

        public static bool Owns(Exception error, Assembly mine) {
            if (mine == null) { return false; }
            for (int depth = 0; error != null && depth < DepthLimit; depth++, error = error.InnerException) {
                StackFrame[] frames = new StackTrace(error, false).GetFrames();
                if (frames == null) { continue; }
                for (int i = 0; i < frames.Length; i++) {
                    Assembly owner = frames[i].GetMethod()?.DeclaringType?.Assembly;
                    if (ReferenceEquals(owner, mine)) { return true; }
                }
            }
            return false;
        }

        public static string Describe(Exception error, Assembly mine) {
            string where = "unknown";
            StackFrame[] frames = error == null ? null : new StackTrace(error, false).GetFrames();
            for (int i = 0; frames != null && i < frames.Length; i++) {
                MethodBase method = frames[i].GetMethod();
                if (!ReferenceEquals(method?.DeclaringType?.Assembly, mine)) { continue; }
                where = method.DeclaringType.Name + "." + method.Name;
                break;
            }
            return error == null ? "none" : error.GetType().Name + ": " + error.Message + " at " + where;
        }

        public static string KeyOf(MethodBase method) =>
            method == null ? "unknown" : (method.DeclaringType?.FullName ?? "?") + "." + method.Name;
    }

    public sealed class FaultBreaker {
        private readonly int limit;
        private readonly Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);

        public FaultBreaker(int limit) {
            if (limit < 1) { throw new ArgumentOutOfRangeException(nameof(limit)); }
            this.limit = limit;
        }

        public int Count(string key) => counts.TryGetValue(key, out int count) ? count : 0;

        public bool Tripped(string key) => Count(key) >= limit;

        public bool Record(string key) {
            int count = Count(key);
            if (count >= limit) { return false; }
            counts[key] = ++count;
            return count == limit;
        }

        public void Reset() => counts.Clear();
    }
}
