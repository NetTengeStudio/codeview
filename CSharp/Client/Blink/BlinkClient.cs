using Barotrauma;
using Barotrauma.Networking;
using ScpItdMod.Core;
using Microsoft.Xna.Framework.Input;

namespace ScpItdMod.Blink {
    public static class BlinkClient {
        public const Keys ForceKey = Keys.F;

        private static BlinkState state;
        private static Focus focus;
        private static float darkness;
        private static uint sequence;
        private static bool sequenced;
        private static bool authorityEngaged;
        private static bool wasHeld;

        public static BlinkState State => state;

        public static bool Tracking => focus.Tracking;

        public static bool Engaged { get; private set; }

        public static float Darkness => darkness;

        public static float Fill => state.Fill;

        public static bool EyesOpen => state.EyesOpen;

        public static bool Irritated => state.Irritated;

        private static bool Authoritative => GameMain.Client == null;

        public static void Hook() =>
            ModNet.Receive(BlinkNet.StateId, BlinkNet.StateWidth, OnStateReceived);

        public static void Reset() {
            focus.Clear();
            Renew();
        }

        public static void Advance(Character character, float dt) {
            bool active = focus.Follow(character, out bool renewed);
            if (renewed) { Renew(); }
            if (!active || dt <= 0.0f) { return; }

            bool engaged = Authoritative ? BlinkGate.Active(character) : BlinkGate.Forced || authorityEngaged;
            if (engaged && !Engaged) { state = BlinkSim.Rested(Difficulty()); }
            Engaged = engaged;

            if (!engaged || PlayView.Busy) {
                ReleaseForceKey();
                darkness = Scalar.Approach(darkness, 0.0f, BlinkConfig.DarkRate, dt);
                return;
            }

            ReadForceKey();
            darkness = Authoritative
                ? BlinkSim.Advance(ref state, Difficulty(), dt)
                : BlinkSim.Follow(ref state, dt);
        }

        public static void Soothe(BlinkDrops drops) => BlinkSim.Soothe(ref state, drops);

        public static void Irritate(float seconds) => BlinkSim.Irritate(ref state, seconds);

        private static void Renew() {
            state = BlinkSim.Rested(Difficulty());
            darkness = 0.0f;
            sequence = 0u;
            sequenced = false;
            authorityEngaged = false;
            wasHeld = false;
            Engaged = false;
        }

        private static void ReleaseForceKey() {
            if (!wasHeld) { return; }
            wasHeld = false;
            if (!Authoritative) { ReportForce(false, false); }
        }

        private static void ReadForceKey() {
            if (PlayView.Typing) {
                ReleaseForceKey();
                return;
            }
            bool hit = PlayerInput.KeyHit(ForceKey);
            bool held = PlayerInput.KeyDown(ForceKey);
            BlinkSim.Force(ref state, hit, held);
            if (!Authoritative && (hit || held != wasHeld)) { ReportForce(hit, held); }
            wasHeld = held;
        }

        private static void ReportForce(bool hit, bool held) {
            uint bits = 0u;
            if (hit) { bits |= BlinkNet.HitBit; }
            if (held) { bits |= BlinkNet.HeldBit; }
            ModNet.Send(BlinkNet.ForceId, bits, BlinkNet.ForceWidth, DeliveryMethod.Reliable);
        }

        private static void OnStateReceived(uint word) {
            BlinkState incomingState = state;
            BlinkNet.Unpack(word, ref incomingState, out BlinkFlags flags, out uint incoming);
            if (sequenced && !BlinkNet.SequenceField.Newer(incoming, sequence)) { return; }
            sequence = incoming;
            sequenced = true;
            state = incomingState;
            authorityEngaged = (flags & BlinkFlags.Engaged) != 0;
        }

        private static BlinkDifficulty Difficulty() => BlinkSim.Grade(Level.Loaded?.Difficulty ?? 0.0f);
    }
}
