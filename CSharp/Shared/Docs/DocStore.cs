using System;
using System.Collections.Generic;
using ScpItdMod.Net;

namespace ScpItdMod.Docs {
    internal readonly struct DocDraft {
        public readonly string Type;
        public readonly string[] Values;
        public readonly ulong ReadRoles, WriteRoles;
        public DocDraft(string type, string[] values, ulong readRoles, ulong writeRoles) { Type = type; Values = values; ReadRoles = readRoles; WriteRoles = writeRoles; }
    }

    public sealed class Doc {
        public ulong Id { get; internal set; }
        public DocType Type { get; internal set; }
        public ulong Owner { get; internal set; }
        public uint Rev { get; internal set; }
        public ulong ReadRoles { get; internal set; }
        public ulong WriteRoles { get; internal set; }
        public bool Deleted { get; internal set; }
        public bool Sealed { get; internal set; }
        public ulong Certifier { get; internal set; }
        public uint CertifiedRev { get; internal set; }
        public ulong CertifiedHash { get; internal set; }
        public double Created { get; internal set; }
        public double Updated { get; internal set; }
        public bool Orphan { get; internal set; }
        public ushort StoredVersion { get; internal set; }

        internal string[] Values;
        internal readonly Revision[] History = new Revision[DocLimits.Revisions];
        internal int HistoryCount;
        internal readonly Grant[] Grants = new Grant[DocLimits.Grants];
        internal int GrantCount;

        public string Value(int slot) => (uint)slot < (uint)Values.Length ? Values[slot] : "";

        internal ref Revision HistoryAt(int back) => ref History[(HistoryCount - 1 - back) % DocLimits.Revisions];

        internal int HistoryKept => Math.Min(HistoryCount, DocLimits.Revisions);
    }

    internal struct Revision {
        public uint Rev;
        public ulong Actor;
        public double Time;
        public ulong Changed;
        public string[] Before;
    }

    internal struct Grant {
        public ulong Actor;
        public double Until;
    }

    public struct AuditEntry {
        public ulong Seq;
        public double Time;
        public ulong Actor;
        public ulong Doc;
        public DocOp Op;
        public DocError Result;
        public uint Rev;
    }

    public sealed class DocStore {
        private const uint Magic = 0x434F4453;
        private const byte Format = 1;

        private readonly Dictionary<string, DocType> types = new Dictionary<string, DocType>(StringComparer.Ordinal);
        private readonly Dictionary<ulong, Doc> docs = new Dictionary<ulong, Doc>();
        private readonly Dictionary<string, HashSet<ulong>> byType = new Dictionary<string, HashSet<ulong>>(StringComparer.Ordinal);
        private readonly Dictionary<FieldKey, HashSet<ulong>> byField = new Dictionary<FieldKey, HashSet<ulong>>();
        private readonly AuditEntry[] audit = new AuditEntry[DocLimits.Audit];
        private ulong auditSeq;
        private ulong nextId = 1;

        public int Count => docs.Count;
        internal ulong NextId => nextId;

        internal DocError CreateBatch(in Actor actor, ReadOnlySpan<DocDraft> drafts, double now) {
            if ((ulong)drafts.Length >= ulong.MaxValue - nextId) { return DocError.TooLarge; }
            var staged = new DocStore { nextId = nextId };
            foreach (var type in types) { staged.types.Add(type.Key, type.Value); }
            for (int i = 0; i < drafts.Length; i++) {
                ref readonly DocDraft draft = ref drafts[i];
                DocError result = staged.Create(actor, draft.Type, draft.Values, draft.ReadRoles, draft.WriteRoles, now, out _);
                if (result != DocError.Ok) { return result; }
            }
            docs.EnsureCapacity(checked(docs.Count + drafts.Length));
            foreach (var entry in staged.docs) {
                docs.Add(entry.Key, entry.Value); Index(entry.Value);
                Log(actor, entry.Key, DocOp.Create, DocError.Ok, entry.Value.Rev, now);
            }
            nextId = staged.nextId;
            return DocError.Ok;
        }

