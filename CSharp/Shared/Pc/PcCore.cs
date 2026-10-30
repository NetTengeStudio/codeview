using System;
using System.Collections.Generic;
using System.Globalization;
using ScpItdMod.Docs;
using ScpItdMod.Net;

namespace ScpItdMod.Pc {
    public struct PcActor {
        public ulong Id;
        public uint Profile;
        public int Clearance;
        public bool Gm;
        public bool Card;
        public string Name;

        public Actor Doc => new Actor(Id, PcRoles.Of(Clearance), Gm);
    }

    public sealed class PcPlace {
        public ushort Computer;
        public Guid Server;
        public uint Epoch;
        public string Name = "";
        public string Disk = "";
        public bool Powered = true;
        public bool ServerOnline = true;
        public readonly List<PcPrinter> Printers = new List<PcPrinter>();
    }

    public sealed class PcCore {
        public const string NodeType = "pc.node";
        public const string DossierType = "pc.dossier";
        public const int DiskSlot = 0, ParentSlot = 1, NameSlot = 2, KindSlot = 3, LevelSlot = 4, BodySlot = 5;
        public const int SubjectSlot = 0, StatusSlot = 1, DLevelSlot = 2, NotesSlot = 3, ClosedSlot = 4;
        public const int ClosedLevel = 3;
        public const int CreateDossierLevel = 2;
        private const int Remembered = 64;

        private static readonly Actor System = new Actor(0, ulong.MaxValue, true);

        private readonly DocStore store;
        private readonly PrintQueue queue;
        private readonly List<ulong> found = new List<ulong>();
        private readonly List<ulong> found2 = new List<ulong>();
        private readonly Dictionary<(ulong, uint, uint), Cached> done = new Dictionary<(ulong, uint, uint), Cached>();
        private readonly Queue<(ulong, uint, uint)> doneOrder = new Queue<(ulong, uint, uint)>();
        private readonly struct Cached {
            public readonly PcError Error;
            public readonly ulong Id;
            private readonly PcOp op;
            private readonly ushort computer;
            private readonly string disk;
            private readonly ulong target;
            private readonly int qualifier;
            private readonly string text;
            private readonly int clearance;
            private readonly bool gm;
            private readonly bool card;

            public Cached((PcError Error, ulong Id) reply, PcActor who, PcOp op, PcPlace place, ulong target, int qualifier, string text) {
                Error = reply.Error; Id = reply.Id;
                this.op = op; computer = place.Computer; disk = place.Disk;
                this.target = target; this.qualifier = qualifier; this.text = text;
                clearance = who.Clearance; gm = who.Gm; card = who.Card;
            }

            public bool Matches(PcActor who, PcOp requested, PcPlace place, ulong id, int option, string content) =>
                op == requested && computer == place.Computer && string.Equals(disk, place.Disk, StringComparison.Ordinal) &&
                target == id && qualifier == option && string.Equals(text, content, StringComparison.Ordinal) &&
                clearance == who.Clearance && gm == who.Gm && card == who.Card;
        }

        public void ClearRequests() {
            done.Clear();
            doneOrder.Clear();
        }
        private readonly int[] one = new int[1];
        private readonly string[] oneValue = new string[1];

        public Func<string, string> SubjectName = key => key;
        public Func<IReadOnlyList<(string Key, string Name)>> Subjects = () => Array.Empty<(string, string)>();

        public PcCore(DocStore store, PrintQueue queue) {
            this.store = store;
            this.queue = queue;
        }

        public PrintQueue Queue => queue;

