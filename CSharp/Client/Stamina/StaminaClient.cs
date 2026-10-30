using Barotrauma;
using ScpItdMod.Core;

namespace ScpItdMod.Stamina {
    public static class StaminaClient {
        public static StaminaState Predicted = StaminaState.Full;

        private static Drift drift;
        private static Focus focus;
        private static float exhaustion;
        private static uint sequence;
        private static bool sequenced;

        public static bool Exerting => Predicted.Exerting;

        public static bool Locked => Predicted.Locked;

        public static float Exhaustion => exhaustion;

        public static float Display => drift.Apply(Predicted.Value);

        public static void Hook() =>
            ModNet.Receive(StaminaNet.StateId, StaminaNet.Width, OnStateReceived);

        public static void Reset() {
            focus.Clear();
            Renew();
        }

        public static void Advance(Character character, StaminaActivity activity, bool exerting, float dt) {
            bool active = focus.Follow(character, out bool renewed);
            if (renewed) { Renew(); }
            if (!active || dt <= 0.0f) { return; }

            StaminaSim.Step(ref Predicted, activity, exerting, dt);
            exhaustion = Scalar.Approach(exhaustion, StaminaSim.Exhaustion(Predicted.Value),
                StaminaConfig.ExhaustRate, dt);
            drift.Relax(StaminaConfig.OffsetRate, dt);
        }

        private static void Renew() {
            Predicted = StaminaState.Full;
            drift.Clear();
            exhaustion = 0.0f;
            sequence = 0u;
            sequenced = false;
        }

        private static void OnStateReceived(uint word) {
            StaminaNet.Unpack((ushort)word, out float authoritative, out StaminaFlags flags, out uint incoming);
            if (sequenced && !StaminaNet.SequenceField.Newer(incoming, sequence)) { return; }
            sequence = incoming;
            sequenced = true;

            float error = authoritative - Predicted.Value;
            Predicted.Value = authoritative;
            Predicted.Flags = flags;
            if (error >= StaminaConfig.ValueTolerance || error <= -StaminaConfig.ValueTolerance) {
                drift.Absorb(error, StaminaConfig.SnapError);
            }
        }
    }
}