        public ulong AuditSeq => auditSeq;

        public void Register(DocType type) {
            if (type == null) { throw new ArgumentNullException(nameof(type)); }
            if (docs.Count > 0) { throw new InvalidOperationException("register types before documents exist"); }
            if (type.Id.Length > DocLimits.TypeIdChars || !types.TryAdd(type.Id, type)) {
                throw new ArgumentException($"type {type.Id} is too long or already registered");
            }
        }

        public DocType TypeOf(string id) => id != null && types.TryGetValue(id, out DocType type) ? type : null;

        public bool CanRead(in Actor actor, Doc doc, double now) {
            if (doc == null) { return false; }
            if (actor.Gm) { return true; }
            if (doc.Deleted || doc.Orphan) { return false; }
            ulong roles = doc.Type.ReadPolicy == null ? doc.ReadRoles : doc.Type.ReadPolicy(doc);
            if ((doc.Owner == actor.Id && doc.Type.OwnerReads) || (roles & actor.Roles) != 0) { return true; }
            if (!doc.Type.AllowDelegation) { return false; }
            for (int i = 0; i < doc.GrantCount; i++) {
                if (doc.Grants[i].Actor == actor.Id && doc.Grants[i].Until > now) { return true; }
            }
            return false;
        }

        public static bool CanSee(in Actor actor, Doc doc, int slot) {
            ulong roles = doc.Type.Fields[slot].ReadRoles;
            return roles == 0 || actor.Gm || (roles & actor.Roles) != 0;
        }

        public static bool CanWrite(in Actor actor, Doc doc) {
            if (doc.Deleted || doc.Orphan) { return false; }
            if (actor.Gm) { return true; }
            if (doc.Sealed) { return false; }
            return (doc.Owner == actor.Id && doc.Type.OwnerEdits) || (doc.WriteRoles & actor.Roles) != 0;
        }

        public Doc Peek(ulong id) => docs.TryGetValue(id, out Doc doc) ? doc : null;

        public DocError Create(in Actor actor, string typeId, string[] values, ulong readRoles, ulong writeRoles,
            double now, out ulong id) {
            id = 0;
            if (nextId == ulong.MaxValue) { return Log(actor, 0, DocOp.Create, DocError.TooLarge, 0, now); }
            DocType type = TypeOf(typeId);
            if (type == null) { return Log(actor, 0, DocOp.Create, DocError.Unknown, 0, now); }
            if (!actor.Gm && (type.CreateRoles & actor.Roles) == 0) { return Log(actor, 0, DocOp.Create, DocError.Denied, 0, now); }
            if (values == null || values.Length != type.Fields.Length) { return Log(actor, 0, DocOp.Create, DocError.Invalid, 0, now); }
            var copy = new string[values.Length];
            for (int i = 0; i < values.Length; i++) {
                copy[i] = values[i] ?? "";
                if (copy[i].Length > 0 && !actor.Gm && type.Fields[i].WriteRoles != 0 && (type.Fields[i].WriteRoles & actor.Roles) == 0) {
                    return Log(actor, 0, DocOp.Create, DocError.Denied, 0, now);
                }
                DocError check = CheckValue(actor, type, i, copy[i], now);
                if (check != DocError.Ok) { return Log(actor, 0, DocOp.Create, check, 0, now); }
            }
            if (type.CreatePolicy != null) {
                DocError policy = type.CreatePolicy(actor, copy);
                if (policy != DocError.Ok) { return Log(actor, 0, DocOp.Create, policy, 0, now); }
            }
            var doc = new Doc {
                Id = nextId++, Type = type, Owner = actor.Id, Rev = 1, ReadRoles = readRoles, WriteRoles = writeRoles,
                Created = now, Updated = now, Values = copy, StoredVersion = type.Version
            };
            docs.Add(doc.Id, doc);
            Index(doc);
            id = doc.Id;
            return Log(actor, doc.Id, DocOp.Create, DocError.Ok, doc.Rev, now);
        }

