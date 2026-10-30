using System;
using System.Collections.Generic;
using ScpItdMod.Net;

namespace ScpItdMod.Docs {
    public readonly struct DocPush {
        public readonly object Session;
        public readonly byte[] Payload;

        public DocPush(object session, byte[] payload) {
            Session = session;
            Payload = payload;
        }
    }

    public sealed class DocServer {
        public const byte Proto = 1;
        public const int MaxRequest = 32 * 1024;
        public const int MaxWatched = 64;
        private const float Rate = 20.0f;
        private const float Burst = 40.0f;

        private readonly Dictionary<object, Peer> peers = new Dictionary<object, Peer>(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<ulong, List<Peer>> watchers = new Dictionary<ulong, List<Peer>>();
        private readonly List<object> abandoned = new List<object>();
        private readonly List<ulong> found = new List<ulong>();
        private readonly WireWriter scratch = new WireWriter(1024);
        private readonly int[] slots = new int[DocType.MaxFields];
        private readonly string[] values = new string[DocType.MaxFields];

        public DocServer(DocStore store) => Store = store ?? throw new ArgumentNullException(nameof(store));

        public DocStore Store { get; }

        public int Watching(object session) => session != null && peers.TryGetValue(session, out Peer peer) ? peer.Watched.Count : 0;

        public void Drop(object session) {
            if (session == null || !peers.TryGetValue(session, out Peer peer)) { return; }
            foreach (ulong watched in peer.Watched) { Unwatch(watched, peer); }
            peers.Remove(session);
        }

        public void Prune(Predicate<object> connected) {
            if (connected == null) { throw new ArgumentNullException(nameof(connected)); }
            abandoned.Clear();
            foreach (var entry in peers) {
                if (!connected(entry.Key)) { abandoned.Add(entry.Key); }
            }
            foreach (object session in abandoned) { Drop(session); }
            abandoned.Clear();
        }

        public void Clear() {
            peers.Clear();
            watchers.Clear();
            abandoned.Clear();
        }

        public bool Handle(in Actor actor, object session, uint nonce, uint seq, ReadOnlySpan<byte> request, double now,
            Func<object, Actor> currentActor, WireWriter reply, List<DocPush> pushes) {
            reply.Clear();
            pushes.Clear();
            if (actor.Id == 0 || session == null || currentActor == null || request.Length > MaxRequest) { return false; }
            Peer peer = PeerOf(session);
            if (peer.NonceBound && peer.Nonce != nonce) { return false; }
            if (!peer.Gate.Take(now, Rate, Burst)) { return false; }
            if (!peer.NonceBound) { peer.Nonce = nonce; peer.NonceBound = true; }
            if (!peer.Replay.Accept(seq)) { return false; }
            if (peer.Actor.Id != 0 && peer.Actor.Id != actor.Id) {
                foreach (ulong watched in peer.Watched) { Unwatch(watched, peer); }
                peer.Watched.Clear();
                peer.Order.Clear();
            }
            peer.Actor = actor;

            var reader = new WireReader(request);
            if (reader.U8() != Proto) { return false; }
            var op = (DocOp)reader.U8();
            ulong req = reader.Var();
            if (reader.Failed) { return false; }

            DocError result;
            ulong touched = 0;
            switch (op) {
                case DocOp.Create: {
                    string type = reader.Text(DocLimits.TypeIdChars * 4);
                    ulong readRoles = reader.U64();
                    ulong writeRoles = reader.U64();
                    int count = reader.Count(DocType.MaxFields);
                    string[] fields = new string[count];
                    for (int i = 0; i < count; i++) { fields[i] = reader.Text(DocLimits.FieldChars * 4); }
                    result = Malformed(ref reader) ?? Remote(type) ?? Store.Create(actor, type, fields, readRoles, writeRoles, now, out touched);
                    if (result == DocError.Ok) { Watch(peer, touched); }
                    break;
                }
                case DocOp.Read: {
                    touched = reader.Var();
                    result = Malformed(ref reader) ?? Remote(touched) ?? Store.Read(actor, touched, now, out _);
                    if (result == DocError.Ok) { Watch(peer, touched); }
                    break;
                }
                case DocOp.Edit: {
                    touched = reader.Var();
                    uint baseRev = (uint)reader.Count(int.MaxValue);
                    int count = reader.Count(DocType.MaxFields);
                    for (int i = 0; i < count; i++) {
                        slots[i] = reader.Count(DocType.MaxFields - 1);
                        values[i] = reader.Text(DocLimits.FieldChars * 4);
                    }
                    result = Malformed(ref reader) ?? Remote(touched) ?? Store.Edit(actor, touched, baseRev,
                        new ReadOnlySpan<int>(slots, 0, count), new ReadOnlySpan<string>(values, 0, count), now, out _);
                    Array.Clear(values, 0, count);
                    break;
                }
                case DocOp.Revert: {
                    touched = reader.Var();
                    uint rev = (uint)reader.Count(int.MaxValue);
                    result = Malformed(ref reader) ?? Remote(touched) ?? Store.Revert(actor, touched, rev, now, out _);
                    break;
                }
                case DocOp.Certify:
                case DocOp.Unseal:
                case DocOp.Delete:
                case DocOp.Restore: {
                    touched = reader.Var();
                    result = Malformed(ref reader) ?? Remote(touched) ?? op switch {
                        DocOp.Certify => Store.Certify(actor, touched, now),
                        DocOp.Unseal => Store.Unseal(actor, touched, now),
                        DocOp.Delete => Store.Delete(actor, touched, now),
                        _ => Store.Restore(actor, touched, now)
                    };
                    break;
                }
                case DocOp.Present: {
                    touched = reader.Var();
                    ulong viewer = reader.U64();
                    int seconds = reader.Count(ushort.MaxValue);
                    result = Malformed(ref reader) ?? Remote(touched) ?? Store.Present(actor, touched, viewer, seconds, now);
                    break;
                }
                case DocOp.Find: {
                    string type = reader.Text(DocLimits.TypeIdChars * 4);
                    string key = reader.Text(256);
                    string value = reader.Text(DocLimits.FieldChars * 4);
                    result = Malformed(ref reader) ?? Remote(type) ?? Store.Find(actor, type, key, value, now, found);
                    Header(reply, req, op, result);
                    if (result == DocError.Ok) {
                        reply.Var((ulong)found.Count);
                        for (int i = 0; i < found.Count; i++) { reply.Var(found[i]); }
                    }
                    return true;
                }
                default:
                    result = DocError.Invalid;
                    break;
            }

            Header(reply, req, op, result);
            Doc doc = touched != 0 ? Store.Peek(touched) : null;
            if (result == DocError.Ok && doc != null && Store.CanRead(actor, doc, now)) { View(reply, actor, doc); }
            if (result == DocError.Ok && doc != null && op != DocOp.Read && op != DocOp.Create && op != DocOp.Present) {
                Broadcast(doc, peer, currentActor, now, pushes);
            }
            return true;
        }

        public static void Header(WireWriter writer, ulong req, DocOp op, DocError result) {
            writer.U8(Proto);
            writer.Var(req);
            writer.U8((byte)op);
            writer.U8((byte)result);
        }

        public static void View(WireWriter writer, in Actor actor, Doc doc) {
            writer.Var(doc.Id);
            writer.Text(doc.Type.Id);
            writer.Var(doc.Rev);
            writer.U8((byte)((doc.Sealed ? 1 : 0) | (DocStore.Genuine(doc) ? 2 : 0) | (doc.Deleted ? 4 : 0) |
                (doc.Owner == actor.Id ? 8 : 0) | (doc.Certifier != 0 ? 16 : 0)));
            int visible = 0;
            for (int i = 0; i < doc.Type.Fields.Length; i++) {
                if (DocStore.CanSee(actor, doc, i)) { visible++; }
            }
            writer.Var((ulong)visible);
            for (int i = 0; i < doc.Type.Fields.Length; i++) {
                if (!DocStore.CanSee(actor, doc, i)) { continue; }
                writer.Var((ulong)i);
                writer.Text(doc.Value(i));
            }
        }

        private void Broadcast(Doc doc, Peer author, Func<object, Actor> currentActor, double now, List<DocPush> pushes) {
            if (!watchers.TryGetValue(doc.Id, out List<Peer> list)) { return; }
            for (int i = list.Count - 1; i >= 0; i--) {
                Peer peer = list[i];
                if (ReferenceEquals(peer, author)) { continue; }
                Actor verified = currentActor(peer.Session);
                bool allowed = verified.Id != 0 && verified.Id == peer.Actor.Id && Store.CanRead(verified, doc, now);
                scratch.Clear();
                if (allowed) {
                    Header(scratch, 0, DocOp.Read, DocError.Ok);
                    View(scratch, verified, doc);
                } else {
                    Header(scratch, 0, DocOp.Read, DocError.Denied);
                    scratch.Var(doc.Id);
                    peer.Watched.Remove(doc.Id);
                    list.RemoveAt(i);
                }
                pushes.Add(new DocPush(peer.Session, scratch.Span.ToArray()));
            }
            if (list.Count == 0) { watchers.Remove(doc.Id); }
        }

        private Peer PeerOf(object session) {
            if (!peers.TryGetValue(session, out Peer peer)) {
                peer = new Peer { Session = session };
                peers.Add(session, peer);
            }
            return peer;
        }

        private void Watch(Peer peer, ulong doc) {
            if (peer.Watched.Contains(doc)) { return; }
            if (peer.Watched.Count >= MaxWatched) {
                ulong oldest = peer.Order.Dequeue();
                while (!peer.Watched.Contains(oldest)) { oldest = peer.Order.Dequeue(); }
                peer.Watched.Remove(oldest);
                Unwatch(oldest, peer);
            }
            peer.Watched.Add(doc);
            peer.Order.Enqueue(doc);
            if (peer.Order.Count > MaxWatched * 4) {
                peer.Order.Clear();
                foreach (ulong kept in peer.Watched) { peer.Order.Enqueue(kept); }
            }
            if (!watchers.TryGetValue(doc, out List<Peer> list)) {
                list = new List<Peer>(2);
                watchers.Add(doc, list);
            }
            list.Add(peer);
        }

        private void Unwatch(ulong doc, Peer peer) {
            if (!watchers.TryGetValue(doc, out List<Peer> list)) { return; }
            list.Remove(peer);
            if (list.Count == 0) { watchers.Remove(doc); }
        }

        private static DocError? Malformed(ref WireReader reader) => reader.Failed || !reader.End ? DocError.Invalid : null;

        private DocError? Remote(string type) => Store.TypeOf(type)?.Remote == true ? null : DocError.NotFound;
        private DocError? Remote(ulong id) => Store.Peek(id) is Doc doc && !doc.Type.Remote ? DocError.NotFound : null;

        private sealed class Peer {
            public Actor Actor;
            public object Session;
            public uint Nonce;
            public bool NonceBound;
            public Replay Replay;
            public Gate Gate;
            public readonly HashSet<ulong> Watched = new HashSet<ulong>();
            public readonly Queue<ulong> Order = new Queue<ulong>();
        }
    }
}
