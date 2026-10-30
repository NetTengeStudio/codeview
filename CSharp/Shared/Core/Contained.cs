using System;
using System.Collections.Generic;
using Barotrauma;

namespace ScpItdMod.Core {
    public static class Contained {
        public const int Limit = 3;

        private sealed class Strike {
            public int Count;
            public bool Disabled;
        }

        private static readonly Dictionary<string, Strike> strikes = new Dictionary<string, Strike>(StringComparer.Ordinal);

        public static bool IsDisabled(string feature) => strikes.TryGetValue(feature, out Strike strike) && strike.Disabled;

        public static int Failures(string feature) => strikes.TryGetValue(feature, out Strike strike) ? strike.Count : 0;

        public static void Run(string feature, Action action) {
            if (IsDisabled(feature)) { return; }
            try { action(); }
            catch (Exception error) { Fail(feature, error); }
        }

        public static void Fail(string feature, Exception error) {
            if (!strikes.TryGetValue(feature, out Strike strike)) {
                strike = new Strike();
                strikes[feature] = strike;
            }
            strike.Count++;
            if (strike.Count == 1) { ModLog.Error($"{feature} failed: {error.GetType().Name}: {error.Message}"); }
            if (strike.Count < Limit || strike.Disabled) { return; }
            strike.Disabled = true;
            ModLog.Error($"{feature} disabled after {strike.Count} failures; the game keeps running without it");
        }

        public static void Revive(string feature) {
            if (!strikes.TryGetValue(feature, out Strike strike)) { return; }
            strike.Count = 0;
            strike.Disabled = false;
        }
    }
}
