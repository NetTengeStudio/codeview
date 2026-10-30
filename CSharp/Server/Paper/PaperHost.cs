using System;
using System.Collections.Generic;
using System.Globalization;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using ScpItdMod.Core;
using ScpItdMod.Docs;
using ScpItdMod.Net;

namespace ScpItdMod.Paper {
    public static class PaperHost {
        private const string DocTagPrefix = "scpitd_doc_";
        private const double FaceEvery = 3.0;

        private sealed class Peer {
            public readonly RequestSessions Requests = new RequestSessions();
            public Gate Gate;
            public Gate Shows;
        }

        private sealed class Face {
            public uint Rev = PaperRules.Unknown;
            public string Body = "";
            public string Marks = "";
            public double Pushed = double.NegativeInfinity;
            public bool Pending;
        }

        private static readonly Dictionary<ulong, Peer> peers = new Dictionary<ulong, Peer>();
        private static readonly Dictionary<Item, Face> faces = new Dictionary<Item, Face>();
        private static readonly Dictionary<ulong, Item> owners = new Dictionary<ulong, Item>();
        private static readonly List<Item> pending = new List<Item>();
        private static readonly FaceAsk[] asks = new FaceAsk[PaperRules.MaxAsks];
        private static readonly WireWriter out_ = new WireWriter(4096);
        private static readonly int[] bodySlot = { PaperRules.BodySlot };
        private static readonly int[] marksSlot = { PaperRules.MarksSlot };
        private static readonly string[] one = new string[1];

        public static void Attach() => ModNet.ReceiveBytes(PaperModule.Channel, PaperRules.MaxRequest, OnRequest);

        public static void RoundStart() {
            peers.Clear();
            faces.Clear();
            owners.Clear();
            pending.Clear();
        }

        private static void OnRequest(Client sender, uint nonce, uint seq, byte[] payload) {
            DocStore store = DocNet.Store;
            if (store == null) { return; }
            ulong account = Access.Id(sender);
            if (!peers.TryGetValue(account, out Peer peer)) {
                peer = new Peer();
                peers.Add(account, peer);
            }
            double now = Timing.TotalTime;
            if (!peer.Gate.Take(now, 12.0f, 24.0f)) { return; }
            if (!peer.Requests.Accept(sender, nonce, seq, out _)) { return; }
            Flush(now);

            var r = new WireReader(payload);
            if (!PaperRules.Head(ref r, out PaperOp op, out ushort itemId)) { return; }
            if (op == PaperOp.Faces) {
                Faces(sender, store, ref r, now);
                return;
            }
            uint revision = 0, request = 0; bool finalSave = false; string text = null;
            if (op == PaperOp.Save && !PaperRules.ReadSave(ref r, out revision, out finalSave, out text, out request)) { return; }
            Character me = sender.Character;
            Item paper = Entity.FindEntityByID(itemId) as Item;
            if (me == null || !PaperModule.Reaches(me, paper)) {
                if (op == PaperOp.Save) { Saved(sender, itemId, PaperError.Far, 0, request); }
                else { Note(sender, itemId, PaperError.Far); }
                return;
            }
            Doc doc = Resolve(store, account, paper, now);
            if (doc == null) {
                if (op == PaperOp.Save) { Saved(sender, itemId, PaperError.Denied, 0, request); }
                else { Note(sender, itemId, PaperError.Denied); }
                return;
            }
            var holder = new Actor(account, PaperRules.HolderBit, Access.Gm(sender));
            switch (op) {
                case PaperOp.Open:
                    if (!r.End) { return; }
                    SendView(sender, paper, doc, PaperError.Ok, PaperMode.Own, 0);
                    break;
                case PaperOp.Save: {
                    uint rev = revision; bool final = finalSave; string body = text;
                    if (!PaperModule.Writes(me, paper)) {
                        Saved(sender, itemId, PaperError.Hands, doc.Rev, request);
                        return;
                    }
                    if (!PaperRules.ValidBody(body)) {
                        Saved(sender, itemId, PaperError.Long, doc.Rev, request);
                        return;
                    }
                    if (string.Equals(body, doc.Value(PaperRules.BodySlot), StringComparison.Ordinal)) {
                        Saved(sender, itemId, PaperError.Ok, doc.Rev, request);
                        if (final) { Changed(paper, doc, now, true, false); }
                        return;
                    }
                    one[0] = body;
                    DocError result = store.Edit(holder, doc.Id, rev, bodySlot, one, now, out uint next);
                    if (result == DocError.Conflict) {
                        SendView(sender, paper, doc, PaperError.Conflict, PaperMode.Own, 0, request);
                        return;
                    }
                    Saved(sender, itemId, PaperRules.Of(result), result == DocError.Ok ? next : doc.Rev, request);
                    if (result == DocError.Ok) { Changed(paper, doc, now, final, false); }
                    break;
                }
                case PaperOp.Stamp: {
                    if (!PaperRules.ReadStamp(ref r, out ushort x, out ushort y, out short turn)) { return; }
                    string kind = PaperModule.StampKind(PaperModule.HeldStamp(me));
                    if (kind == null) {
                        Note(sender, itemId, PaperError.NoStamp);
                        return;
                    }
                    MarkError marked = Marks.Add(doc.Value(PaperRules.MarksSlot), new Mark(kind, x, y, turn), out string marks);
                    if (marked != MarkError.Ok) {
                        SendView(sender, paper, doc, PaperRules.Of(marked), PaperMode.Own, 0);
                        return;
                    }
                    one[0] = marks;
                    var clerk = new Actor(account, PaperRules.HolderBit | PaperRules.StampBit, holder.Gm);
                    DocError result = store.Edit(clerk, doc.Id, doc.Rev, marksSlot, one, now, out _);
                    SendView(sender, paper, doc, PaperRules.Of(result), PaperMode.Own, 0);
                    if (result == DocError.Ok) { Changed(paper, doc, now, true, true); }
                    break;
                }
                case PaperOp.Show: {
                    if (!PaperRules.ReadShow(ref r, out ushort targetId)) { return; }
                    if (!peer.Shows.Take(now, 0.5f, 2.0f)) { return; }
                    Client viewer = ClientOf(Entity.FindEntityByID(targetId) as Character);
                    Character target = viewer?.Character;
                    if (target == null || ReferenceEquals(target, me) || target.IsDead || target.IsUnconscious ||
                        Vector2.Distance(me.WorldPosition, target.WorldPosition) > PaperRules.ShowRange) {
                        Note(sender, itemId, PaperError.Target);
                        return;
                    }
                    if (store.Present(holder, doc.Id, Access.Id(viewer), PaperRules.ShowSeconds, now) != DocError.Ok) {
                        if (op == PaperOp.Save) { Saved(sender, itemId, PaperError.Denied, 0, request); }
                else { Note(sender, itemId, PaperError.Denied); }
                        return;
                    }
                    SendView(viewer, paper, doc, PaperError.Ok, PaperMode.Shown, me.ID);
                    Note(sender, itemId, PaperError.Ok);
                    break;
                }
            }
        }

