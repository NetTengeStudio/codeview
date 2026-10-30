using System;
using Barotrauma;
using System.Collections.Generic;
using Barotrauma.Networking;
using ScpItdMod.Core;
using ScpItdMod.Net;

namespace ScpItdMod.People {
    public sealed class Draft {
        public uint Profile;
        public uint Base;
        public ProfileData Data;
        public PeopleError Error;
        public ProfileField Field;
        public bool Conflict;
    }

    public static class PeopleClient {
        public const string Channel = "scpitd.people";
        private const int MaxText = ProfileRules.LookBytes;
        // One reply window for every mutation. The server answers or stays silent (rate gate, lost packet);
        // the same payload is resent each window, and the server replays its receipt for a duplicate.
        private const double ReplySeconds = 3.0;

        private static readonly Dictionary<ushort, string> names = new Dictionary<ushort, string>();
        private static readonly Dictionary<ushort, string> looks = new Dictionary<ushort, string>();
        private static readonly Dictionary<ushort, string> lobbyNames = new Dictionary<ushort, string>();
        private static Dictionary<uint, Draft> drafts = new Dictionary<uint, Draft>();
        private sealed class Mutation {
            public PeopleOp Op; public uint Profile; public ProfileData Data;
            public double Deadline; public byte[] Payload;
            public HashSet<uint> Before;
        }
        private static Dictionary<ulong, Mutation> pending = new Dictionary<ulong, Mutation>();
        private static readonly List<Profile> incomingBook = new List<Profile>();
        private static object connection;
        private static readonly Dictionary<(ulong Low, ulong High, ulong Owner), Dictionary<uint, Draft>> draftBooks = new Dictionary<(ulong, ulong, ulong), Dictionary<uint, Draft>>();
        private static (ulong Low, ulong High, ulong Owner) scope;
        private static bool scopeKnown;
        private static readonly Dictionary<(ulong Low, ulong High, ulong Owner), Dictionary<ulong, Mutation>> pendingBooks = new Dictionary<(ulong, ulong, ulong), Dictionary<ulong, Mutation>>();
        private static readonly byte[] clientIdentity = Guid.NewGuid().ToByteArray();
        private static readonly List<byte[]> retries = new List<byte[]>(1);
        private static bool bookPending;
        private static double bookDeadline;
        private static readonly List<NameEntry> scratch = new List<NameEntry>();
        private static readonly WireWriter request = new WireWriter(1024);
        private static ulong nextReq;
        private static uint deferredSelect;

        public static readonly List<Profile> Book = new List<Profile>();
        public static readonly List<RosterEntry> Roster = new List<RosterEntry>();

        public static uint Active { get; private set; }

        public static bool Loaded { get; private set; }

        public static PeopleReply Last { get; private set; }

        public static uint LastProfile { get; private set; }

        public static int Revision { get; private set; }

        public static int BookRevision { get; private set; }

        public static int RosterRevision { get; private set; }

        public static int LobbyRevision { get; private set; }

        public static bool RosterDenied { get; private set; }

        public static bool Waiting => pending.Count > 0;

        // False when the server reports that its last write of the profile store to disk failed.
        // Changes are in server memory, but they may not survive a restart.
        public static bool Durable { get; private set; } = true;

        public static void Attach() => ModNet.ReceiveBytes(Channel, 1 << 18, OnMessage);

        public static void Reset() {
            names.Clear();
            looks.Clear();
            lobbyNames.Clear();
            Roster.Clear();
            Revision++;
            LobbyRevision++;
        }

        public static void Forget() {
            Reset();
            Book.Clear();
            pending.Clear(); pendingBooks.Clear(); drafts.Clear(); draftBooks.Clear(); scopeKnown = false; incomingBook.Clear(); retries.Clear(); bookPending = false; deferredSelect = 0;
            Last = default; LastProfile = 0;
            Loaded = false;
            Active = 0;
            BookRevision++;
        }

        public static bool RealName(ushort entity, out string name) => names.TryGetValue(entity, out name);

        public static bool Look(ushort entity, out string text) => looks.TryGetValue(entity, out text);

        // Keyed by the lobby client's SessionId. Empty until the server has sent
        // a list, in which case the caller keeps the vanilla nickname.
        public static bool LobbyName(byte session, out string name) => lobbyNames.TryGetValue(session, out name);

        public static IReadOnlyDictionary<ushort, string> Names => names;

        public static Dictionary<ushort, string>.KeyCollection LookIds => looks.Keys;

        public static Profile Find(uint id) {
            for (int i = 0; i < Book.Count; i++) {
                if (Book[i].Id == id) { return Book[i]; }
            }
            return null;
        }

        public static Draft DraftOf(uint profile) => drafts.TryGetValue(profile, out Draft d) ? d : null;