        public DocError Read(in Actor actor, ulong id, double now, out Doc doc) {
            doc = Peek(id);
            if (doc == null || !CanRead(actor, doc, now)) {
                DocError error = doc == null ? DocError.NotFound : DocError.Denied;
                doc = null;
                return Masked(actor, id, DocOp.Read, error, now);
            }
            return DocError.Ok;
        }

        public DocError Edit(in Actor actor, ulong id, uint baseRev, ReadOnlySpan<int> slots, ReadOnlySpan<string> values,
            double now, out uint rev) {
            rev = 0;
            Doc doc = Peek(id);
            DocError error = Writable(actor, doc, now);
            if (error != DocError.Ok) { return Masked(actor, id, DocOp.Edit, error, now); }
            if (doc.Rev == uint.MaxValue) { return Log(actor, id, DocOp.Edit, DocError.TooLarge, doc.Rev, now); }
            if (slots.Length == 0 || slots.Length != values.Length || slots.Length > doc.Type.Fields.Length) {
                return Log(actor, id, DocOp.Edit, DocError.Invalid, doc.Rev, now);
            }
            ulong mask = 0;
            for (int i = 0; i < slots.Length; i++) {
                int slot = slots[i];
                if ((uint)slot >= (uint)doc.Type.Fields.Length) { return Log(actor, id, DocOp.Edit, DocError.Invalid, doc.Rev, now); }
                ulong bit = 1UL << slot;
                if ((mask & bit) != 0) { return Log(actor, id, DocOp.Edit, DocError.Invalid, doc.Rev, now); }
                mask |= bit;
                ulong roles = doc.Type.Fields[slot].WriteRoles;
                if (!actor.Gm && roles != 0 && (roles & actor.Roles) == 0) { return Log(actor, id, DocOp.Edit, DocError.Denied, doc.Rev, now); }
                DocError check = CheckValue(actor, doc.Type, slot, values[i], now);
                if (check != DocError.Ok) { return Log(actor, id, DocOp.Edit, check, doc.Rev, now); }
                if (doc.Type.EditPolicy != null) {
                    check = doc.Type.EditPolicy(actor, doc, slot, values[i] ?? "");
                    if (check != DocError.Ok) { return Log(actor, id, DocOp.Edit, check, doc.Rev, now); }
                }
            }
            if (baseRev != doc.Rev && !Mergeable(doc, baseRev, mask)) { return Log(actor, id, DocOp.Edit, DocError.Conflict, doc.Rev, now); }

            string[] before = (string[])doc.Values.Clone();
            Unindex(doc);
            for (int i = 0; i < slots.Length; i++) { doc.Values[slots[i]] = values[i] ?? ""; }
            Index(doc);
            Commit(doc, actor.Id, mask, before, now);
            rev = doc.Rev;
            return Log(actor, id, DocOp.Edit, DocError.Ok, doc.Rev, now);
        }

