using System;

namespace ScpItdMod.Cinematics {
    public enum CinematicControlVerdict : byte { Returned, Restore, GiveUp }
    public enum CinematicControlGiveUp : byte { None, ClientGone, CharacterUnusable, ClientHasAnother, CharacterHasAnother }

    public struct CinematicControlObservation {
        public bool ClientConnected, CharacterUsable, ClientOwnsCharacter, ClientHasOther, CharacterHasOther;
    }

    public static class CinematicControlRetry {
        private static readonly float[] Delays = { 0.25f, 0.5f, 1f, 2f, 4f, 8f };
        public static int Attempts => Delays.Length;
        public static float Delay(int attempt) => (uint)attempt < (uint)Delays.Length ? Delays[attempt] : -1f;
        public static float TotalSeconds { get { float sum = 0; for (int i = 0; i < Delays.Length; i++) sum += Delays[i]; return sum; } }
    }

    public static class CinematicControlReturn {
        public static CinematicControlVerdict Evaluate(in CinematicControlObservation seen, out CinematicControlGiveUp reason) {
            reason = CinematicControlGiveUp.None;
            if (seen.ClientOwnsCharacter) return CinematicControlVerdict.Returned;
            if (!seen.ClientConnected) reason = CinematicControlGiveUp.ClientGone;
            else if (!seen.CharacterUsable) reason = CinematicControlGiveUp.CharacterUnusable;
            else if (seen.ClientHasOther) reason = CinematicControlGiveUp.ClientHasAnother;
            else if (seen.CharacterHasOther) reason = CinematicControlGiveUp.CharacterHasAnother;
            return reason == CinematicControlGiveUp.None ? CinematicControlVerdict.Restore : CinematicControlVerdict.GiveUp;
        }
    }
}
