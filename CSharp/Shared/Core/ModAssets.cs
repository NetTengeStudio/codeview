using Barotrauma;
using Barotrauma.LuaCs;

namespace ScpItdMod.Core {
    public static class ModAssets {
        private static string root = string.Empty;

        public static string Root => root;

        public static void Mount<TPlugin>(IPluginManagementService plugins) where TPlugin : IAssemblyPlugin {
            if (plugins != null && plugins.TryGetPackageForPlugin<TPlugin>(out ContentPackage package)) {
                root = package.Dir;
                return;
            }
            root = string.Empty;
            ModLog.Error($"{typeof(TPlugin).Name} content package folder not found");
        }

        public static void Release() => root = string.Empty;

        public static string Resolve(string relativePath) {
            if (ModAssetPath.TrySplit(relativePath, out string package, out string inner)) {
                foreach (ContentPackage candidate in ContentPackageManager.EnabledPackages.All)
                    if (string.Equals(candidate.Name, package, System.StringComparison.Ordinal)) return System.IO.Path.Combine(candidate.Dir, inner);
                return string.Empty;
            }
            return root.Length == 0 ? string.Empty : System.IO.Path.Combine(root, relativePath);
        }
    }
}