        public DocError Revert(in Actor actor, ulong id, uint toRev, double now, out uint rev) {
            rev = 0;
            Doc doc = Peek(id);
            DocError error = Writable(actor, doc, now);
            if (error != DocError.Ok) { return Masked(actor, id, DocOp.Revert, error, now); }
            if (!actor.Gm && doc.Owner != actor.Id) { return Log(actor, id, DocOp.Revert, DocError.Denied, doc.Rev, now); }
            if (doc.Rev == uint.MaxValue) { return Log(actor, id, DocOp.Revert, DocError.TooLarge, doc.Rev, now); }
            string[] target = null;
            for (int back = 0; back < doc.HistoryKept; back++) {
                ref Revision revision = ref doc.HistoryAt(back);
                if (revision.Rev == toRev + 1) {
                    target = revision.Before;
                    break;
                }
            }
            if (target == null || toRev >= doc.Rev) { return Log(actor, id, DocOp.Revert, DocError.NotFound, doc.Rev, now); }
            ulong mask = 0;
            for (int i = 0; i < target.Length; i++) {
                if (!string.Equals(target[i], doc.Values[i], StringComparison.Ordinal)) { mask |= 1UL << i; }
                ulong roles = doc.Type.Fields[i].WriteRoles;
                if ((mask & (1UL << i)) != 0 && !actor.Gm && roles != 0 && (roles & actor.Roles) == 0) {
                    return Log(actor, id, DocOp.Revert, DocError.Denied, doc.Rev, now);
                }
                if ((mask & (1UL << i)) != 0 && doc.Type.EditPolicy != null) {
                    DocError policy = doc.Type.EditPolicy(actor, doc, i, target[i]);
                    if (policy != DocError.Ok) { return Log(actor, id, DocOp.Revert, policy, doc.Rev, now); }
                }
            }
            string[] before = doc.Values;
            Unindex(doc);
            doc.Values = (string[])target.Clone();
            Index(doc);
            Commit(doc, actor.Id, mask, before, now);
            rev = doc.Rev;
            return Log(actor, id, DocOp.Revert, DocError.Ok, doc.Rev, now);
        }

        public DocError Certify(in Actor actor, ulong id, double now) {
            Doc doc = Peek(id);
            DocError error = doc == null ? DocError.NotFound
                : !CanRead(actor, doc, now) || doc.Deleted || doc.Orphan ? DocError.Denied
                : !actor.Gm && (doc.Type.CertifyRoles & actor.Roles) == 0 ? DocError.Denied
                : doc.Sealed ? DocError.Sealed
                : DocError.Ok;
            if (error != DocError.Ok) { return Masked(actor, id, DocOp.Certify, error, now); }
            doc.Sealed = true;
            doc.Certifier = actor.Id;
            doc.CertifiedRev = doc.Rev;
            doc.CertifiedHash = Hash(doc);
            return Log(actor, id, DocOp.Certify, DocError.Ok, doc.Rev, now);
        }

        public DocError Unseal(in Actor actor, ulong id, double now) {
            Doc doc = Peek(id);
            DocError error = doc == null ? DocError.NotFound : !actor.Gm ? DocError.Denied : !doc.Sealed ? DocError.Invalid : DocError.Ok;
            if (error != DocError.Ok) { return Masked(actor, id, DocOp.Unseal, error, now); }
            doc.Sealed = false;
            return Log(actor, id, DocOp.Unseal, DocError.Ok, doc.Rev, now);
        }

        public static bool Genuine(Doc doc) => doc != null && doc.Certifier != 0 && doc.Sealed &&
            doc.CertifiedRev == doc.Rev && doc.CertifiedHash == Hash(doc);

        public DocError Present(in Actor actor, ulong id, ulong viewer, double seconds, double now) {
            Doc doc = Peek(id);
            DocError error = doc == null ? DocError.NotFound
                : !CanRead(actor, doc, now) || doc.Deleted || !doc.Type.AllowDelegation ? DocError.Denied
                : viewer == 0 || viewer == actor.Id || !(seconds > 0.0) ? DocError.Invalid
                : DocError.Ok;
            if (error != DocError.Ok) { return Masked(actor, id, DocOp.Present, error, now); }
            double until = now + Math.Min(seconds, DocLimits.MaxGrantSeconds);
            int slot = -1;
            for (int i = 0; i < doc.GrantCount; i++) {
                if (doc.Grants[i].Actor == viewer || doc.Grants[i].Until <= now) {
                    slot = i;
                    break;
                }
            }
            if (slot < 0 && doc.GrantCount < DocLimits.Grants) { slot = doc.GrantCount++; }
            if (slot < 0) {
                slot = 0;
                for (int i = 1; i < doc.GrantCount; i++) {
                    if (doc.Grants[i].Until < doc.Grants[slot].Until) { slot = i; }
                }
            }
            doc.Grants[slot] = new Grant { Actor = viewer, Until = until };
            return Log(actor, id, DocOp.Present, DocError.Ok, doc.Rev, now);
        }

