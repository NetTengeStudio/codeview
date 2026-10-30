using System.Collections.Generic;

namespace ScpItdMod.Weather {
    public sealed class SkyLightTiles {
        private readonly Dictionary<long, int> built = new Dictionary<long, int>();
        private WeatherGrid grid;

        public static long Key(int tx, int ty) => ((long)tx << 32) | (uint)ty;

        public bool Bind(WeatherGrid grid) {
            if (ReferenceEquals(this.grid, grid)) { return false; }
            this.grid = grid; built.Clear();
            return true;
        }

        public bool Stale(int tx, int ty) => !built.TryGetValue(Key(tx, ty), out int revision) || revision != grid.LightRevision;

        public void Built(int tx, int ty) => built[Key(tx, ty)] = grid.LightRevision;
    }
}
