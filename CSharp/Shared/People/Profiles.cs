using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ScpItdMod.Docs;
using ScpItdMod.Net;

namespace ScpItdMod.People {
    public enum PeopleError : byte { Ok, NotFound, Invalid, TooLarge, Full, Denied, Corrupt, Conflict, Busy, Timeout, RateLimited }

    public enum ProfileField : byte { None, Name, Look, Notes, Looks }

    public struct Appearance : IEquatable<Appearance> {
        public const int TagsMax = 96;

        public string Tags;
        public byte Hair;
        public byte Beard;
        public byte Moustache;
        public byte Face;
        public uint Skin;
        public uint HairColor;
        public uint FacialColor;

        public bool Set => !string.IsNullOrEmpty(Tags);

        public bool Equals(Appearance o) => (Tags ?? "") == (o.Tags ?? "") && Hair == o.Hair && Beard == o.Beard && Moustache == o.Moustache && Face == o.Face &&
                                             Skin == o.Skin && HairColor == o.HairColor && FacialColor == o.FacialColor;

        public override bool Equals(object obj) => obj is Appearance o && Equals(o);

        public override int GetHashCode() => HashCode.Combine(Tags ?? "", Hair, Beard, Moustache, Face, Skin, HairColor, FacialColor);

        public void Write(WireWriter w) {
            w.Text(Tags ?? "");
            w.U8(Hair);
            w.U8(Beard);
            w.U8(Moustache);
            w.U8(Face);
            w.U32(Skin);
            w.U32(HairColor);
            w.U32(FacialColor);
        }

        public static Appearance Read(ref WireReader r) => new Appearance {
            Tags = r.Text(TagsMax * 4),
            Hair = r.U8(),
            Beard = r.U8(),
            Moustache = r.U8(),
            Face = r.U8(),
            Skin = r.U32(),
            HairColor = r.U32(),
            FacialColor = r.U32()
        };
    }

    public sealed class ProfileData {
        public string Name = "";
        public string Look = "";
        public string Notes = "";
        public Appearance Looks;

        public ProfileData Clone() => new ProfileData { Name = Name, Look = Look, Notes = Notes, Looks = Looks };

        public bool Same(ProfileData o) => o != null && Name == o.Name && Look == o.Look && Notes == o.Notes && Looks.Equals(o.Looks);

        public void Write(WireWriter w) {
            w.Text(Name);
            w.Text(Look);
            w.Text(Notes);
            Looks.Write(w);
        }

        public static ProfileData Read(ref WireReader r) => new ProfileData {
            Name = r.Text(ProfileRules.NameMax * 4),
            Look = r.Text(ProfileRules.LookBytes),
            Notes = r.Text(ProfileRules.NotesBytes),
            Looks = Appearance.Read(ref r)
        };
    }

    public sealed class Profile {
        public uint Id { get; internal set; }
        public uint Version { get; internal set; }
        public ProfileData Data { get; internal set; } = new ProfileData();
        public double Created { get; internal set; }
        public double Updated { get; internal set; }

        public string Name => Data.Name;
        public string Description => Data.Look;
    }

    public static class ProfileRules {
        public const int NameMin = 2;
        public const int NameMax = 32;
        public const int LookMax = 400;
        public const int LookLines = 8;
        public const int LookBytes = 2400;
        public const int NotesMax = 600;
        public const int NotesLines = 12;
        public const int NotesBytes = 3600;
        public const int PerAccount = 5;

        public static int Units(string text) {
            if (string.IsNullOrEmpty(text)) { return 0; }
            int n = 0;
            int i = 0;
            while (i < text.Length) {
                int len = StringInfo.GetNextTextElementLength(text.AsSpan(i));
                if (len < 1) { len = 1; }
                if (text[i] != '\n') { n++; }
                i += len;
            }
            return n;
        }