        public DocError Delete(in Actor actor, ulong id, double now) {
            Doc doc = Peek(id);
            DocError error = doc == null ? DocError.NotFound
                : doc.Deleted ? DocError.Invalid
                : actor.Gm || (doc.Owner == actor.Id && !doc.Sealed) ? DocError.Ok
                : DocError.Denied;
            if (error != DocError.Ok) { return Masked(actor, id, DocOp.Delete, error, now); }
            doc.Deleted = true;
            doc.GrantCount = 0;
            Unindex(doc);
            return Log(actor, id, DocOp.Delete, DocError.Ok, doc.Rev, now);
        }

        public DocError Restore(in Actor actor, ulong id, double now) {
            Doc doc = Peek(id);
            DocError error = doc == null ? DocError.NotFound : !actor.Gm && doc.Owner != actor.Id ? DocError.Denied : !doc.Deleted ? DocError.Invalid : DocError.Ok;
            if (error != DocError.Ok) { return Masked(actor, id, DocOp.Restore, error, now); }
            doc.Deleted = false;
            Index(doc);
            return Log(actor, id, DocOp.Restore, DocError.Ok, doc.Rev, now);
        }

        public DocError Find(in Actor actor, string typeId, string key, string value, double now, List<ulong> found) {
            found.Clear();
            DocType type = TypeOf(typeId);
            int slot = type?.Slot(key) ?? -1;
            if (slot < 0 || !type.Fields[slot].Indexed) { return Log(actor, 0, DocOp.Find, DocError.Invalid, 0, now); }
            if (!actor.Gm && type.Fields[slot].ReadRoles != 0 && (type.Fields[slot].ReadRoles & actor.Roles) == 0) {
                return Log(actor, 0, DocOp.Find, DocError.Denied, 0, now);
            }
            if (byField.TryGetValue(new FieldKey(type.Id, slot, Normal(value)), out HashSet<ulong> ids)) {
                foreach (ulong id in ids) {
                    if (CanRead(actor, docs[id], now)) { found.Add(id); }
                }
                found.Sort();
            }
            return DocError.Ok;
        }

        public void OfType(string typeId, List<ulong> found) {
            found.Clear();
            if (typeId != null && byType.TryGetValue(typeId, out HashSet<ulong> ids)) { found.AddRange(ids); }
            found.Sort();
        }

        public void Removed(string typeId, List<ulong> found) {
            found.Clear();
            foreach (Doc doc in docs.Values) {
                if (doc.Deleted && doc.Type.Id == typeId) { found.Add(doc.Id); }
            }
            found.Sort();
        }

        public int AuditSince(ulong seq, List<AuditEntry> output) {
            output.Clear();
            ulong first = auditSeq > DocLimits.Audit ? auditSeq - DocLimits.Audit : 0;
            for (ulong s = Math.Max(seq, first); s < auditSeq; s++) { output.Add(audit[s % DocLimits.Audit]); }
            return output.Count;
        }

