namespace ScpItdMod.Thermal {
    public enum ThermalGovernance : byte { Govern, ReleaseAcute, ReleaseAll }

    public static class ThermalGovernanceRules {
        public const int FaultLimit = 3;

        public static ThermalGovernance Decide(bool simulationEnabled, bool godMode, bool disabledByEvent) {
            if (godMode || disabledByEvent) return ThermalGovernance.ReleaseAll;
            return simulationEnabled ? ThermalGovernance.Govern : ThermalGovernance.ReleaseAcute;
        }

        public static bool Quarantine(int consecutiveFaults) => consecutiveFaults >= FaultLimit;
    }
}
