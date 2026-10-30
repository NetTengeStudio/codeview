using System;

namespace ScpItdMod.Cinematics {
    public enum CinematicBridgeVerdict : byte { Chosen, NotLoaded, Incompatible, NoneActive, Ambiguous }

    public readonly struct CinematicBridgeCandidate {
        public readonly bool Compatible, Active;
        public CinematicBridgeCandidate(bool compatible, bool active) { Compatible = compatible; Active = active; }
    }

    public static class CinematicBridgeChoice {
        public static CinematicBridgeVerdict Pick(ReadOnlySpan<CinematicBridgeCandidate> candidates, out int index) {
            index = -1;
            if (candidates.IsEmpty) return CinematicBridgeVerdict.NotLoaded;
            int compatible = 0;
            for (int i = 0; i < candidates.Length; i++) {
                if (!candidates[i].Compatible) continue;
                compatible++;
                if (!candidates[i].Active) continue;
                if (index >= 0) { index = -1; return CinematicBridgeVerdict.Ambiguous; }
                index = i;
            }
            if (index >= 0) return CinematicBridgeVerdict.Chosen;
            return compatible == 0 ? CinematicBridgeVerdict.Incompatible : CinematicBridgeVerdict.NoneActive;
        }

        public static string Explain(CinematicBridgeVerdict verdict, int candidates, int requiredVersion) => verdict switch {
            CinematicBridgeVerdict.NotLoaded => "BOVehicles with CinematicBridge v" + requiredVersion + " is not loaded. Enable the updated BOVehicles package together with SCPITD.",
            CinematicBridgeVerdict.Incompatible => "Incompatible BOVehicles: " + candidates + " loaded CinematicBridge copies, none is v" + requiredVersion + ". Update BOVehicles.",
            CinematicBridgeVerdict.NoneActive => "BOVehicles is loaded (" + candidates + " CinematicBridge copies) but none is running: only stale assemblies of an unloaded session remain.",
            CinematicBridgeVerdict.Ambiguous => "More than one BOVehicles copy is running (" + candidates + " CinematicBridge copies). Enable only one BOVehicles package.",
            _ => ""
        };
    }
}