        public void Save(WireWriter writer) {
            writer.Clear();
            writer.U32(Magic);
            writer.U8(Format);
            writer.Var(nextId);
            writer.Var((ulong)docs.Count);
            foreach (Doc doc in docs.Values) {
                writer.Var(doc.Id);
                writer.Text(doc.Type.Id);
                writer.Var(doc.Orphan ? doc.StoredVersion : doc.Type.Version);
                writer.U64(doc.Owner);
                writer.Var(doc.Rev);
                writer.U64(doc.ReadRoles);
                writer.U64(doc.WriteRoles);
                writer.U8((byte)((doc.Deleted ? 1 : 0) | (doc.Sealed ? 2 : 0)));
                writer.U64(doc.Certifier);
                writer.Var(doc.CertifiedRev);
                writer.U64(doc.CertifiedHash);
                writer.U64((ulong)BitConverter.DoubleToInt64Bits(doc.Created));
                writer.U64((ulong)BitConverter.DoubleToInt64Bits(doc.Updated));
                WriteValues(writer, doc.Values);
                int kept = doc.HistoryKept;
                writer.Var((ulong)kept);
                for (int back = kept - 1; back >= 0; back--) {
                    ref Revision revision = ref doc.HistoryAt(back);
                    writer.Var(revision.Rev);
                    writer.U64(revision.Actor);
                    writer.U64((ulong)BitConverter.DoubleToInt64Bits(revision.Time));
                    writer.U64(revision.Changed);
                    WriteValues(writer, revision.Before);
                }
            }
            ulong first = auditSeq > DocLimits.Audit ? auditSeq - DocLimits.Audit : 0;
            writer.Var(auditSeq);
            writer.Var(auditSeq - first);
            for (ulong s = first; s < auditSeq; s++) {
                ref AuditEntry entry = ref audit[s % DocLimits.Audit];
                writer.U64((ulong)BitConverter.DoubleToInt64Bits(entry.Time));
                writer.U64(entry.Actor);
                writer.Var(entry.Doc);
                writer.U8((byte)entry.Op);
                writer.U8((byte)entry.Result);
                writer.Var(entry.Rev);
            }
            writer.U32(Crc32.Of(writer.Span));
        }

        public DocError Load(ReadOnlySpan<byte> data, out int orphans) {
            orphans = 0;
            if (docs.Count > 0) { return DocError.Invalid; }
            if (data.Length < 9 || Crc32.Of(data.Slice(0, data.Length - 4)) !=
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(data.Length - 4))) {
                return DocError.Corrupt;
            }
            var reader = new WireReader(data.Slice(0, data.Length - 4));
            if (reader.U32() != Magic || reader.U8() != Format) { return DocError.Corrupt; }
            ulong loadedNext = reader.Var();
            int count = reader.Count(1 << 24);
            var loaded = new List<Doc>(Math.Min(count, 4096));
            for (int n = 0; n < count && !reader.Failed; n++) {
                var doc = new Doc { Id = reader.Var() };
                string typeId = reader.Text(DocLimits.TypeIdChars * 4);
                ushort version = (ushort)reader.Count(ushort.MaxValue);
                doc.Owner = reader.U64();
                doc.Rev = (uint)reader.Count(int.MaxValue);
                doc.ReadRoles = reader.U64();
                doc.WriteRoles = reader.U64();
                byte flags = reader.U8();
                doc.Deleted = (flags & 1) != 0;
                doc.Sealed = (flags & 2) != 0;
                doc.Certifier = reader.U64();
                doc.CertifiedRev = (uint)reader.Count(int.MaxValue);
                doc.CertifiedHash = reader.U64();
                doc.Created = BitConverter.Int64BitsToDouble((long)reader.U64());
                doc.Updated = BitConverter.Int64BitsToDouble((long)reader.U64());
                string[] values = ReadValues(ref reader);
                int kept = reader.Count(DocLimits.Revisions);
                for (int r = 0; r < kept && !reader.Failed; r++) {
                    doc.History[doc.HistoryCount++ % DocLimits.Revisions] = new Revision {
                        Rev = (uint)reader.Count(int.MaxValue),
                        Actor = reader.U64(),
                        Time = BitConverter.Int64BitsToDouble((long)reader.U64()),
                        Changed = reader.U64(),
                        Before = ReadValues(ref reader)
                    };
                }
                if (reader.Failed || values == null || doc.Id == 0 || doc.Id >= loadedNext) { return DocError.Corrupt; }
                doc.StoredVersion = version;
                DocType type = TypeOf(typeId);
                string[] upgraded = type?.Upgrade(version, values);
                if (upgraded == null || HasForeignHistory(doc, upgraded.Length)) {
                    doc.Type = DocType.Placeholder(typeId, version, values.Length);
                    doc.Orphan = true;
                    doc.Values = values;
                    orphans++;
                } else {
                    doc.Type = type;
                    doc.Values = upgraded;
                }
                loaded.Add(doc);
            }
            ulong seq = reader.Var();
            int entries = reader.Count(DocLimits.Audit);
            var restored = new AuditEntry[entries];
            for (int i = 0; i < entries && !reader.Failed; i++) {
                restored[i] = new AuditEntry {
                    Seq = seq - (ulong)entries + (ulong)i,
                    Time = BitConverter.Int64BitsToDouble((long)reader.U64()),
                    Actor = reader.U64(),
                    Doc = reader.Var(),
                    Op = (DocOp)reader.U8(),
                    Result = (DocError)reader.U8(),
                    Rev = (uint)reader.Count(int.MaxValue)
                };
            }
            if (reader.Failed || !reader.End || (ulong)entries > seq) { return DocError.Corrupt; }