        private static void Faces(Client sender, DocStore store, ref WireReader r, double now) {
            int n = PaperRules.ReadFaces(ref r, asks);
            for (int i = 0; i < n; i++) {
                Item paper = Entity.FindEntityByID(asks[i].Item) as Item;
                if (!PaperModule.IsPaper(paper)) { continue; }
                Face face = FaceOf(store, paper, now);
                if (face.Rev != asks[i].Rev) { Push(sender, paper, face, false); }
            }
        }

        private static Face FaceOf(DocStore store, Item paper, double now) {
            if (!faces.TryGetValue(paper, out Face face)) {
                face = new Face();
                faces.Add(paper, face);
            }
            ulong id = DocOf(paper);
            Doc doc = id == 0 ? null : store.Peek(id);
            if (doc == null) {
                face.Rev = 0;
                face.Body = "";
                face.Marks = "";
            } else if (face.Rev != doc.Rev) {
                Fill(face, doc);
            }
            return face;
        }

        private static void Fill(Face face, Doc doc) {
            face.Rev = doc.Rev;
            face.Body = Scramble.Of(doc.Value(PaperRules.BodySlot), doc.Id);
            face.Marks = doc.Value(PaperRules.MarksSlot);
        }

        private static void Changed(Item paper, Doc doc, double now, bool force, bool thump) {
            if (!faces.TryGetValue(paper, out Face face)) {
                face = new Face();
                faces.Add(paper, face);
            }
            Fill(face, doc);
            if (!force && now - face.Pushed < FaceEvery) {
                if (!face.Pending) {
                    face.Pending = true;
                    pending.Add(paper);
                }
                return;
            }
            Broadcast(paper, face, now, thump);
        }

        private static void Flush(double now) {
            for (int i = pending.Count - 1; i >= 0; i--) {
                Item paper = pending[i];
                if (paper.Removed || !faces.TryGetValue(paper, out Face face)) {
                    pending.RemoveAt(i);
                    continue;
                }
                if (now - face.Pushed < FaceEvery) { continue; }
                pending.RemoveAt(i);
                Broadcast(paper, face, now, false);
            }
        }

