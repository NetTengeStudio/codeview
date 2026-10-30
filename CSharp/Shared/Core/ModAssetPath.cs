using System;

namespace ScpItdMod.Core {
    public static class ModAssetPath {
        public const char PackageMarker = '@';

        public static bool TrySplit(string resource, out string package, out string path) {
            package = path = "";
            if (resource == null || resource.Length < 4 || resource[0] != PackageMarker) return false;
            int slash = resource.IndexOf('/');
            if (slash < 2 || slash >= resource.Length - 1) return false;
            package = resource.Substring(1, slash - 1);
            path = resource.Substring(slash + 1);
            return path.IndexOf("..", StringComparison.Ordinal) < 0 && path[0] != '/' && path.IndexOf((char)92) < 0;
        }
    }
}