        public PcError ImportLegacy(ReadOnlySpan<byte> data, double now, out int made) {
            made = 0;
            if (!PcLegacy.Read(data, out List<LegacyDisk> disks)) { return PcError.Invalid; }
            var drafts = new List<DocDraft>();
            foreach (LegacyDisk disk in disks) {
                Children(disk.Key, 0, found);
                bool imported = false;
                foreach (ulong id in found) {
                    Doc node = store.Peek(id);
                    if (node.Value(KindSlot) == "d" && node.Value(BodySlot) == disk.Receipt) { imported = true; break; }
                }
                if (imported) { continue; }
                bool seed = found.Count == 0;
                if (DiskNodes(disk.Key) + disk.Nodes.Count + 1 + (seed ? 4 : 0) > PcLimits.NodesPerDisk || found.Count >= PcLimits.EntriesPerFolder) { made = 0; return PcError.Full; }
                void Add(ulong parent, string name, bool folder, int read, int write, string text) => drafts.Add(new DocDraft(NodeType,
                    new[] { disk.Key, Id(parent), name, folder ? "d" : "f", read.ToString(CultureInfo.InvariantCulture), text }, PcRoles.Level(read), PcRoles.Level(write)));
                if (seed) {
                    Add(0, "Мои документы", true, 0, 0, "");
                    ulong shared = store.NextId + (ulong)drafts.Count;
                    Add(0, "Общие", true, 0, 0, ""); Add(0, "Служебное", true, 2, 2, "");
                    Add(shared, "Прочтите.txt", false, 0, 0, "Рабочая станция Фонда. Файлы из прежней системы находятся в папке «Перенесено».");
                }
                string folderName = "Перенесено";
                for (int suffix = 2; NameTaken(disk.Key, 0, folderName, 0); suffix++) { folderName = "Перенесено " + suffix.ToString(CultureInfo.InvariantCulture); }
                ulong root = store.NextId + (ulong)drafts.Count;
                Add(0, folderName, true, disk.Read, disk.Write, disk.Receipt);
                ulong first = store.NextId + (ulong)drafts.Count;
                foreach (LegacyNode node in disk.Nodes) { Add(node.Parent < 0 ? root : first + (ulong)node.Parent, node.Name, node.Folder, node.Read, node.Write, node.Text); }
                made += disk.Nodes.Count;
            }
            if (drafts.Count == 0) { return PcError.Ok; }
            DocError result = store.CreateBatch(System, global::System.Runtime.InteropServices.CollectionsMarshal.AsSpan(drafts), now);
            if (result != DocError.Ok) { made = 0; }
            return Of(result);
        }

        public static DocType Node() => new DocType(NodeType, 1, new[] {
            new Field("disk", FieldKind.Text, 48, required: true, indexed: true),
            new Field("parent", FieldKind.Text, 20, required: true, indexed: true),
            new Field("name", FieldKind.Text, PcLimits.NameMax, required: true),
            new Field("kind", FieldKind.Text, 1, required: true),
            new Field("level", FieldKind.Int, 2),
            new Field("body", FieldKind.Text, PcLimits.BodyMax, multiline: true)
        }, createRoles: PcRoles.Base);

        public static DocType Dossier() => new DocType(DossierType, 1, new[] {
            new Field("subject", FieldKind.Text, 32, required: true, indexed: true),
            new Field("status", FieldKind.Text, PcLimits.StatusMax),
            new Field("level", FieldKind.Int, 2),
            new Field("notes", FieldKind.Text, PcLimits.NotesMax, multiline: true),
            new Field("closed", FieldKind.Text, PcLimits.NotesMax, multiline: true, readRoles: PcRoles.Level(ClosedLevel), writeRoles: PcRoles.Level(ClosedLevel))
        }, createRoles: PcRoles.Level(CreateDossierLevel), ownerEdits: false,
            ownerReads: false, allowDelegation: false, readPolicy: static d => DossierReadRoles(d),
            createPolicy: static (actor, values) => DossierLevelPolicy(actor, values[DLevelSlot]),
            editPolicy: static (actor, document, slot, value) =>
                slot == DLevelSlot ? DossierLevelPolicy(actor, value) : DocError.Ok);

        private static ulong DossierReadRoles(Doc document) {
            return int.TryParse(document.Value(DLevelSlot), NumberStyles.None, CultureInfo.InvariantCulture, out int level) &&
                level >= 0 && level <= PcLimits.Levels ? PcRoles.Level(level) : 0UL;
        }