        public static Draft Open(uint profile) {
            if (drafts.TryGetValue(profile, out Draft d)) { return d; }
            Profile p = Find(profile);
            if (profile != 0 && p == null) { return null; }
            d = new Draft { Profile = profile, Base = p?.Version ?? 0, Data = p?.Data.Clone() ?? new ProfileData() };
            drafts[profile] = d;
            return d;
        }

        public static bool Dirty(uint profile) {
            if (!drafts.TryGetValue(profile, out Draft d)) { return false; }
            Profile p = Find(profile);
            return p == null ? profile == 0 : !d.Data.Same(p.Data) || d.Base != p.Version;
        }

        public static void Discard(uint profile) => drafts.Remove(profile);

        public static void RequestBook() => Send(PeopleOp.Book, 0, null);

        public static void Commit(Draft d) {
            if (d == null || !Loaded || !scopeKnown) { return; }
            ProfileData data = ProfileRules.Normalized(d.Data);
            PeopleError check = ProfileRules.Check(data, out ProfileField field);
            d.Error = check;
            d.Field = field;
            if (check != PeopleError.Ok) {
                Revision++;
                return;
            }
            if (d.Profile == 0) {
                Send(PeopleOp.Create, 0, w => data.Write(w));
            } else {
                uint id = d.Profile;
                uint version = d.Base;
                Send(PeopleOp.Edit, id, w => { w.Var(id); w.Var(version); data.Write(w); });
            }
        }

        public static void Rebase(Draft d) {
            Profile p = Find(d.Profile);
            if (p == null) { return; }
            d.Base = p.Version;
            d.Conflict = false;
            d.Error = PeopleError.Ok;
            Revision++;
        }

        public static void Delete(uint id) {
            Profile p = Find(id);
            if (p == null) { return; }
            uint version = p.Version;
            Send(PeopleOp.Delete, id, w => { w.Var(id); w.Var(version); });
        }

        // A selection made while another mutation is in flight is kept and sent when the
        // reply arrives, so the UI and the server agree. Send alone would drop it silently.
        public static void Select(uint id) {
            if (Waiting) { deferredSelect = id; return; }
            Send(PeopleOp.Select, id, w => w.Var(id));
        }

        public static void RequestRoster() => Send(PeopleOp.Roster, 0, null);

        public static void Tick() {
            CheckConnection();
            double now = Timing.TotalTime;
            retries.Clear();
            foreach (Mutation mutation in pending.Values) {
                if (now < mutation.Deadline) { continue; }
                mutation.Deadline = now + ReplySeconds;
                Last = new PeopleReply { Op = mutation.Op, Result = PeopleError.Timeout }; LastProfile = mutation.Profile;
                Draft d = DraftOf(mutation.Profile); if (d != null) { d.Error = PeopleError.Timeout; }
                retries.Add(mutation.Payload); Revision++;
            }
            if (GameMain.Client != null) {
                for (int i = 0; i < retries.Count; i++) { ModNet.SendBytes(Channel, retries[i], DeliveryMethod.Reliable); }
                if (bookPending && now >= bookDeadline) {
                    Last = new PeopleReply { Op = PeopleOp.Book, Result = PeopleError.Timeout }; LastProfile = 0; Revision++;
                    RequestBook();
                }
            }
            retries.Clear();
            if (deferredSelect != 0 && !Waiting) {
                uint id = deferredSelect;
                deferredSelect = 0;
                Select(id);
            }
        }

        private static void CheckConnection() {
            object current = GameMain.Client;
            if (ReferenceEquals(connection, current)) { return; }
            if (connection != null) {
                if (scopeKnown) { draftBooks[scope] = drafts; pendingBooks[scope] = pending; }
                Reset(); Book.Clear(); pending = new Dictionary<ulong, Mutation>(); incomingBook.Clear(); retries.Clear(); deferredSelect = 0;
                drafts = new Dictionary<uint, Draft>(); scopeKnown = false;
                Loaded = false; Active = 0; Last = default; LastProfile = 0; bookPending = false; BookRevision++;
            }
            connection = current;
        }

        private static void Send(PeopleOp op, uint profile, Action<WireWriter> body) {
            CheckConnection();
            if (op != PeopleOp.Book && op != PeopleOp.Roster && Waiting) { return; }
            request.Clear();
            request.U8(PeopleServer.Proto);
            request.U8((byte)op);
            request.Raw(clientIdentity);
            ulong id = ++nextReq;
            request.Var(id);
            body?.Invoke(request);
            if (op != PeopleOp.Book && op != PeopleOp.Roster) {
                var before = new HashSet<uint>(); for (int i = 0; i < Book.Count; i++) { before.Add(Book[i].Id); }
                pending[id] = new Mutation { Op = op, Profile = profile, Data = DraftOf(profile) is Draft submitted ? ProfileRules.Normalized(submitted.Data) : null, Before = before, Deadline = Timing.TotalTime + ReplySeconds, Payload = request.Span.ToArray() };
            }
            if (op == PeopleOp.Book) { bookPending = true; bookDeadline = Timing.TotalTime + 3; }
            ModNet.SendBytes(Channel, request.Span, DeliveryMethod.Reliable);
        }

