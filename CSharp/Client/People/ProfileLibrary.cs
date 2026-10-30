using System;
using System.IO;
using ScpItdMod.Net;

namespace ScpItdMod.People {
    // Local templates have their own account and IDs. Server IDs never cross this boundary.
    public sealed class ProfileLibrary {
        private const ulong Account = 1;
        public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScpItd", "characters.dat");
        private readonly string path;
        private ProfileStore store;
        public ProfileBook Book => store.Book(Account);
        public ProfileLibrary(string path) {
            this.path = Path.GetFullPath(path);
            store = Read();
        }
        private ProfileStore Read() {
            var result = new ProfileStore();
            if (!File.Exists(path)) { return result; }
            var info = new FileInfo(path);
            if (info.Length > 65536) { throw new InvalidDataException("Библиотека персонажей слишком велика."); }
            PeopleError error = result.Load(File.ReadAllBytes(path));
            if (error != PeopleError.Ok) { throw new InvalidDataException("Библиотека персонажей повреждена: " + error); }
            return result;
        }
        public PeopleError Save(uint id, ProfileData data, out uint saved, out ProfileField field) {
            saved = id; field = ProfileField.None;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var gate = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
                ProfileStore next = Read();
                Profile current = next.Book(Account)?.Find(id);
                Profile baseline = store.Book(Account)?.Find(id);
                if (id != 0 && (current == null || baseline == null || current.Version != baseline.Version)) { return PeopleError.Conflict; }
                double now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                PeopleError error = id == 0 ? next.Create(Account, data, now, out saved, out field)
                    : next.Edit(Account, id, current.Version, data, now, out field);
                if (error != PeopleError.Ok) { return error; }
                next.Select(Account, saved);
                Persist(next); store = next;
                return PeopleError.Ok;
            }
        }
        public PeopleError Activate(uint id) {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var gate = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
                ProfileStore next = Read();
                PeopleError error = next.Select(Account, id);
                if (error != PeopleError.Ok) { return error; }
                Persist(next); store = next;
                return PeopleError.Ok;
            }
        }
        public PeopleError Delete(uint id) {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var gate = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
                ProfileStore next = Read();
                Profile baseline = Book?.Find(id);
                if (baseline == null) { return PeopleError.NotFound; }
                PeopleError error = next.Delete(Account, id, baseline.Version);
                if (error != PeopleError.Ok) { return error; }
                Persist(next); store = next; return PeopleError.Ok;
            }
        }
        private void Persist(ProfileStore next) {
            var writer = new WireWriter(4096); next.Save(writer);
            ScpItdMod.Docs.DocFile.Write(path, writer.Span);
        }
    }
}
