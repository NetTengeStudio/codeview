namespace ScpItdMod.Core {
    public static class SoundBank {
        public static string[] Paths(string folder, string prefix, int count) {
            string[] paths = new string[count];
            for (int i = 0; i < count; i++) {
                paths[i] = folder + prefix + (i + 1) + ".ogg";
            }
            return paths;
        }
    }
}
