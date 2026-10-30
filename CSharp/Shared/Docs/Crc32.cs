using System;
namespace ScpItdMod.Docs {
    public static class Crc32 {
        private static readonly uint[] Table = Build();

        public static uint Of(ReadOnlySpan<byte> data) {
            uint c = 0xFFFFFFFFu;
            for (int i = 0; i < data.Length; i++) { c = Table[(c ^ data[i]) & 0xFF] ^ (c >> 8); }
            return c ^ 0xFFFFFFFFu;
        }

        private static uint[] Build() {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++) {
                uint c = n;
                for (int k = 0; k < 8; k++) { c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; }
                table[n] = c;
            }
            return table;
        }
    }
}
