namespace ScpItdMod.People {
    public sealed class DraftAutosave {
        public const double Quiet = 1.5, RetryGap = 6.0;
        public const int MaxAttempts = 3;

        private uint profile;
        private int signature;
        private double changedAt, lastAttempt;
        private int attempts;
        private bool tracking;

        public bool Step(double now, uint id, bool dirty, bool blocked, int currentSignature, bool flush) {
            if (!dirty || !tracking || id != profile) {
                tracking = dirty;
                profile = id;
                signature = currentSignature;
                changedAt = now;
                attempts = 0;
                if (!dirty) { return false; }
            }
            if (currentSignature != signature) {
                signature = currentSignature;
                changedAt = now;
                attempts = 0;
            }
            if (blocked || attempts >= MaxAttempts) { return false; }
            if (!flush && now - changedAt < Quiet) { return false; }
            if (attempts > 0 && now - lastAttempt < RetryGap && !flush) { return false; }
            attempts++;
            lastAttempt = now;
            return true;
        }
    }
}
