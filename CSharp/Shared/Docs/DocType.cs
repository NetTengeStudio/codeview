using System;
using System.Collections.Generic;

namespace ScpItdMod.Docs {
    public enum FieldKind : byte { Text, Int, Flag, Ref }

    public readonly struct Field {
        public readonly string Key;
        public readonly FieldKind Kind;
        public readonly int MaxLength;
        public readonly bool Required;
        public readonly bool Multiline;
        public readonly bool Indexed;
        public readonly ulong ReadRoles;
        public readonly ulong WriteRoles;

        public Field(string key, FieldKind kind, int maxLength = 64, bool required = false, bool multiline = false,
            bool indexed = false, ulong readRoles = 0, ulong writeRoles = 0) {
            Key = key;
            Kind = kind;
            MaxLength = maxLength;
            Required = required;
            Multiline = multiline;
            Indexed = indexed;
            ReadRoles = readRoles;
            WriteRoles = writeRoles;
        }
    }

    public sealed class DocType {
        public const int MaxFields = 64;

        public readonly string Id;
        public readonly ushort Version;
        public readonly Field[] Fields;
        public readonly ulong CreateRoles;
        public readonly ulong CertifyRoles;
        public readonly bool OwnerEdits;
        public readonly bool OwnerReads;
        public readonly bool AllowDelegation;
        public readonly Func<Doc, ulong> ReadPolicy;
        public readonly Func<Actor, string[], DocError> CreatePolicy;
        public readonly Func<Actor, Doc, int, string, DocError> EditPolicy;
        public readonly bool Remote;

        private readonly Dictionary<string, int> slots = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Func<ushort, string[], string[]> migrate;

        public DocType(string id, ushort version, Field[] fields, ulong createRoles = ulong.MaxValue,
            ulong certifyRoles = 0, bool ownerEdits = true, Func<ushort, string[], string[]> migrate = null,
            bool remote = false, bool ownerReads = true, bool allowDelegation = true, Func<Doc, ulong> readPolicy = null,
            Func<Actor, string[], DocError> createPolicy = null,
            Func<Actor, Doc, int, string, DocError> editPolicy = null) {
            if (string.IsNullOrEmpty(id)) { throw new ArgumentException("type id is empty"); }
            if (fields == null || fields.Length == 0 || fields.Length > MaxFields) {
                throw new ArgumentException($"{id} needs 1..{MaxFields} fields");
            }
            Id = id;
            Version = version;
            Fields = fields;
            CreateRoles = createRoles;
            CertifyRoles = certifyRoles;
            OwnerEdits = ownerEdits;
            OwnerReads = ownerReads;
            AllowDelegation = allowDelegation;
            ReadPolicy = readPolicy;
            CreatePolicy = createPolicy;
            EditPolicy = editPolicy;
            Remote = remote;
            this.migrate = migrate;
            for (int i = 0; i < fields.Length; i++) {
                if (string.IsNullOrEmpty(fields[i].Key) || !slots.TryAdd(fields[i].Key, i)) {
                    throw new ArgumentException($"{id} has an empty or repeated field key");
                }
                if (fields[i].MaxLength <= 0 || fields[i].MaxLength > DocLimits.FieldChars) {
                    throw new ArgumentException($"{id}.{fields[i].Key} has an invalid length limit");
                }
            }
        }

        public static DocType Placeholder(string id, ushort version, int count) {
            var fields = new Field[Math.Clamp(count, 1, MaxFields)];
            for (int i = 0; i < fields.Length; i++) {
                fields[i] = new Field("f" + i, FieldKind.Text, DocLimits.FieldChars, multiline: true);
            }
            return new DocType(string.IsNullOrEmpty(id) ? "?" : id, version, fields, createRoles: 0, ownerEdits: false);
        }

        public int Slot(string key) => key != null && slots.TryGetValue(key, out int slot) ? slot : -1;

        public ulong AllFields => Fields.Length == 64 ? ulong.MaxValue : (1UL << Fields.Length) - 1UL;

        public string[] Upgrade(ushort from, string[] values) {
            if (from == Version) { return values; }
            if (from > Version || migrate == null) { return null; }
            string[] upgraded = migrate(from, values);
            if (upgraded == null || upgraded.Length != Fields.Length) { return null; }
            return upgraded;
        }

        public DocError Check(int slot, string value) {
            Field field = Fields[slot];
            value ??= "";
            if (value.Length == 0) { return field.Required ? DocError.Invalid : DocError.Ok; }
            if (value.Length > field.MaxLength) { return DocError.TooLarge; }
            switch (field.Kind) {
                case FieldKind.Text:
                    for (int i = 0; i < value.Length; i++) {
                        char c = value[i];
                        if (c == '\n' && field.Multiline) { continue; }
                        if (char.IsHighSurrogate(c)) {
                            if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1])) { return DocError.Invalid; }
                            i++;
                            continue;
                        }
                        if (char.IsLowSurrogate(c) || char.IsControl(c)) { return DocError.Invalid; }
                    }
                    return DocError.Ok;
                case FieldKind.Int:
                    return long.TryParse(value, System.Globalization.NumberStyles.AllowLeadingSign,
                        System.Globalization.CultureInfo.InvariantCulture, out _) ? DocError.Ok : DocError.Invalid;
                case FieldKind.Flag:
                    return value == "0" || value == "1" ? DocError.Ok : DocError.Invalid;
                case FieldKind.Ref:
                    return ulong.TryParse(value, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out ulong id) && id != 0 ? DocError.Ok : DocError.Invalid;
                default:
                    return DocError.Invalid;
            }
        }
    }

    public static class DocLimits {
        public const int FieldChars = 4096;
        public const int Revisions = 16;
        public const int Grants = 16;
        public const double MaxGrantSeconds = 300.0;
        public const int Audit = 4096;
        public const int TypeIdChars = 64;
    }

    public enum DocError : byte { Ok, NotFound, Denied, Conflict, Invalid, Sealed, TooLarge, Unknown, Corrupt }

    public enum DocOp : byte { Create, Read, Edit, Certify, Unseal, Present, Delete, Restore, Find, Revert }

    public readonly struct Actor {
        public readonly ulong Id;
        public readonly ulong Roles;
        public readonly bool Gm;

        public Actor(ulong id, ulong roles, bool gm = false) {
            Id = id;
            Roles = roles;
            Gm = gm;
        }
    }
}
