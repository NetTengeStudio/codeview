using ScpItdMod.Motion;

namespace ScpItdMod.Core {
    // Opacity envelope for a docked HUD block. The envelope is a Motion property
    // now, so the same spring runtime that animates the inventory ring and the
    // Windows drives every HUD fade. `Reset` releases the scope: a meter that is
    // switched off does not keep a live property behind.
    public struct HudFade {
        public const float InRate = 8.0f;
        public const float OutRate = 3.2f;
        public const float DismissRate = 24.0f;
        public const float Shown = 0.85f;
        public const float Cutoff = 0.004f;

        private MotionScope scope;
        private MotionHandle<float> opacity;

        public float Opacity => opacity.Alive ? opacity.Value : 0.0f;

        public bool Painted => Opacity > Cutoff;

        public void Reset() {
            scope?.Dispose();
            scope = null;
        }

        public void Advance(bool visible, float dt) {
            if (scope?.Alive != true) {
                scope?.Dispose();
                scope = MotionClient.Runtime.Scope();
                opacity = scope.Scalar();
            }
            opacity.To(visible ? Shown : 0.0f,
                MotionTransition.Spring(visible ? InRate : DismissRate, 1.0f));
        }
    }
}
