namespace ScpItdMod.Stamina {
    public static class StaminaConfig {
        public const float Drain = 1.0f / 22.0f;
        public const float DrainRun = 1.0f;
        public const float DrainSwim = 1.5f;
        public const float DrainClimb = 1.3f;

        public const float Regen = 1.0f / 16.0f;
        public const float RegenDelay = 0.6f;
        public const float RegenLand = 1.0f;
        public const float RegenWater = 0.7f;
        public const float RegenClimb = 0.85f;

        public const float FalloffStart = 0.30f;
        public const float SlowFloor = 0.72f;
        public const float ExhaustedWalk = 0.62f;

        public const float LockAt = 0.02f;
        public const float UnlockAt = 0.25f;
        public const float MovingEpsilon = 0.01f;

        public const float SendInterval = 0.1f;
        public const float HeartbeatInterval = 1.0f;
        public const float SnapError = 0.25f;
        public const int ValueBits = 9;
        public const int FlagBits = 2;
        public const int SequenceBits = 5;
        public const float ValueTolerance = 0.005f;

        public const float FillRate = 14.0f;
        public const float OffsetRate = 3.0f;
        public const float ExhaustRate = 2.2f;
        public const float FadeHold = 1.1f;
        public const float FullEpsilon = 0.999f;

        public const float ZoomBoost = 0.20f;

        public const float VignetteAlpha = 0.70f;
        public const float VignetteInner = 0.50f;
        public const float VignetteOuter = 1.20f;
        public const int VignetteResolution = 256;

        public const float BreathVolumeSpan = 0.70f;
    }
}
