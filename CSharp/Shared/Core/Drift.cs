namespace ScpItdMod.Core {
    public struct Drift {
        private const float Epsilon = 0.00001f;

        private float offset;

        public float Apply(float predicted) => Scalar.Clamp01(predicted + offset);

        public void Absorb(float error, float snapError) {
            if (error > snapError || error < -snapError) {
                offset = 0.0f;
                return;
            }
            offset -= error;
            if (offset > 1.0f) { offset = 1.0f; }
            else if (offset < -1.0f) { offset = -1.0f; }
        }

        public void Relax(float rate, float dt) {
            if (offset == 0.0f || dt <= 0.0f) { return; }
            offset -= offset * Scalar.Decay(rate, dt);
            if (offset < Epsilon && offset > -Epsilon) { offset = 0.0f; }
        }

        public void Clear() => offset = 0.0f;
    }
}
