namespace ScpItdMod.Blink {
    public static class BlinkConfig {
        public const float StepCeiling = 1.0f / 14.0f;

        public const float CloseSeconds = 2.0f / 7.0f;
        public const float ClosePhaseDegrees = 360.0f / CloseSeconds;
        public const float FullDarkEnter = -0.25f * CloseSeconds;
        public const float FullDarkLeave = -0.75f * CloseSeconds;
        public const float CycleEnd = -CloseSeconds;

        public const float EyesOpenAbove = -0.30f * CloseSeconds;
        public const float EyesOpenBelow = -0.80f * CloseSeconds;

        public const float HoldFloor = -0.50f * CloseSeconds;

        public const float OpenRate = 0.6f;
        public const float IrritationRateBase = 1.0f;
        public const float IrritationRateScale = 0.7f;
        public const float IrritationRateCeiling = 4.0f;
        public const float IrritationPerSecond = 4.0f;

        public const float EasyMin = 7.0f;
        public const float EasyMax = 10.0f;
        public const float NormalMin = 6.5f;
        public const float NormalMax = 9.5f;
        public const float HardMin = 6.0f;
        public const float HardMax = 9.0f;

        public const float EasyBelow = 34.0f;
        public const float NormalBelow = 67.0f;

        public const float DropsEffect = 0.6f;
        public const int DropsSecondsMin = 20;
        public const int DropsSecondsMax = 31;
        public const float FineDropsEffect = 0.4f;
        public const int FineDropsSecondsMin = 30;
        public const int FineDropsSecondsMax = 41;
        public const float SuperDropsEffect = 0.0f;
        public const float SuperDropsSeconds = 60.0f;

        public const float StuckSeconds = 1000.0f / 7.0f;
        public const float StuckTearAt = 0.6f * StuckSeconds;
        public const float StuckTearSpan = 0.5f * StuckSeconds;

        public const float SendInterval = 0.1f;
        public const float HeartbeatInterval = 1.0f;
        public const int TimerBits = 12;
        public const int FrequencyBits = 6;
        public const int StuckBits = 6;
        public const int FlagBits = 2;
        public const int SequenceBits = 6;
        public const float ForceInterval = 0.05f;
        public const float ForcePacketInterval = 1.0f / 120.0f;

        public const float TimerTolerance = 0.02f * CloseSeconds;

        public const float TimerFloor = CycleEnd - StepCeiling;
        public const float TimerCeiling = EasyMax;
        public const float FrequencyFloor = HardMin;
        public const float FrequencyCeiling = EasyMax;

        public const float DarkRate = 6.0f;
    }
}