        public static string Clean(string text) {
            if (string.IsNullOrEmpty(text)) { return ""; }
            string s = text.Replace("\r\n", "\n").Replace('\r', '\n');
            try {
                s = s.Normalize(NormalizationForm.FormC);
            } catch (ArgumentException) {
                return s;
            }
            var b = new StringBuilder(s.Length);
            foreach (string line in s.Split('\n')) {
                if (b.Length > 0) { b.Append('\n'); }
                b.Append(line.TrimEnd());
            }
            return b.ToString().Trim('\n', ' ');
        }

        public static PeopleError CheckName(string name) {
            if (name == null) { return PeopleError.Invalid; }
            string trimmed = name.Trim();
            if (trimmed.Length < NameMin) { return PeopleError.Invalid; }
            if (trimmed.Length > NameMax) { return PeopleError.TooLarge; }
            if (trimmed.Length != name.Length) { return PeopleError.Invalid; }
            bool lastSpace = false;
            for (int i = 0; i < name.Length; i++) {
                char c = name[i];
                bool space = c == ' ';
                if (space && lastSpace) { return PeopleError.Invalid; }
                lastSpace = space;
                if (char.IsLetter(c) || space || c == '-' || c == '\'' || c == '.') { continue; }
                return PeopleError.Invalid;
            }
            return PeopleError.Ok;
        }

        public static PeopleError CheckText(string text, int maxUnits, int maxLines, int maxBytes) {
            if (text == null) { return PeopleError.Ok; }
            if (Encoding.UTF8.GetByteCount(text) > maxBytes) { return PeopleError.TooLarge; }
            int lines = 0;
            for (int i = 0; i < text.Length; i++) {
                char c = text[i];
                if (c == '\n') {
                    if (++lines >= maxLines) { return PeopleError.TooLarge; }
                    continue;
                }
                if (c == '‖' || c == '‮' || c == '‭' || c == '⁦' || c == '⁧' || c == '⁨' || c == '⁩') { return PeopleError.Invalid; }
                if (char.IsHighSurrogate(c)) {
                    if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) { return PeopleError.Invalid; }
                    i++;
                    continue;
                }
                if (char.IsLowSurrogate(c) || char.IsControl(c)) { return PeopleError.Invalid; }
                UnicodeCategory cat = CharUnicodeInfo.GetUnicodeCategory(c);
                if (cat == UnicodeCategory.Format && c != '‍' && c != '‌') { return PeopleError.Invalid; }
            }
            return Units(text) > maxUnits ? PeopleError.TooLarge : PeopleError.Ok;
        }

        public static PeopleError CheckDescription(string text) => CheckText(text, LookMax, LookLines, LookBytes);

        public static PeopleError Check(ProfileData d, out ProfileField field) {
            field = ProfileField.Name;
            PeopleError e = CheckName(d.Name);
            if (e != PeopleError.Ok) { return e; }
            field = ProfileField.Look;
            e = CheckText(d.Look, LookMax, LookLines, LookBytes);
            if (e != PeopleError.Ok) { return e; }
            field = ProfileField.Notes;
            e = CheckText(d.Notes, NotesMax, NotesLines, NotesBytes);
            if (e != PeopleError.Ok) { return e; }
            field = ProfileField.Looks;
            if (d.Looks.Tags != null && (d.Looks.Tags.Length > Appearance.TagsMax || d.Looks.Tags.IndexOfAny(new[] { '\n', ';', '‖' }) >= 0)) { return PeopleError.Invalid; }
            field = ProfileField.None;
            return PeopleError.Ok;
        }

