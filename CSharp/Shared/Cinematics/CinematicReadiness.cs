using System;
using System.Collections.Generic;

namespace ScpItdMod.Cinematics {
    public struct CinematicReadiness {
        private uint epoch;
        private ulong catalog;
        private bool known;

        public bool Ready(uint expectedEpoch, ulong expectedCatalog) => known && epoch == expectedEpoch && catalog == expectedCatalog;

        public bool Accept(uint receivedEpoch, ulong receivedCatalog, uint expectedEpoch, ulong expectedCatalog) {
            if (receivedEpoch != expectedEpoch || receivedCatalog != expectedCatalog || expectedCatalog == 0) return false;
            epoch = receivedEpoch; catalog = receivedCatalog; known = true;
            return true;
        }

        public static ulong Fingerprint(IReadOnlyDictionary<string, CinematicPlan> plans) {
            var names = new string[plans.Count];
            int index = 0;
            foreach (string name in plans.Keys) names[index++] = name;
            Array.Sort(names, StringComparer.Ordinal);
            ulong hash = 14695981039346656037UL;
            foreach (string name in names) {
                foreach (char c in name) { hash = unchecked((hash ^ (byte)c) * 1099511628211UL); hash = unchecked((hash ^ (byte)(c >> 8)) * 1099511628211UL); }
                hash = unchecked(hash * 1099511628211UL);
                ulong plan = plans[name].Hash;
                for (int b = 0; b < 8; b++) { hash = unchecked((hash ^ (byte)plan) * 1099511628211UL); plan >>= 8; }
            }
            return hash == 0 ? 1 : hash;
        }
    }
}
