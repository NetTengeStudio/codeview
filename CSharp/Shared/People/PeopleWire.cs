using System;
using System.Collections.Generic;
using ScpItdMod.Net;

namespace ScpItdMod.People {
    public enum PeopleOp : byte { Book, Create, Edit, Delete, Select, Names, Looks, Roster, Lobby }

    public readonly struct NameEntry {
        public readonly ushort Entity;
        public readonly string Name;

        public NameEntry(ushort entity, string name) {
            Entity = entity;
            Name = name;
        }
    }

    public sealed class RosterEntry {
        public ushort Entity;
        public string Callsign = "";
        public string Real = "";
        public string Job = "";
        public string Account = "";
        public uint Profile;
        public uint Version;
        public string Look = "";
        public string Notes = "";
        public bool Alive;

        public void Write(WireWriter w) {
            w.U32(Entity);
            w.Text(Callsign);
            w.Text(Real);
            w.Text(Job);
            w.Text(Account);
            w.Var(Profile);
            w.Var(Version);
            w.Text(Look);
            w.Text(Notes);
            w.Bool(Alive);
        }

        public static RosterEntry Read(ref WireReader r) {
            uint entity = r.U32();
            var e = new RosterEntry {
                Callsign = r.Text(256),
                Real = r.Text(ProfileRules.NameMax * 4),
                Job = r.Text(256),
                Account = r.Text(256),
                Profile = (uint)r.Count(int.MaxValue),
                Version = (uint)r.Count(int.MaxValue),
                Look = r.Text(ProfileRules.LookBytes),
                Notes = r.Text(ProfileRules.NotesBytes),
                Alive = r.Bool()
            };
            e.Entity = entity > ushort.MaxValue ? (ushort)0 : (ushort)entity;
            return e;
        }
    }

    public struct PeopleReply {
        public ulong Request;
        public PeopleOp Op;
        public PeopleError Result;
        public ProfileField Field;
    }

    public sealed class PeopleServer {
        public const byte Proto = 4;
        public const int MaxRequest = 8192;
        public const int MaxRoster = 128;
        private const float Rate = 4.0f;
        private const float Burst = 10.0f;
        // Time until the gate has one token again. The client waits this long before it resends a refused request.
        public const double RetryAfterSeconds = 1.0 / Rate;

        private sealed class Peer {
            public readonly RequestSessions Requests = new RequestSessions();
            public Gate Gate;
            public ulong LastMutation;
            public (ulong Low, ulong High) ClientIdentity;
            public byte[] MutationPayload, MutationReply;
        }

        private readonly Dictionary<ulong, Peer> peers = new Dictionary<ulong, Peer>();

        private readonly ulong scopeLow, scopeHigh;
        public PeopleServer(ProfileStore store) {
            Store = store ?? throw new ArgumentNullException(nameof(store));
            Span<byte> identity = stackalloc byte[16]; Guid.NewGuid().TryWriteBytes(identity);
            scopeLow = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(identity);
            scopeHigh = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(identity.Slice(8));
        }

        public ProfileStore Store { get; }

        public bool Locked { get; set; }

        public Func<ulong, uint, bool> InUse { get; set; }

        public Func<ulong, bool> IsGm { get; set; }

        public Action<List<RosterEntry>> FillRoster { get; set; }

        // False while the last write of the profile store to disk has failed. Sent with every book.
        public Func<bool> IsDurable { get; set; }

        private readonly List<RosterEntry> roster = new List<RosterEntry>();

        public void Clear() {
            // Round transport state expires; a completed profile mutation remains committed.
            // Keep its receipt for the same client identity until a later mutation replaces it.
            foreach (Peer peer in peers.Values) { peer.Requests.Clear(); peer.Gate = default; }
        }

        public bool Handle(ulong account, uint nonce, uint seq, ReadOnlySpan<byte> request, double now, WireWriter reply) => Handle(account, nonce, seq, request, now, reply, out _);

        public bool Handle(ulong account, uint nonce, uint seq, ReadOnlySpan<byte> request, double now, WireWriter reply, out PeopleOp op) =>
            Handle(account, null, nonce, seq, request, now, reply, out op);