        public static ProfileData Normalized(ProfileData d) => new ProfileData {
            Name = (d.Name ?? "").Trim(),
            Look = Clean(d.Look),
            Notes = Clean(d.Notes),
            Looks = d.Looks
        };
    }

    public sealed class ProfileBook {
        internal readonly List<Profile> Items = new List<Profile>(ProfileRules.PerAccount);

        public uint Active { get; internal set; }

        public int Count => Items.Count;

        public Profile At(int index) => Items[index];

        public Profile Find(uint id) {
            for (int i = 0; i < Items.Count; i++) {
                if (Items[i].Id == id) { return Items[i]; }
            }
            return null;
        }

        public Profile Current => Find(Active);
    }

    public sealed class ProfileStore {
        private const uint Magic = 0x4C504F50;
        private const byte LegacyFormat = 1;
        private const byte Format = 2;

        private readonly Dictionary<ulong, ProfileBook> books = new Dictionary<ulong, ProfileBook>();
        private readonly Dictionary<uint, ulong> owners = new Dictionary<uint, ulong>();
        private uint nextId = 1;

        public int Accounts => books.Count;

        public int Revision { get; private set; }

        public IEnumerable<KeyValuePair<ulong, ProfileBook>> Books => books;

        public ProfileBook Book(ulong account) => books.TryGetValue(account, out ProfileBook book) ? book : null;

        public ulong OwnerOf(uint profile) => owners.TryGetValue(profile, out ulong account) ? account : 0;

        public Profile Get(uint profile) {
            ulong owner = OwnerOf(profile);
            return owner == 0 ? null : books[owner].Find(profile);
        }

        public PeopleError Create(ulong account, string name, string description, double now, out uint id) =>
            Create(account, new ProfileData { Name = name, Look = description ?? "" }, now, out id, out _);

        public PeopleError Create(ulong account, ProfileData data, double now, out uint id, out ProfileField field) {
            id = 0;
            field = ProfileField.None;
            if (account == 0 || data == null) { return PeopleError.Invalid; }
            ProfileData clean = ProfileRules.Normalized(data);
            PeopleError error = ProfileRules.Check(clean, out field);
            if (error != PeopleError.Ok) { return error; }
            ProfileBook book = Ensure(account);
            if (book.Count >= ProfileRules.PerAccount) { return PeopleError.Full; }
            var profile = new Profile { Id = nextId++, Version = 1, Data = clean, Created = now, Updated = now };
            book.Items.Add(profile);
            owners.Add(profile.Id, account);
            if (book.Active == 0) { book.Active = profile.Id; }
            id = profile.Id;
            Revision++;
            return PeopleError.Ok;
        }

        public PeopleError Edit(ulong account, uint id, string name, string description) {
            Profile p = Book(account)?.Find(id);
            if (p == null) { return PeopleError.NotFound; }
            ProfileData d = p.Data.Clone();
            d.Name = name;
            d.Look = description ?? "";
            return Edit(account, id, p.Version, d, 0, out _);
        }

        public PeopleError Edit(ulong account, uint id, uint baseVersion, ProfileData data, double now, out ProfileField field) {
            field = ProfileField.None;
            Profile profile = Book(account)?.Find(id);
            if (profile == null) { return PeopleError.NotFound; }
            if (profile.Version != baseVersion) { return PeopleError.Conflict; }
            ProfileData clean = ProfileRules.Normalized(data);
            PeopleError error = ProfileRules.Check(clean, out field);
            if (error != PeopleError.Ok) { return error; }
            if (clean.Same(profile.Data)) { return PeopleError.Ok; }
            profile.Data = clean;
            profile.Version++;
            profile.Updated = now;
            Revision++;
            return PeopleError.Ok;
        }

        public PeopleError Delete(ulong account, uint id, uint baseVersion = 0) {
            ProfileBook book = Book(account);
            Profile profile = book?.Find(id);
            if (profile == null) { return PeopleError.NotFound; }
            if (baseVersion != 0 && profile.Version != baseVersion) { return PeopleError.Conflict; }
            book.Items.Remove(profile);
            owners.Remove(id);
            if (book.Active == id) { book.Active = book.Count > 0 ? book.At(0).Id : 0; }
            Revision++;
            return PeopleError.Ok;
        }

        public PeopleError Select(ulong account, uint id) {
            ProfileBook book = Book(account);
            if (book?.Find(id) == null) { return PeopleError.NotFound; }
            if (book.Active != id) {
                book.Active = id;
                Revision++;
            }
            return PeopleError.Ok;
        }

        public void Save(WireWriter writer) {
            writer.Clear();
            writer.U32(Magic);
            writer.U8(Format);
            writer.Var(nextId);
            writer.Var((ulong)books.Count);
            foreach (KeyValuePair<ulong, ProfileBook> pair in books) {
                writer.U64(pair.Key);
                writer.Var(pair.Value.Active);
                writer.Var((ulong)pair.Value.Count);
                for (int i = 0; i < pair.Value.Count; i++) {
                    Profile p = pair.Value.At(i);
                    writer.Var(p.Id);
                    writer.Var(p.Version);
                    p.Data.Write(writer);
                    writer.U64((ulong)BitConverter.DoubleToInt64Bits(p.Created));
                    writer.U64((ulong)BitConverter.DoubleToInt64Bits(p.Updated));
                }
            }
            writer.U32(Crc32.Of(writer.Span));
        }

        public PeopleError Load(ReadOnlySpan<byte> data) {
            if (books.Count > 0) { return PeopleError.Invalid; }
            if (data.Length < 9 || Crc32.Of(data.Slice(0, data.Length - 4)) !=
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(data.Length - 4))) {
                return PeopleError.Corrupt;
            }
            var reader = new WireReader(data.Slice(0, data.Length - 4));
            if (reader.U32() != Magic) { return PeopleError.Corrupt; }
            byte format = reader.U8();
            if (format != Format && format != LegacyFormat) { return PeopleError.Corrupt; }
            uint loadedNext = (uint)reader.Count(int.MaxValue);
            int accounts = reader.Count(1 << 20);
            var loaded = new Dictionary<ulong, ProfileBook>();
            var loadedOwners = new Dictionary<uint, ulong>();
            for (int a = 0; a < accounts && !reader.Failed; a++) {
                ulong account = reader.U64();
                var book = new ProfileBook { Active = (uint)reader.Count(int.MaxValue) };
                int count = reader.Count(ProfileRules.PerAccount);
                for (int i = 0; i < count && !reader.Failed; i++) {
                    var p = new Profile { Id = (uint)reader.Count(int.MaxValue) };
                    if (format == LegacyFormat) {
                        p.Version = 1;
                        p.Data = new ProfileData {
                            Name = reader.Text(ProfileRules.NameMax * 4),
                            Look = ProfileRules.Clean(reader.Text(ProfileRules.LookBytes))
                        };
                        p.Created = BitConverter.Int64BitsToDouble((long)reader.U64());
                        p.Updated = p.Created;
                    } else {
                        p.Version = (uint)reader.Count(int.MaxValue);
                        p.Data = ProfileData.Read(ref reader);
                        p.Created = BitConverter.Int64BitsToDouble((long)reader.U64());
                        p.Updated = BitConverter.Int64BitsToDouble((long)reader.U64());
                    }
                    if (p.Id == 0 || p.Id >= loadedNext || p.Version == 0 || !loadedOwners.TryAdd(p.Id, account)) { return PeopleError.Corrupt; }
                    book.Items.Add(p);
                }
                if (book.Active != 0 && book.Find(book.Active) == null) { book.Active = book.Count > 0 ? book.At(0).Id : 0; }
                if (!loaded.TryAdd(account, book)) { return PeopleError.Corrupt; }
            }
            if (reader.Failed || !reader.End) { return PeopleError.Corrupt; }
            foreach (KeyValuePair<ulong, ProfileBook> pair in loaded) { books.Add(pair.Key, pair.Value); }
            foreach (KeyValuePair<uint, ulong> pair in loadedOwners) { owners.Add(pair.Key, pair.Value); }
            nextId = loadedNext;
            Revision++;
            return PeopleError.Ok;
        }

        private ProfileBook Ensure(ulong account) {
            if (!books.TryGetValue(account, out ProfileBook book)) {
                book = new ProfileBook();
                books.Add(account, book);
            }
            return book;
        }
    }
}