            nextId = loadedNext;
            for (int i = 0; i < loaded.Count; i++) {
                if (!docs.TryAdd(loaded[i].Id, loaded[i])) {
                    docs.Clear();
                    byType.Clear();
                    byField.Clear();
                    return DocError.Corrupt;
                }
                Index(loaded[i]);
            }
            auditSeq = seq;
            for (int i = 0; i < entries; i++) { audit[restored[i].Seq % DocLimits.Audit] = restored[i]; }
            return DocError.Ok;
        }

        private static bool HasForeignHistory(Doc doc, int fields) {
            for (int back = 0; back < doc.HistoryKept; back++) {
                if (doc.HistoryAt(back).Before.Length != fields) { return true; }
            }
            return false;
        }

        private DocError Writable(in Actor actor, Doc doc, double now) {
            if (doc == null) { return DocError.NotFound; }
            if (!CanRead(actor, doc, now)) { return DocError.Denied; }
            if (doc.Sealed && !actor.Gm) { return DocError.Sealed; }
            return CanWrite(actor, doc) ? DocError.Ok : DocError.Denied;
        }

        private static bool Mergeable(Doc doc, uint baseRev, ulong mask) {
            if (baseRev > doc.Rev || doc.Rev - baseRev > (uint)doc.HistoryKept) { return false; }
            ulong changed = 0;
            for (int back = 0; back < doc.HistoryKept; back++) {
                ref Revision revision = ref doc.HistoryAt(back);
                if (revision.Rev <= baseRev) { break; }
                changed |= revision.Changed;
            }
            return (changed & mask) == 0;
        }

        private static void Commit(Doc doc, ulong actor, ulong mask, string[] before, double now) {
            doc.Rev++;
            doc.Updated = now;
            doc.History[doc.HistoryCount++ % DocLimits.Revisions] = new Revision {
                Rev = doc.Rev, Actor = actor, Time = now, Changed = mask, Before = before
            };
        }

        private DocError CheckValue(in Actor actor, DocType type, int slot, string value, double now) {
            DocError error = type.Check(slot, value);
            if (error != DocError.Ok || type.Fields[slot].Kind != FieldKind.Ref || string.IsNullOrEmpty(value)) { return error; }
            ulong target = ulong.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            return docs.TryGetValue(target, out Doc doc) && !doc.Deleted && CanRead(actor, doc, now) ? DocError.Ok : DocError.NotFound;
        }