        public bool Handle(ulong account, object connection, uint nonce, uint seq, ReadOnlySpan<byte> request, double now, WireWriter reply, out PeopleOp op) {
            reply.Clear();
            op = PeopleOp.Book;
            if (account == 0 || request.Length > MaxRequest) { return false; }
            if (!peers.TryGetValue(account, out Peer peer)) {
                peer = new Peer();
                peers.Add(account, peer);
            }

            var reader = new WireReader(request);
            if (reader.U8() != Proto) { return false; }
            op = (PeopleOp)reader.U8();
            var clientIdentity = (Low: reader.U64(), High: reader.U64());
            ulong req = reader.Var();
            if (reader.Failed || (clientIdentity.Low | clientIdentity.High) == 0) { return false; }

            // A refused request was not executed and is not recorded, so the client's resend is accepted later.
            if (!peer.Gate.Take(now, Rate, Burst)) {
                Header(reply, new PeopleReply { Request = req, Op = op, Result = PeopleError.RateLimited });
                return true;
            }
            if (!peer.Requests.Accept(connection ?? peer, nonce, seq, out _)) { return false; }

            bool mutation = op is PeopleOp.Create or PeopleOp.Edit or PeopleOp.Delete or PeopleOp.Select;
            if (mutation && req == 0) { return false; }
            if (mutation && clientIdentity == peer.ClientIdentity && req <= peer.LastMutation) {
                if (req == peer.LastMutation && request.SequenceEqual(peer.MutationPayload)) { reply.Raw(peer.MutationReply); return true; }
                return false;
            }

            PeopleError result;
            ProfileField field = ProfileField.None;
            switch (op) {
                case PeopleOp.Book:
                    result = reader.End ? PeopleError.Ok : PeopleError.Invalid;
                    break;
                case PeopleOp.Create: {
                    ProfileData data = ProfileData.Read(ref reader);
                    result = Malformed(ref reader) ?? (Locked ? PeopleError.Denied : Store.Create(account, data, now, out _, out field));
                    break;
                }
                case PeopleOp.Edit: {
                    uint id = (uint)reader.Count(int.MaxValue);
                    uint baseVersion = (uint)reader.Count(int.MaxValue);
                    ProfileData data = ProfileData.Read(ref reader);
                    result = Malformed(ref reader) ?? (Locked ? PeopleError.Denied : Store.Edit(account, id, baseVersion, data, now, out field));
                    break;
                }
                case PeopleOp.Delete: {
                    uint id = (uint)reader.Count(int.MaxValue);
                    uint baseVersion = (uint)reader.Count(int.MaxValue);
                    result = Malformed(ref reader) ?? (Locked ? PeopleError.Denied :
                        InUse != null && InUse(account, id) ? PeopleError.Busy : Store.Delete(account, id, baseVersion));
                    break;
                }
                case PeopleOp.Select: {
                    uint id = (uint)reader.Count(int.MaxValue);
                    result = Malformed(ref reader) ?? (Locked ? PeopleError.Denied : Store.Select(account, id));
                    break;
                }
                case PeopleOp.Roster:
                    if (!reader.End) {
                        result = PeopleError.Invalid;
                        break;
                    }
                    roster.Clear();
                    bool allowed = IsGm != null && FillRoster != null && IsGm(account);
                    if (allowed) { FillRoster(roster); }
                    WriteRoster(reply, req, roster, allowed ? PeopleError.Ok : PeopleError.Denied);
                    return true;
                default:
                    result = PeopleError.Invalid;
                    break;
            }
            Header(reply, new PeopleReply { Request = req, Op = PeopleOp.Book, Result = result, Field = field });
            reply.U64(scopeLow); reply.U64(scopeHigh); reply.U64(account);
            reply.Bool(IsDurable == null || IsDurable());
            WriteBook(reply, Store.Book(account));
            if (mutation) {
                peer.ClientIdentity = clientIdentity; peer.LastMutation = req; peer.MutationPayload = request.ToArray(); peer.MutationReply = reply.Span.ToArray();
            }
            return true;
        }

        public static void Header(WireWriter writer, in PeopleReply h) {
            writer.U8(Proto);
            writer.Var(h.Request);
            writer.U8((byte)h.Op);
            writer.U8((byte)h.Result);
            writer.U8((byte)h.Field);
        }

        public static bool ReadHeader(ref WireReader reader, out PeopleReply h) {
            h = default;
            if (reader.U8() != Proto) { return false; }
            h.Request = reader.Var();
            h.Op = (PeopleOp)reader.U8();
            h.Result = (PeopleError)reader.U8();
            h.Field = (ProfileField)reader.U8();
            return !reader.Failed;
        }

        public static void WriteBook(WireWriter writer, ProfileBook book) {
            if (book == null) {
                writer.Var(0);
                writer.Var(0);
                return;
            }
            writer.Var(book.Active);
            writer.Var((ulong)book.Count);
            for (int i = 0; i < book.Count; i++) {
                Profile p = book.At(i);
                writer.Var(p.Id);
                writer.Var(p.Version);
                p.Data.Write(writer);
            }
        }

        public static bool ReadBook(ref WireReader reader, out uint active, List<Profile> profiles) {
            profiles.Clear();
            active = (uint)reader.Count(int.MaxValue);
            int count = reader.Count(ProfileRules.PerAccount);
            for (int i = 0; i < count && !reader.Failed; i++) {
                var p = new Profile { Id = (uint)reader.Count(int.MaxValue), Version = (uint)reader.Count(int.MaxValue) };
                p.Data = ProfileData.Read(ref reader);
                profiles.Add(p);
            }
            return !reader.Failed;
        }

        public static void WriteNames(WireWriter writer, PeopleOp op, List<NameEntry> entries) {
            writer.Clear();
            Header(writer, new PeopleReply { Op = op });
            writer.Var((ulong)entries.Count);
            for (int i = 0; i < entries.Count; i++) {
                writer.U32(entries[i].Entity);
                writer.Text(entries[i].Name);
            }
        }

        public static bool ReadNames(ref WireReader reader, int maxText, List<NameEntry> entries) {
            entries.Clear();
            int count = reader.Count(4096);
            for (int i = 0; i < count && !reader.Failed; i++) {
                uint entity = reader.U32();
                string name = reader.Text(maxText);
                if (entity > ushort.MaxValue) { return false; }
                entries.Add(new NameEntry((ushort)entity, name));
            }
            return !reader.Failed && reader.End;
        }

        public static void WriteRoster(WireWriter writer, ulong req, List<RosterEntry> entries, PeopleError result = PeopleError.Ok) {
            writer.Clear();
            Header(writer, new PeopleReply { Request = req, Op = PeopleOp.Roster, Result = result });
            int n = Math.Min(entries.Count, MaxRoster);
            writer.Var((ulong)n);
            for (int i = 0; i < n; i++) { entries[i].Write(writer); }
        }

        public static bool ReadRoster(ref WireReader reader, List<RosterEntry> entries) {
            entries.Clear();
            int count = reader.Count(MaxRoster);
            for (int i = 0; i < count && !reader.Failed; i++) { entries.Add(RosterEntry.Read(ref reader)); }
            return !reader.Failed && reader.End;
        }

        private static PeopleError? Malformed(ref WireReader reader) => reader.Failed || !reader.End ? PeopleError.Invalid : null;
    }
}