        private static DocError DossierLevelPolicy(Actor actor, string value) {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int level) ||
                level < 0 || level > PcLimits.Levels) { return DocError.Invalid; }
            return actor.Gm || (actor.Roles & PcRoles.Level(level)) != 0 ? DocError.Ok : DocError.Denied;
        }

        public static string Id(ulong id) => id.ToString(CultureInfo.InvariantCulture);

        private static int LevelOf(Doc d, int slot) => int.TryParse(d.Value(slot), NumberStyles.Integer, CultureInfo.InvariantCulture, out int l) ? Math.Clamp(l, 0, PcLimits.Levels) : 0;

        private Doc Node(ulong id, string disk) {
            Doc d = store.Peek(id);
            return d != null && d.Type.Id == NodeType && !d.Orphan && d.Value(DiskSlot) == disk ? d : null;
        }

        private void Children(string disk, ulong parent, List<ulong> into) {
            store.Find(System, NodeType, "parent", Id(parent), 0, into);
            for (int i = into.Count - 1; i >= 0; i--) {
                Doc d = store.Peek(into[i]);
                if (d == null || d.Deleted || d.Value(DiskSlot) != disk) { into.RemoveAt(i); }
            }
        }

        public int DiskNodes(string disk) {
            store.Find(System, NodeType, "disk", disk, 0, found2);
            return found2.Count;
        }

        public void Seed(string disk, double now) {
            Children(disk, 0, found);
            if (found.Count > 0) { return; }
            ulong mine = Make(System, disk, 0, "Мои документы", NodeKind.Folder, 0, "", now);
            ulong shared = Make(System, disk, 0, "Общие", NodeKind.Folder, 0, "", now);
            Make(System, disk, 0, "Служебное", NodeKind.Folder, 2, "", now);
            Make(System, disk, shared, "Прочтите.txt", NodeKind.Text, 0,
                "Рабочая станция Фонда.\nФайлы хранятся на диске этой станции. Папка «Служебное» открыта с допуском 2 и выше.\nПечать: Файл → Печать. Камеры и личные дела — в меню «Пуск».", now);
            _ = mine;
        }

        public PcError Publish(string disk, string folderName, string fileName, string body, double now, out ulong id) {
            id = 0;
            Seed(disk, now);
            if (body.Length > PcLimits.BodyMax) { body = body.Substring(0, PcLimits.BodyMax); }
            Children(disk, 0, found);
            ulong folder = 0;
            for (int i = 0; i < found.Count; i++) {
                Doc d = store.Peek(found[i]);
                if (d.Value(KindSlot) == "d" && string.Equals(d.Value(NameSlot), folderName, StringComparison.OrdinalIgnoreCase)) { folder = found[i]; break; }
            }
            if (folder == 0) {
                if (found.Count >= PcLimits.EntriesPerFolder || DiskNodes(disk) >= PcLimits.NodesPerDisk - 1) { return PcError.Full; }
                folder = Make(System, disk, 0, folderName, NodeKind.Folder, 0, "", now);
                if (folder == 0) { return PcError.Invalid; }
            }
            Children(disk, folder, found2);
            if (found2.Count >= PcLimits.EntriesPerFolder || DiskNodes(disk) >= PcLimits.NodesPerDisk) { return PcError.Full; }
            string name = fileName;
            for (int suffix = 2; NameTaken(disk, folder, name, 0); suffix++) { name = fileName + " (" + suffix.ToString(CultureInfo.InvariantCulture) + ")"; }
            id = Make(System, disk, folder, name, NodeKind.Text, 0, body, now);
            return id == 0 ? PcError.Invalid : PcError.Ok;
        }

        public ulong Make(in Actor actor, string disk, ulong parent, string name, NodeKind kind, int level, string body, double now) {
            ulong roles = PcRoles.Level(level);
            string[] values = { disk, Id(parent), name, kind == NodeKind.Folder ? "d" : "f", level.ToString(CultureInfo.InvariantCulture), body ?? "" };
            return store.Create(actor, NodeType, values, roles, roles, now, out ulong id) == DocError.Ok ? id : 0;
        }

        private static PcError Of(DocError e) => e switch {
            DocError.Ok => PcError.Ok,
            DocError.NotFound => PcError.NotFound,
            DocError.Denied => PcError.Denied,
            DocError.Conflict => PcError.Conflict,
            DocError.TooLarge => PcError.TooLarge,
            DocError.Sealed => PcError.Denied,
            _ => PcError.Invalid
        };

        public void Handle(in PcActor who, PcPlace place, PcOp op, uint token, ref WireReader r, double now, WireWriter w, uint epoch = 0) {
            if (!place.Powered || !place.ServerOnline) {
                PcWire.Reply(w, PcReply.Done, token, place.Powered ? PcError.NoServer : PcError.NoPower);
                w.Var(0);
                return;
            }
            Actor a = who.Doc;
            switch (op) {
                case PcOp.Hello: if (r.Failed || !r.End) { return; } Info(who, place, token, now, w); return;
                case PcOp.List: {
                    ulong folder = r.Var();
                    if (r.Failed || !r.End) { return; }
                    Folder(a, place, folder, token, now, w);
                    return;
                }
                case PcOp.Open: {
                    ulong id = r.Var();
                    if (r.Failed || !r.End) { return; }
                    File(a, place, id, token, now, w);
                    return;
                }
                case PcOp.Save: {
                    ulong id = r.Var();
                    uint rev = (uint)r.Var();
                    string text = r.Text(PcLimits.BodyMax * 4);
                    if (r.Failed || !r.End) { return; }
                    Save(a, place, id, rev, text, token, now, w);
                    return;
                }
                case PcOp.Create: {
                    ulong folder = r.Var();
                    var kind = (NodeKind)r.U8();
                    string name = r.Text(PcLimits.NameMax * 4);
                    if (r.Failed || !r.End || kind > NodeKind.Text) { return; }
                    Remember(who, epoch, op, place, folder, (int)kind, name, token, a, now, w);
                    return;
                }
                case PcOp.Rename: {
                    ulong id = r.Var();
                    string name = r.Text(PcLimits.NameMax * 4);
                    if (r.Failed || !r.End) { return; }
                    Remember(who, epoch, op, place, id, 0, name, token, a, now, w);
                    return;
                }
                case PcOp.Delete: {
                    ulong id = r.Var();
                    if (r.Failed || !r.End) { return; }
                    Remember(who, epoch, op, place, id, 0, null, token, a, now, w);
                    return;
                }
                case PcOp.Bin: if (r.Failed || !r.End) { return; } Bin(a, place, token, w); return;
                case PcOp.Restore: {
                    ulong id = r.Var();
                    if (r.Failed || !r.End) { return; }
                    Remember(who, epoch, op, place, id, 0, null, token, a, now, w);
                    return;
                }
                case PcOp.Dossiers: if (r.Failed || !r.End) { return; } Dossiers(a, token, now, w); return;
                case PcOp.Dossier: {
                    ulong id = r.Var();
                    if (r.Failed || !r.End) { return; }
                    DossierView(a, id, token, PcError.Ok, now, w);
                    return;
                }
                case PcOp.SaveDossier: {
                    ulong id = r.Var();
                    uint rev = (uint)r.Var();
                    string status = r.Text(PcLimits.StatusMax * 4);
                    int level = r.U8();
                    string notes = r.Text(PcLimits.NotesMax * 4);
                    bool hasClosed = r.Bool();
                    string closed = hasClosed ? r.Text(PcLimits.NotesMax * 4) : null;
                    if (r.Failed || !r.End) { return; }
                    SaveDossier(a, id, rev, status, level, notes, closed, token, now, w);
                    return;
                }
                case PcOp.NewDossier: {
                    string subject = r.Text(128);
                    if (r.Failed || !r.End) { return; }
                    Remember(who, epoch, op, place, 0, 0, subject, token, a, now, w);
                    return;
                }
                case PcOp.Print: {
                    ulong id = r.Var();
                    uint rev = (uint)r.Var();
                    uint printer = r.U32();
                    if (r.Failed || !r.End || printer > ushort.MaxValue) { return; }
                    PcError e = Print(who, place, id, rev, (ushort)printer, token, now, out uint job);
                    PcWire.Reply(w, PcReply.Done, token, e);
                    w.Var(job);
                    return;
                }
                case PcOp.Cancel: {
                    uint job = r.U32();
                    if (r.Failed || !r.End) { return; }
                    PcError e = queue.Cancel(who.Id, who.Gm, job, now);
                    PcWire.Reply(w, PcReply.Done, token, e);
                    w.Var(job);
                    return;
                }
                case PcOp.Jobs: if (r.Failed || !r.End) { return; } Jobs(who, place, token, now, w); return;
            }
        }

        private void Remember(PcActor who, uint epoch, PcOp op, PcPlace place, ulong target, int qualifier, string content,
            uint token, Actor actor, double now, WireWriter w) {
            var key = (who.Id, epoch, token);
            if (done.TryGetValue(key, out Cached cached)) {
                if (!cached.Matches(who, op, place, target, qualifier, content)) {
                    PcWire.Reply(w, PcReply.Done, token, PcError.Conflict);
                    w.Var(0);
                    return;
                }
            } else {
                PcError error;
                ulong id;
                switch (op) {
                    case PcOp.Create: error = Create(actor, place, target, (NodeKind)qualifier, content, now, out id); break;
                    case PcOp.Rename: error = Rename(actor, place, target, content, now); id = target; break;
                    case PcOp.Delete: error = Delete(actor, place, target, now); id = target; break;
                    case PcOp.Restore: error = Restore(actor, place, target, now); id = target; break;
                    case PcOp.NewDossier: error = NewDossier(actor, content, now, out id); break;
                    default: PcWire.Reply(w, PcReply.Done, token, PcError.Invalid); w.Var(0); return;
                }
                cached = new Cached((error, id), who, op, place, target, qualifier, content);
                done[key] = cached;
                doneOrder.Enqueue(key);
                while (doneOrder.Count > Remembered * 8) { done.Remove(doneOrder.Dequeue()); }
            }
            PcWire.Reply(w, PcReply.Done, token, cached.Error);
            w.Var(cached.Id);
        }

        public void Info(in PcActor who, PcPlace place, uint token, double now, WireWriter w) {
            Seed(place.Disk, now);
            PcWire.Reply(w, PcReply.Info, token, PcError.Ok);
            PcWire.WriteDesktop(w, new DesktopIdentity(place.Server, who.Id, place.Epoch, who.Profile, place.Computer));
            w.Text(place.Name);
            w.Text(who.Name ?? "");
            w.U8((byte)Math.Clamp(who.Clearance, 0, 255));
            w.Bool(who.Card);
            w.Bool(who.Gm);
            w.Text(place.Disk);
            Children(place.Disk, 0, found);
            Actor a = who.Doc;
            int count = 0;
            for (int i = 0; i < found.Count; i++) {
                if (store.CanRead(a, store.Peek(found[i]), now)) { count++; }
            }
            w.Var((ulong)count);
            for (int i = 0; i < found.Count; i++) {
                Doc d = store.Peek(found[i]);
                if (!store.CanRead(a, d, now)) { continue; }
                w.Var(d.Id);
                w.Text(d.Value(NameSlot));
                w.U8((byte)LevelOf(d, LevelSlot));
            }
            Printers(place, w);
        }

        private static void Printers(PcPlace place, WireWriter w) {
            w.Var((ulong)place.Printers.Count);
            for (int i = 0; i < place.Printers.Count; i++) {
                w.U32(place.Printers[i].Id);
                w.Text(place.Printers[i].Name);
                w.U8((byte)place.Printers[i].State);
            }
        }

        private string PathOf(Doc d, string disk) {
            var parts = new List<string>(PcLimits.Depth);
            for (int depth = 0; d != null && depth <= PcLimits.Depth; depth++) {
                parts.Add(d.Value(NameSlot));
                ulong p = ulong.TryParse(d.Value(ParentSlot), NumberStyles.None, CultureInfo.InvariantCulture, out ulong pid) ? pid : 0;
                d = p == 0 ? null : Node(p, disk);
            }
            parts.Reverse();
            return "C:\\" + string.Join("\\", parts);
        }

        private int Depth(Doc d, string disk) {
            int depth = 0;
            while (d != null && depth <= PcLimits.Depth + 1) {
                depth++;
                ulong p = ulong.TryParse(d.Value(ParentSlot), NumberStyles.None, CultureInfo.InvariantCulture, out ulong pid) ? pid : 0;
                d = p == 0 ? null : Node(p, disk);
            }
            return depth;
        }

        private static ulong Parent(Doc d) => ulong.TryParse(d.Value(ParentSlot), NumberStyles.None, CultureInfo.InvariantCulture, out ulong p) ? p : 0;

        private PcEntry EntryOf(in Actor a, Doc d) => new PcEntry {
            Id = d.Id, Name = d.Value(NameSlot), Kind = d.Value(KindSlot) == "d" ? NodeKind.Folder : NodeKind.Text,
            Level = (byte)LevelOf(d, LevelSlot), Size = d.Value(BodySlot).Length, Updated = d.Updated, Rev = d.Rev, Mine = d.Owner == a.Id && a.Id != 0
        };

        public void Folder(in Actor a, PcPlace place, ulong folder, uint token, double now, WireWriter w) {
            Doc f = Node(folder, place.Disk);
            PcError e = f == null || f.Deleted ? PcError.NotFound : f.Value(KindSlot) != "d" ? PcError.Invalid : !store.CanRead(a, f, now) ? PcError.Denied : PcError.Ok;
            PcWire.Reply(w, PcReply.Folder, token, e);
            w.Var(folder);
            if (e != PcError.Ok) { return; }
            w.Var(Parent(f));
            w.Text(f.Value(NameSlot));
            w.Text(PathOf(f, place.Disk));
            w.U8((byte)LevelOf(f, LevelSlot));
            w.Bool(StoreWritable(a, f));
            Children(place.Disk, folder, found);
            int count = 0;
            for (int i = 0; i < found.Count && count < PcLimits.EntriesPerFolder; i++) {
                if (store.CanRead(a, store.Peek(found[i]), now)) { count++; }
            }
            w.Var((ulong)count);
            int written = 0;
            for (int i = 0; i < found.Count && written < count; i++) {
                Doc d = store.Peek(found[i]);
                if (!store.CanRead(a, d, now)) { continue; }
                PcWire.Entry(w, EntryOf(a, d));
                written++;
            }
        }

        private static bool StoreWritable(in Actor a, Doc d) => DocStore.CanWrite(a, d);

        public void File(in Actor a, PcPlace place, ulong id, uint token, double now, WireWriter w) {
            Doc d = Node(id, place.Disk);
            PcError e = d == null || d.Deleted ? PcError.NotFound : d.Value(KindSlot) != "f" ? PcError.Invalid : !store.CanRead(a, d, now) ? PcError.Denied : PcError.Ok;
            PcWire.Reply(w, PcReply.File, token, e);
            w.Var(id);
            if (e != PcError.Ok) { return; }
            WriteFile(a, d, w);
        }

        private static void WriteFile(in Actor a, Doc d, WireWriter w) {
            w.Var(Parent(d));
            w.Text(d.Value(NameSlot));
            w.Var(d.Rev);
            w.Bool(!StoreWritable(a, d));
            w.Text(d.Value(BodySlot));
        }

        public void Save(in Actor a, PcPlace place, ulong id, uint rev, string text, uint token, double now, WireWriter w) {
            Doc d = Node(id, place.Disk);
            PcError e = d == null || d.Deleted ? PcError.NotFound : d.Value(KindSlot) != "f" ? PcError.Invalid : text.Length > PcLimits.BodyMax ? PcError.TooLarge : PcError.Ok;
            if (e == PcError.Ok && (!store.CanRead(a, d, now) || !StoreWritable(a, d))) { e = PcError.Denied; }
            uint next = 0;
            if (e == PcError.Ok && rev == d.Rev && string.Equals(text, d.Value(BodySlot), StringComparison.Ordinal)) {
                next = d.Rev;
            } else if (e == PcError.Ok) {
                one[0] = BodySlot;
                oneValue[0] = text;
                e = Of(store.Edit(a, id, rev, one, oneValue, now, out next));
            }
            PcWire.Reply(w, PcReply.Saved, token, e);
            w.Var(id);
            w.Var(e == PcError.Ok ? next : d?.Rev ?? 0);
            if (e == PcError.Conflict) { WriteFile(a, d, w); }
        }

        private bool NameTaken(string disk, ulong folder, string name, ulong except) {
            Children(disk, folder, found2);
            for (int i = 0; i < found2.Count; i++) {
                if (found2[i] != except && string.Equals(store.Peek(found2[i]).Value(NameSlot), name, StringComparison.OrdinalIgnoreCase)) { return true; }
            }
            return false;
        }

        public PcError Create(in Actor a, PcPlace place, ulong folder, NodeKind kind, string name, double now, out ulong id) {
            id = 0;
            name = name?.Trim() ?? "";
            Doc f = Node(folder, place.Disk);
            if (f == null || f.Deleted || f.Value(KindSlot) != "d") { return PcError.NotFound; }
            if (!store.CanRead(a, f, now) || !StoreWritable(a, f)) { return PcError.Denied; }
            if (!PcLimits.ValidName(name)) { return PcError.Invalid; }
            if (NameTaken(place.Disk, folder, name, 0)) { return PcError.Exists; }
            if (Depth(f, place.Disk) >= PcLimits.Depth) { return PcError.Depth; }
            if (DiskNodes(place.Disk) >= PcLimits.NodesPerDisk) { return PcError.Full; }
            Children(place.Disk, folder, found2);
            if (found2.Count >= PcLimits.EntriesPerFolder) { return PcError.Full; }
            id = Make(a, place.Disk, folder, name, kind, LevelOf(f, LevelSlot), "", now);
            return id == 0 ? PcError.Denied : PcError.Ok;
        }

        public PcError Rename(in Actor a, PcPlace place, ulong id, string name, double now) {
            name = name?.Trim() ?? "";
            Doc d = Node(id, place.Disk);
            if (d == null || d.Deleted) { return PcError.NotFound; }
            if (Parent(d) == 0 && !a.Gm) { return PcError.Denied; }
            if (!PcLimits.ValidName(name)) { return PcError.Invalid; }
            if (NameTaken(place.Disk, Parent(d), name, id)) { return PcError.Exists; }
            one[0] = NameSlot;
            oneValue[0] = name;
            return Of(store.Edit(a, id, d.Rev, one, oneValue, now, out _));
        }

        public PcError Delete(in Actor a, PcPlace place, ulong id, double now) {
            Doc d = Node(id, place.Disk);
            if (d == null || d.Deleted) { return PcError.NotFound; }
            if (Parent(d) == 0) { return PcError.Denied; }
            if (d.Value(KindSlot) == "d") {
                Children(place.Disk, id, found2);
                if (found2.Count > 0) { return PcError.NotEmpty; }
            }
            return Of(store.Delete(a, id, now));
        }

        public void Bin(in Actor a, PcPlace place, uint token, WireWriter w) {
            store.Removed(NodeType, found);
            int count = 0;
            for (int i = 0; i < found.Count; i++) {
                Doc d = store.Peek(found[i]);
                if (d.Deleted && d.Value(DiskSlot) == place.Disk && (a.Gm || d.Owner == a.Id)) { count++; }
            }
            count = Math.Min(count, PcLimits.EntriesPerFolder);
            PcWire.Reply(w, PcReply.Bin, token, PcError.Ok);
            w.Var((ulong)count);
            int written = 0;
            for (int i = found.Count - 1; i >= 0 && written < count; i--) {
                Doc d = store.Peek(found[i]);
                if (!d.Deleted || d.Value(DiskSlot) != place.Disk || !(a.Gm || d.Owner == a.Id)) { continue; }
                PcWire.Entry(w, EntryOf(a, d));
                written++;
            }
        }

        public PcError Restore(in Actor a, PcPlace place, ulong id, double now) {
            Doc d = Node(id, place.Disk);
            if (d == null || !d.Deleted) { return PcError.NotFound; }
            Doc parent = Node(Parent(d), place.Disk);
            if (parent == null || parent.Deleted) { return PcError.NotFound; }
            if (NameTaken(place.Disk, Parent(d), d.Value(NameSlot), id)) { return PcError.Exists; }
            return Of(store.Restore(a, id, now));
        }

        public void Dossiers(in Actor a, uint token, double now, WireWriter w) {
            store.OfType(DossierType, found);
            int count = 0;
            for (int i = 0; i < found.Count; i++) {
                if (store.CanRead(a, store.Peek(found[i]), now)) { count++; }
            }
            count = Math.Min(count, PcLimits.DossierList);
            PcWire.Reply(w, PcReply.Dossiers, token, PcError.Ok);
            w.Bool(a.Gm || (a.Roles & PcRoles.Level(CreateDossierLevel)) != 0);
            w.Var((ulong)count);
            int written = 0;
            for (int i = 0; i < found.Count && written < count; i++) {
                Doc d = store.Peek(found[i]);
                if (!store.CanRead(a, d, now)) { continue; }
                w.Var(d.Id);
                w.Text(SubjectName(d.Value(SubjectSlot)) ?? "");
                w.Text(d.Value(StatusSlot));
                w.U8((byte)LevelOf(d, DLevelSlot));
                PcWire.Time(w, d.Updated);
                written++;
            }
            IReadOnlyList<(string Key, string Name)> subjects = Subjects();
            int n = Math.Min(subjects.Count, 64);
            w.Var((ulong)n);
            for (int i = 0; i < n; i++) {
                w.Text(subjects[i].Key);
                w.Text(subjects[i].Name);
            }
        }

        public void DossierView(in Actor a, ulong id, uint token, PcError head, double now, WireWriter w) {
            Doc d = store.Peek(id);
            PcError e = d == null || d.Type.Id != DossierType || d.Deleted ? PcError.NotFound : !store.CanRead(a, d, now) ? PcError.Denied : head;
            PcWire.Reply(w, PcReply.Dossier, token, e);
            w.Var(id);
            if (d == null || e == PcError.NotFound || e == PcError.Denied) { return; }
            bool closed = DocStore.CanSee(a, d, ClosedSlot);
            w.Var(d.Rev);
            w.Text(SubjectName(d.Value(SubjectSlot)) ?? "");
            w.Text(d.Value(StatusSlot));
            w.U8((byte)LevelOf(d, DLevelSlot));
            w.Text(d.Value(NotesSlot));
            w.Bool(closed);
            if (closed) { w.Text(d.Value(ClosedSlot)); }
            w.Bool(DocStore.CanWrite(a, d));
        }

        public PcError NewDossier(in Actor a, string subject, double now, out ulong id) {
            id = 0;
            bool known = false;
            IReadOnlyList<(string Key, string Name)> subjects = Subjects();
            for (int i = 0; i < subjects.Count; i++) {
                if (subjects[i].Key == subject) { known = true; }
            }
            if (!known) { return PcError.NotFound; }
            store.Find(System, DossierType, "subject", subject, now, found2);
            if (found2.Count > 0) {
                id = found2[0];
                return PcError.Exists;
            }
            ulong read = PcRoles.Level(1);
            ulong write = PcRoles.Level(CreateDossierLevel);
            return Of(store.Create(a, DossierType, new[] { subject, "", "1", "", "" }, read, write, now, out id));
        }

        public void SaveDossier(in Actor a, ulong id, uint rev, string status, int level, string notes, string closed, uint token, double now, WireWriter w) {
            Doc d = store.Peek(id);
            PcError e = d == null || d.Type.Id != DossierType || d.Deleted ? PcError.NotFound : PcError.Ok;
            if (e == PcError.Ok) {
                Span<int> slots = stackalloc int[4] { StatusSlot, DLevelSlot, NotesSlot, ClosedSlot };
                string[] values = { status ?? "", Math.Clamp(level, 0, PcLimits.Levels).ToString(CultureInfo.InvariantCulture), notes ?? "", closed };
                int n = closed != null && DocStore.CanSee(a, d, ClosedSlot) ? 4 : 3;
                int requestedLevel = Math.Clamp(level, 0, PcLimits.Levels);
                ulong requestedRole = PcRoles.Level(requestedLevel);
                if (!a.Gm && (a.Roles & requestedRole) == 0) {
                    e = PcError.Denied;
                } else {
                    e = Of(store.Edit(a, id, rev, slots.Slice(0, n), values.AsSpan(0, n), now, out _));
                    if (e == PcError.Ok) { d.ReadRoles = requestedRole; }
                }
            }
            DossierView(a, id, token, e, now, w);
        }

        public PcError Print(in PcActor who, PcPlace place, ulong id, uint rev, ushort printer, uint token, double now, out uint job) {
            job = 0;
            int pi = -1;
            for (int i = 0; i < place.Printers.Count; i++) {
                if (place.Printers[i].Id == printer) { pi = i; }
            }
            if (pi < 0) { return PcError.NoPrinter; }
            if (place.Printers[pi].State == PrinterState.Off || place.Printers[pi].State == PrinterState.Gone) { return PcError.PrinterOff; }
            Actor a = who.Doc;
            Doc d = Node(id, place.Disk);
            if (d == null || d.Deleted || d.Value(KindSlot) != "f") { return PcError.NotFound; }
            if (!store.CanRead(a, d, now)) { return PcError.Denied; }
            if (rev != d.Rev) { return PcError.Conflict; }
            PcError e = queue.Enqueue(who.Id, token, printer, place.Printers[pi].Epoch, id, d.Rev, d.Value(NameSlot), d.Value(BodySlot), now, out PrintJob j);
            if (j != null) { job = j.Id; }
            return e;
        }

        public void Jobs(in PcActor who, PcPlace place, uint token, double now, WireWriter w) {
            PcWire.Reply(w, PcReply.Jobs, token, PcError.Ok);
            Printers(place, w);
            int count = 0;
            IReadOnlyList<PrintJob> jobs = queue.Jobs;
            for (int i = 0; i < jobs.Count; i++) {
                if (Visible(jobs[i], place)) { count++; }
            }
            count = Math.Min(count, 32);
            w.Var((ulong)count);
            int written = 0;
            for (int i = 0; i < jobs.Count && written < count; i++) {
                PrintJob j = jobs[i];
                if (!Visible(j, place)) { continue; }
                PcWire.Job(w, new PcJob {
                    Id = j.Id, Title = j.Account == who.Id || who.Gm ? j.Title : "Документ", Printer = j.Printer, State = j.State, Error = j.Error,
                    Pages = (byte)Math.Min(255, j.Pages.Length), Printed = (byte)Math.Min(255, j.Printed), Progress = queue.Progress(j, now), Mine = j.Account == who.Id
                });
                written++;
            }
        }

        private static bool Visible(PrintJob j, PcPlace place) {
            for (int i = 0; i < place.Printers.Count; i++) {
                if (place.Printers[i].Id == j.Printer) { return true; }
            }
            return false;
        }
    }
}
