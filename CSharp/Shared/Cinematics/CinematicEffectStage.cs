namespace ScpItdMod.Cinematics {
    public struct CinematicEffectStage {
        private uint revision;
        private ushort stage;
        private bool known;

        public bool Enter(uint nextRevision, ushort nextStage) {
            if (known && revision == nextRevision && stage == nextStage) return false;
            known = true; revision = nextRevision; stage = nextStage;
            return true;
        }
    }
}
