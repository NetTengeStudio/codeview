namespace ScpItdMod.Core {
    public sealed class SoundSwitch {
        private readonly SoundCue[] branches;

        public SoundSwitch(SoundCue fallback, params SoundCue[] rest) {
            branches = new SoundCue[rest.Length + 1];
            branches[0] = fallback;
            for (int i = 0; i < rest.Length; i++) {
                branches[i + 1] = rest[i] ?? fallback;
            }
        }

        public void Warm() {
            for (int i = 0; i < branches.Length; i++) {
                branches[i].Warm();
            }
        }

        public SoundCue Of(int branch) =>
            (uint)branch < (uint)branches.Length ? branches[branch] : branches[0];

        public void Release() {
            for (int i = 0; i < branches.Length; i++) {
                branches[i].Release();
            }
        }
    }
}