        private DocError Masked(in Actor actor, ulong id, DocOp op, DocError error, double now) {
            Doc doc = Peek(id);
            Log(actor, id, op, error, doc?.Rev ?? 0, now);
            if (error == DocError.NotFound || doc == null) { return DocError.NotFound; }
            return CanRead(actor, doc, now) ? error : DocError.NotFound;
        }

        private void Index(Doc doc) {
            if (doc.Deleted) { return; }
            if (!byType.TryGetValue(doc.Type.Id, out HashSet<ulong> ofType)) {
                ofType = new HashSet<ulong>();
                byType.Add(doc.Type.Id, ofType);
            }
            ofType.Add(doc.Id);
            if (doc.Orphan) { return; }
            for (int i = 0; i < doc.Type.Fields.Length; i++) {
                if (!doc.Type.Fields[i].Indexed || doc.Values[i].Length == 0) { continue; }
                var key = new FieldKey(doc.Type.Id, i, Normal(doc.Values[i]));
                if (!byField.TryGetValue(key, out HashSet<ulong> ids)) {
                    ids = new HashSet<ulong>();
                    byField.Add(key, ids);
                }
                ids.Add(doc.Id);
            }
        }

        private void Unindex(Doc doc) {
            if (byType.TryGetValue(doc.Type.Id, out HashSet<ulong> ofType)) { ofType.Remove(doc.Id); }
            if (doc.Orphan) { return; }
            for (int i = 0; i < doc.Type.Fields.Length; i++) {
                if (!doc.Type.Fields[i].Indexed || doc.Values[i].Length == 0) { continue; }
                var key = new FieldKey(doc.Type.Id, i, Normal(doc.Values[i]));
                if (byField.TryGetValue(key, out HashSet<ulong> ids) && ids.Remove(doc.Id) && ids.Count == 0) { byField.Remove(key); }
            }
        }

        private DocError Log(in Actor actor, ulong doc, DocOp op, DocError result, uint rev, double now) {
            audit[auditSeq % DocLimits.Audit] = new AuditEntry {
                Seq = auditSeq, Time = now, Actor = actor.Id, Doc = doc, Op = op, Result = result, Rev = rev
            };
            auditSeq++;
            return result;
        }

        private static string Normal(string value) => (value ?? "").Trim().ToLowerInvariant();

        public static ulong Hash(Doc doc) {
            ulong h = 14695981039346656037UL;
            h = Mix(h, doc.Type.Id);
            h = Mix(h, doc.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            for (int i = 0; i < doc.Values.Length; i++) { h = Mix(h, doc.Values[i]); }
            return h;
        }

        private static ulong Mix(ulong h, string text) {
            for (int i = 0; i < text.Length; i++) {
                h ^= text[i];
                h *= 1099511628211UL;
            }
            h ^= 0xFFFF;
            return h * 1099511628211UL;
        }

        private static void WriteValues(WireWriter writer, string[] values) {
            writer.Var((ulong)values.Length);
            for (int i = 0; i < values.Length; i++) { writer.Text(values[i]); }
        }

        private static string[] ReadValues(ref WireReader reader) {
            int length = reader.Count(DocType.MaxFields);
            if (reader.Failed) { return null; }
            var values = new string[length];
            for (int i = 0; i < length; i++) { values[i] = reader.Text(DocLimits.FieldChars * 4); }
            return reader.Failed ? null : values;
        }

        private readonly struct FieldKey : IEquatable<FieldKey> {
            private readonly string type;
            private readonly int slot;
            private readonly string value;

            public FieldKey(string type, int slot, string value) {
                this.type = type;
                this.slot = slot;
                this.value = value;
            }

            public bool Equals(FieldKey other) =>
                slot == other.slot && string.Equals(type, other.type, StringComparison.Ordinal) &&
                string.Equals(value, other.value, StringComparison.Ordinal);

            public override bool Equals(object obj) => obj is FieldKey other && Equals(other);

            public override int GetHashCode() => HashCode.Combine(type, slot, value);
        }
    }

}