        private static void Broadcast(Item paper, Face face, double now, bool thump) {
            face.Pushed = now;
            face.Pending = false;
            if (GameMain.Server == null) { return; }
            Vector2 at = paper.WorldPosition;
            float range = PaperRules.FaceRange * PaperRules.FaceRange;
            var clients = GameMain.Server.ConnectedClients;
            for (int i = 0; i < clients.Count; i++) {
                Character c = clients[i].Character;
                if (c == null || Vector2.DistanceSquared(c.WorldPosition, at) > range) { continue; }
                Push(clients[i], paper, face, thump);
            }
        }

        private static void Push(Client client, Item paper, Face face, bool thump) {
            PaperRules.Face(out_, new PaperFace { Item = paper.ID, Rev = face.Rev, Thump = thump, Body = face.Body, Marks = face.Marks });
            ModNet.SendBytes(PaperModule.Channel, client.Connection, out_.Span, DeliveryMethod.Reliable);
        }

        private static Doc Resolve(DocStore store, ulong account, Item paper, double now) {
            ulong id = DocOf(paper);
            Doc doc = id == 0 ? null : store.Peek(id);
            if (doc != null && owners.TryGetValue(id, out Item owner) && !ReferenceEquals(owner, paper) && !owner.Removed) {
                Doc copy = Create(store, account, paper, id, doc.Value(PaperRules.BodySlot), doc.Value(PaperRules.MarksSlot), now);
                return copy;
            }
            if (doc != null) {
                if (doc.Sealed) { store.Unseal(new Actor(0, 0, true), id, now); }
                owners[id] = paper;
                return doc;
            }
            return Create(store, account, paper, id, "", "", now);
        }

        private static Doc Create(DocStore store, ulong account, Item paper, ulong old, string body, string marks, double now) {
            var creator = new Actor(account, PaperRules.HolderBit | (marks.Length > 0 ? PaperRules.StampBit : 0UL));
            if (store.Create(creator, PaperRules.TypeId, new[] { body, marks }, PaperRules.HolderBit, PaperRules.HolderBit, now, out ulong id) != DocError.Ok) {
                return null;
            }
            if (old != 0) { paper.RemoveTag(Tag(old)); }
            paper.AddTag(Tag(id));
            owners[id] = paper;
            return store.Peek(id);
        }

        private static Identifier Tag(ulong id) => (DocTagPrefix + id.ToString(CultureInfo.InvariantCulture)).ToIdentifier();

        private static void SendView(Client client, Item paper, Doc doc, PaperError result, PaperMode mode, ushort presenter, uint request = 0) {
            PaperRules.View(out_, new PaperView {
                Item = paper.ID, Result = result, Mode = mode, Rev = doc.Rev, Presenter = presenter, Request = request,
                Body = doc.Value(PaperRules.BodySlot), Marks = doc.Value(PaperRules.MarksSlot)
            });
            ModNet.SendBytes(PaperModule.Channel, client.Connection, out_.Span, DeliveryMethod.Reliable);
        }

        private static void Saved(Client client, ushort item, PaperError result, uint rev, uint request) {
            PaperRules.Saved(out_, item, result, rev, request);
            ModNet.SendBytes(PaperModule.Channel, client.Connection, out_.Span, DeliveryMethod.Reliable);
        }

        private static void Note(Client client, ushort item, PaperError result) {
            PaperRules.Note(out_, item, result);
            ModNet.SendBytes(PaperModule.Channel, client.Connection, out_.Span, DeliveryMethod.Reliable);
        }

        public static bool Blank(Item paper) {
            DocStore store = DocNet.Store;
            ulong id = DocOf(paper);
            Doc doc = id == 0 ? null : store?.Peek(id);
            return id == 0 || doc == null || doc.Value(PaperRules.BodySlot).Length == 0 && doc.Value(PaperRules.MarksSlot).Length == 0;
        }

        public static bool Imprint(Item paper, ulong account, string body, double now) {
            DocStore store = DocNet.Store;
            if (store == null || !PaperModule.IsPaper(paper) || !Blank(paper)) { return false; }
            string text = body ?? "";
            if (!PaperRules.ValidBody(text)) { return false; }
            Doc doc = Create(store, account, paper, DocOf(paper), text, "", now);
            if (doc == null) { return false; }
            Changed(paper, doc, now, true, false);
            return true;
        }

        private static ulong DocOf(Item paper) {
            foreach (Identifier tag in paper.GetTags()) {
                string value = tag.Value;
                if (!value.StartsWith(DocTagPrefix, StringComparison.OrdinalIgnoreCase)) { continue; }
                if (ulong.TryParse(value.AsSpan(DocTagPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out ulong id)) { return id; }
            }
            return 0;
        }

        private static Client ClientOf(Character c) {
            if (c == null || GameMain.Server == null) { return null; }
            var clients = GameMain.Server.ConnectedClients;
            for (int i = 0; i < clients.Count; i++) {
                if (ReferenceEquals(clients[i].Character, c)) { return clients[i]; }
            }
            return null;
        }
    }
}