        private static void OnMessage(byte[] payload) {
            CheckConnection();
            var reader = new WireReader(payload);
            if (!PeopleServer.ReadHeader(ref reader, out PeopleReply head)) { return; }
            if (head.Result == PeopleError.RateLimited) {
                OnRateLimited(head);
                return;
            }
            switch (head.Op) {
                case PeopleOp.Book:
                    OnBook(ref reader, head);
                    break;
                case PeopleOp.Names:
                    if (!PeopleServer.ReadNames(ref reader, ProfileRules.NameMax * 4, scratch)) { return; }
                    names.Clear();
                    for (int i = 0; i < scratch.Count; i++) { names[scratch[i].Entity] = scratch[i].Name; }
                    Revision++;
                    break;
                case PeopleOp.Looks:
                    if (!PeopleServer.ReadNames(ref reader, MaxText, scratch)) { return; }
                    looks.Clear();
                    for (int i = 0; i < scratch.Count; i++) { looks[scratch[i].Entity] = scratch[i].Name; }
                    Revision++;
                    break;
                case PeopleOp.Lobby:
                    if (!PeopleServer.ReadNames(ref reader, ProfileRules.NameMax * 4, scratch)) { return; }
                    lobbyNames.Clear();
                    for (int i = 0; i < scratch.Count; i++) { lobbyNames[scratch[i].Entity] = scratch[i].Name; }
                    LobbyRevision++;
                    break;
                case PeopleOp.Roster:
                    if (PeopleServer.ReadRoster(ref reader, Roster)) {
                        RosterDenied = head.Result == PeopleError.Denied;
                        RosterRevision++;
                    }
                    break;
            }
        }

        // The server refused the request before executing it. Nothing was recorded, so the same request
        // is sent again once the gate has a token; the reply is not a receipt.
        private static void OnRateLimited(in PeopleReply head) {
            double retry = Timing.TotalTime + PeopleServer.RetryAfterSeconds;
            if (head.Op == PeopleOp.Book) {
                if (bookPending) { bookDeadline = retry; }
                return;
            }
            if (!pending.TryGetValue(head.Request, out Mutation mutation)) { return; }
            mutation.Deadline = retry;
            Last = head;
            LastProfile = mutation.Profile;
            Revision++;
        }

        private static void OnBook(ref WireReader reader, in PeopleReply head) {
            var receivedScope = (Low: reader.U64(), High: reader.U64(), Owner: reader.U64());
            bool durable = reader.Bool();
            if (!PeopleServer.ReadBook(ref reader, out uint active, incomingBook) || !reader.End || receivedScope.Owner == 0 || (receivedScope.Low | receivedScope.High) == 0) { return; }
            Durable = durable;
            if (!scopeKnown || scope != receivedScope) {
                if (scopeKnown) { draftBooks[scope] = drafts; pendingBooks[scope] = pending; }
                pending = pendingBooks.TryGetValue(receivedScope, out Dictionary<ulong, Mutation> retainedPending) ? retainedPending : new Dictionary<ulong, Mutation>();
                foreach (Mutation mutation in pending.Values) { mutation.Deadline = Timing.TotalTime; }
                drafts = draftBooks.TryGetValue(receivedScope, out Dictionary<uint, Draft> retained) ? retained : new Dictionary<uint, Draft>();
                scope = receivedScope; scopeKnown = true; draftBooks[scope] = drafts; pendingBooks[scope] = pending;
            }
            Book.Clear(); Book.AddRange(incomingBook); incomingBook.Clear(); bookPending = false;
            Active = active;
            Loaded = true;
            Last = head;
            LastProfile = 0;
            if (pending.Remove(head.Request, out var sent)) {
                LastProfile = sent.Profile;
                Settle(sent, head);
            }
            BookRevision++;
            Revision++;
        }

        private static void Settle(Mutation mutation, in PeopleReply head) {
            if (mutation.Op != PeopleOp.Create && mutation.Op != PeopleOp.Edit) { return; }
            Draft d = DraftOf(mutation.Profile);
            if (head.Result != PeopleError.Ok) {
                if (d != null) { d.Error = head.Result; d.Field = head.Field; d.Conflict = head.Result == PeopleError.Conflict; }
                return;
            }
            Profile saved = mutation.Op == PeopleOp.Create ? Book.Find(p => !mutation.Before.Contains(p.Id) && p.Data.Same(mutation.Data)) : Find(mutation.Profile);
            if (saved == null) { return; }
            LastProfile = saved.Id;
            if (d != null) {
                drafts.Remove(mutation.Profile);
                if (!d.Data.Same(mutation.Data)) {
                    d.Profile = saved.Id; d.Base = saved.Version; d.Error = PeopleError.Ok; d.Conflict = false;
                    drafts[saved.Id] = d;
                }
            }
            if (mutation.Op == PeopleOp.Create && Active != saved.Id) { Select(saved.Id); }
        }
    }
}
