using System;
using System.IO;
using ScpItdMod.Core;
using ScpItdMod.Docs;

namespace ScpItdMod.Pc {
    public static class PcMigrate {
        private const string OldFile = "terminals.bin";
        public static int Run(PcCore core, double now) {
            string path = ModStorage.ReadPath(SaveUtil.DefaultSaveFolder, OldFile);
            if (!File.Exists(path) || !DocNet.Writable) { return 0; }
            int made = 0;
            try {
                if (new FileInfo(path).Length > PcLegacy.MaxBytes) { throw new InvalidDataException("archive exceeds the supported size"); }
                PcError result = core.ImportLegacy(File.ReadAllBytes(path), now, out made);
                if (result != PcError.Ok) { throw new InvalidDataException($"invalid or unsupported archive ({result}); source retained"); }
                if (!DocNet.Flush()) { return made; }
                File.Move(path, path + ".migrated", overwrite: true);
                ModLog.Message($"carried over {made} files and folders from the old terminals");
            } catch (Exception error) {
                ModLog.Error($"{OldFile} cannot be carried over: {error.Message}");
            }
            return made;
        }
    }
}
