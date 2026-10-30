using System;
using System.Collections.Generic;
using ScpItdMod.Net;

namespace ScpItdMod.Pc {
    public sealed class PcFolder {
        public ulong Id;
        public ulong Parent;
        public string Name = "";
        public string Path = "";
        public byte Level;
        public bool Writable;
        public PcError Error;
        public bool Loading;
        public int Revision;
        public readonly List<PcEntry> Entries = new List<PcEntry>();
    }

    public sealed class PcFile {
        public ulong Id;
        public ulong Folder;
        public string Name = "";
        public uint Rev;
        public bool ReadOnly;
        public string Text = "";
        public PcError Error;
        public bool Loading;
        public int Revision;
        public bool Conflict;
        public uint TheirRev;
        public string TheirText;
        internal int References;
    }

    public struct PcResult {
        public uint Token;
        public PcOp Op;
        public PcError Error;
        public ulong Id;
        public uint Rev;
    }

    public sealed class PcSession {
        public const int MaxFolders = 64;
        public const int MaxFiles = 16;
        public const double RequestTimeout = 10.0;

        private readonly WireWriter w = new WireWriter(1024);
        private readonly Dictionary<uint, (PcOp Op, ulong Id, double Deadline)> pending = new Dictionary<uint, (PcOp, ulong, double)>();
        private readonly List<uint> expired = new List<uint>();
        private readonly Dictionary<ulong, PcFolder> folders = new Dictionary<ulong, PcFolder>();
        private readonly Dictionary<ulong, PcFile> files = new Dictionary<ulong, PcFile>();
        private readonly List<PcResult> results = new List<PcResult>();
        private readonly Dictionary<uint, string> sentText = new Dictionary<uint, string>();
        private uint token = (uint)Guid.NewGuid().GetHashCode();
        private uint latestDossier;
        private double now;

        public Action<ReadOnlyMemory<byte>> Send = _ => { };

        public ushort Computer { get; private set; }
        public DesktopIdentity Identity { get; private set; }
        public bool Ready { get; private set; }
        public PcError Fault { get; private set; }
        public string Machine { get; private set; } = "";
        public string User { get; private set; } = "";
        public int Clearance { get; private set; }
        public bool Card { get; private set; }
        public bool Gm { get; private set; }
        public string Disk { get; private set; } = "";
        public readonly List<PcRoot> Roots = new List<PcRoot>();
        public readonly List<PcPrinter> Printers = new List<PcPrinter>();
        public readonly List<PcJob> Jobs = new List<PcJob>();
        public readonly List<PcEntry> Bin = new List<PcEntry>();
        public readonly List<PcDossierRow> Dossiers = new List<PcDossierRow>();
        public readonly List<(string Key, string Name)> Subjects = new List<(string, string)>();
        public bool CanCreateDossier { get; private set; }
        public PcDossierView Dossier { get; private set; }
        public PcError DossierError { get; private set; }
        public int Revision { get; private set; }

        public IReadOnlyList<PcResult> Results => results;

        public int Pending => pending.Count;

        public void Begin(ushort computer, double time = 0) {
            End();
            Computer = computer;
            now = time;
            Hello();
        }

        public void End() {
            if (Computer != 0) {
                Start(PcOp.Leave);
                Send(w.Span.ToArray());
            }
            Computer = 0; Identity = default;
            Ready = false;
            Fault = PcError.Ok;
            Machine = User = Disk = "";
            Clearance = 0;
            Card = Gm = CanCreateDossier = false;
            pending.Clear();
            sentText.Clear();
            expired.Clear();
            folders.Clear();
            files.Clear();
            results.Clear();
            Roots.Clear();
            Printers.Clear();
            Jobs.Clear();
            Bin.Clear();
            Dossiers.Clear();
            Subjects.Clear();
            Dossier = null;
            DossierError = PcError.Ok;
            latestDossier = 0;
            Revision++;
        }

        public void Tick(double time) {
            if (Computer == 0 || !double.IsFinite(time)) { return; }
            now = Math.Max(now, time);
            expired.Clear();
            foreach (var pair in pending) {
                if (pair.Value.Deadline <= now) { expired.Add(pair.Key); }
            }
            for (int i = 0; i < expired.Count; i++) {
                uint id = expired[i];
                if (pending.Remove(id, out var request)) { Fail(id, request.Op, request.Id, PcError.Timeout, request.Id); }
            }
        }

        private uint Start(PcOp op, ulong id = 0) {
            token++;
            if (token == 0) { token = 1; }
            PcWire.Request(w, op, token, Computer);
            pending[token] = (op, id, now + RequestTimeout);
            return token;
        }

        private uint Flush() {
            Send(w.Span.ToArray());
            return token;
        }

        public uint Hello() {
            Start(PcOp.Hello);
            return Flush();
        }

        public uint Present(PcApp app, ushort camera = 0, double position = 0, byte speed = 2, bool live = true, bool playing = false) {
            Start(PcOp.Present);
            w.U8((byte)app);
            w.U16(camera); PcWire.Time(w, position); w.U8(speed); w.Bool(live); w.Bool(playing);
            return Flush();
        }

        public PcFolder Folder(ulong id, bool refresh = false) {
            if (!folders.TryGetValue(id, out PcFolder f)) {
                if (folders.Count >= MaxFolders) { folders.Clear(); }
                f = new PcFolder { Id = id };
                folders[id] = f;
                refresh = true;
            }
            if (refresh && !f.Loading) {
                f.Loading = true;
                Start(PcOp.List, id);
                w.Var(id);
                Flush();
            }
            return f;
        }

        public PcFile File(ulong id, bool refresh = false) {
            if (!files.TryGetValue(id, out PcFile f)) {
                if (files.Count >= MaxFiles) { DropOldFiles(); }
                f = new PcFile { Id = id };
                files[id] = f;
                refresh = true;
            }
            if (refresh && !f.Loading) {
                f.Loading = true;
                Start(PcOp.Open, id);
                w.Var(id);
                Flush();
            }
            return f;
        }

        public PcFile Retain(ulong id) {
            bool refresh = files.TryGetValue(id, out PcFile previous) && previous.References == 0;
            PcFile file = File(id, refresh);
            file.References++;
            return file;
        }

        public void Forget(ulong id) {
            if (files.TryGetValue(id, out PcFile file) && file.References > 0) { file.References--; }
        }

        private void DropOldFiles() {
            while (files.Count >= MaxFiles) {
                ulong candidate = 0;
                foreach (var pair in files) {
                    if (!pair.Value.Loading && pair.Value.References == 0) { candidate = pair.Key; break; }
                }
                if (candidate == 0) { break; }
                files.Remove(candidate);
            }
        }

        public uint Save(ulong file, uint baseRev, string text) {
            uint t = Start(PcOp.Save, file);
            sentText[t] = text ?? "";
            w.Var(file);
            w.Var(baseRev);
            w.Text(text ?? "");
            return Flush();
        }

        public uint Create(ulong folder, NodeKind kind, string name) {
            Start(PcOp.Create);
            w.Var(folder);
            w.U8((byte)kind);
            w.Text(name ?? "");
            return Flush();
        }

        public uint Rename(ulong id, string name) {
            Start(PcOp.Rename);
            w.Var(id);
            w.Text(name ?? "");
            return Flush();
        }

        public uint Delete(ulong id) {
            Start(PcOp.Delete);
            w.Var(id);
            return Flush();
        }

        public uint LoadBin() {
            Start(PcOp.Bin);
            return Flush();
        }

        public uint Restore(ulong id) {
            Start(PcOp.Restore);
            w.Var(id);
            return Flush();
        }

        public uint LoadDossiers() {
            Start(PcOp.Dossiers);
            return Flush();
        }

        public uint OpenDossier(ulong id) {
            latestDossier = Start(PcOp.Dossier, id);
            DossierError = PcError.Ok;
            w.Var(id);
            return Flush();
        }

        public uint SaveDossier(PcDossierView v, string status, int level, string notes, string closed) {
            return SaveDossier(v, v.Rev, status, level, notes, closed);
        }

        public uint SaveDossier(PcDossierView v, uint expectedRevision, string status, int level, string notes, string closed) {
            latestDossier = Start(PcOp.SaveDossier, v.Id);
            w.Var(v.Id);
            w.Var(expectedRevision);
            w.Text(status ?? "");
            w.U8((byte)Math.Clamp(level, 0, PcLimits.Levels));
            w.Text(notes ?? "");
            w.Bool(closed != null && v.CanClosed);
            if (closed != null && v.CanClosed) { w.Text(closed); }
            return Flush();
        }

        public uint NewDossier(string subject) {
            Start(PcOp.NewDossier);
            w.Text(subject ?? "");
            return Flush();
        }

        public uint Print(ulong file, uint rev, ushort printer) {
            Start(PcOp.Print);
            w.Var(file);
            w.Var(rev);
            w.U32(printer);
            return Flush();
        }

        public uint Cancel(uint job) {
            Start(PcOp.Cancel);
            w.U32(job);
            return Flush();
        }

        public uint LoadJobs() {
            Start(PcOp.Jobs);
            return Flush();
        }

        public bool Take(uint token, out PcResult result) {
            for (int i = 0; i < results.Count; i++) {
                if (results[i].Token == token) {
                    result = results[i];
                    results.RemoveAt(i);
                    return true;
                }
            }
            result = default;
            return false;
        }

        public bool Waiting(uint token) => pending.ContainsKey(token);

        private void Result(uint token, PcOp op, PcError e, ulong id, uint rev = 0) {
            if (op is PcOp.Hello or PcOp.List or PcOp.Open or PcOp.Bin or PcOp.Dossiers or PcOp.Dossier or PcOp.Jobs) { return; }
            results.Add(new PcResult { Token = token, Op = op, Error = e, Id = id, Rev = rev });
            while (results.Count > 32) { results.RemoveAt(0); }
        }

        public bool Apply(ReadOnlySpan<byte> payload) {
            var r = new WireReader(payload);
            if (r.U8() != PcWire.Proto) { return false; }
            var kind = (PcReply)r.U8();
            uint tok = r.U32();
            var e = (PcError)r.U8();
            if (r.Failed || e > PcError.NoServer || !pending.TryGetValue(tok, out var request)) { return false; }
            PcOp op = request.Op;
            if (kind != PcReplyReader.Expected(op) && !(kind == PcReply.Done && e != PcError.Ok)) { return false; }
            if (!PcReplyReader.Valid(r, kind, e, request.Id)) { return false; }
            pending.Remove(tok);
            Revision++;
            switch (kind) {
                case PcReply.Info: return Info(ref r);
                case PcReply.Folder: return FolderReply(ref r, e);
                case PcReply.File: return FileReply(ref r, e);
                case PcReply.Saved: return Saved(ref r, tok, e);
                case PcReply.Done: {
                    ulong id = r.Var();
                    if (e != PcError.Ok) {
                        Fail(tok, op, request.Id, e, id);
                        return true;
                    }
                    Result(tok, op, e, id);
                    if (e == PcError.Ok) { Invalidate(); }
                    return !r.Failed;
                }
                case PcReply.Bin: {
                    int n = r.Count(PcLimits.EntriesPerFolder);
                    Bin.Clear();
                    for (int i = 0; i < n && !r.Failed; i++) { Bin.Add(PcWire.Entry(ref r)); }
                    return !r.Failed;
                }
                case PcReply.Dossiers: {
                    CanCreateDossier = r.Bool();
                    int n = r.Count(PcLimits.DossierList);
                    Dossiers.Clear();
                    for (int i = 0; i < n && !r.Failed; i++) {
                        Dossiers.Add(new PcDossierRow { Id = r.Var(), Subject = r.Text(256), Status = r.Text(PcLimits.StatusMax * 4), Level = r.U8(), Updated = PcWire.Time(ref r) });
                    }
                    int s = r.Count(64);
                    Subjects.Clear();
                    for (int i = 0; i < s && !r.Failed; i++) { Subjects.Add((r.Text(128), r.Text(256))); }
                    return !r.Failed;
                }
                case PcReply.Dossier: {
                    ulong id = r.Var();
                    if (tok != latestDossier) {
                        Result(tok, op, e, id);
                        return true;
                    }
                    DossierError = e;
                    if (e == PcError.NotFound || e == PcError.Denied) {
                        Dossier = null;
                        Result(tok, op, e, id);
                        return !r.Failed;
                    }
                    var v = new PcDossierView { Id = id, Rev = (uint)r.Var(), Subject = r.Text(256), Status = r.Text(PcLimits.StatusMax * 4), Level = r.U8(), Notes = r.Text(PcLimits.NotesMax * 4) };
                    v.CanClosed = r.Bool();
                    v.Closed = v.CanClosed ? r.Text(PcLimits.NotesMax * 4) : null;
                    v.CanWrite = r.Bool();
                    if (r.Failed) { return false; }
                    Dossier = v;
                    Result(tok, op, e, id);
                    return true;
                }
                case PcReply.Jobs: {
                    if (!ReadPrinters(ref r)) { return false; }
                    int n = r.Count(32);
                    Jobs.Clear();
                    for (int i = 0; i < n && !r.Failed; i++) { Jobs.Add(PcWire.Job(ref r)); }
                    return !r.Failed;
                }
            }
            return false;
        }

        private void Fail(uint token, PcOp op, ulong id, PcError error, ulong resultId) {
            sentText.Remove(token);
            if (op == PcOp.Hello || error == PcError.NoPower || error == PcError.NoServer || error == PcError.Far) {
                Fault = error;
                Ready = false;
            }
            if (op == PcOp.List && folders.TryGetValue(id, out PcFolder folder)) {
                folder.Loading = false;
                folder.Error = error;
                folder.Revision++;
            } else if (op == PcOp.Open && files.TryGetValue(id, out PcFile file)) {
                file.Loading = false;
                file.Error = error;
                file.Revision++;
            } else if ((op == PcOp.Dossier || op == PcOp.SaveDossier) && token == latestDossier) {
                DossierError = error;
            }
            Result(token, op, error, resultId);
            Revision++;
        }

        private void Invalidate() {
            foreach (PcFolder f in folders.Values) { f.Revision = -1; }
        }

        public bool Stale(PcFolder f) => f.Revision < 0;

        private bool Info(ref WireReader r) {
            if (!PcWire.ReadDesktop(ref r, Computer, out DesktopIdentity identity)) { return false; }
            Identity = identity;
            Machine = r.Text(256);
            User = r.Text(256);
            Clearance = r.U8();
            Card = r.Bool();
            Gm = r.Bool();
            Disk = r.Text(256);
            int n = r.Count(16);
            Roots.Clear();
            for (int i = 0; i < n && !r.Failed; i++) { Roots.Add(new PcRoot { Id = r.Var(), Name = r.Text(PcLimits.NameMax * 4), Level = r.U8() }); }
            if (!ReadPrinters(ref r)) { return false; }
            Ready = true;
            Fault = PcError.Ok;
            return true;
        }

        private bool ReadPrinters(ref WireReader r) {
            int n = r.Count(16);
            Printers.Clear();
            for (int i = 0; i < n && !r.Failed; i++) {
                uint id = r.U32();
                Printers.Add(new PcPrinter { Id = id > ushort.MaxValue ? (ushort)0 : (ushort)id, Name = r.Text(256), State = (PrinterState)Math.Min(r.U8(), (byte)PrinterState.Gone) });
            }
            return !r.Failed;
        }

        private bool FolderReply(ref WireReader r, PcError e) {
            ulong id = r.Var();
            if (r.Failed) { return false; }
            if (!folders.TryGetValue(id, out PcFolder f)) {
                f = new PcFolder { Id = id };
                folders[id] = f;
            }
            f.Loading = false;
            f.Error = e;
            f.Revision++;
            if (f.Revision < 1) { f.Revision = 1; }
            f.Entries.Clear();
            if (e != PcError.Ok) { return true; }
            f.Parent = r.Var();
            f.Name = r.Text(PcLimits.NameMax * 4);
            f.Path = r.Text(1024);
            f.Level = r.U8();
            f.Writable = r.Bool();
            int n = r.Count(PcLimits.EntriesPerFolder);
            for (int i = 0; i < n && !r.Failed; i++) { f.Entries.Add(PcWire.Entry(ref r)); }
            f.Entries.Sort((a, b) => a.Kind != b.Kind ? a.Kind.CompareTo(b.Kind) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return !r.Failed;
        }

        private void ReadFile(ref WireReader r, PcFile f) {
            f.Folder = r.Var();
            f.Name = r.Text(PcLimits.NameMax * 4);
            f.Rev = (uint)r.Var();
            f.ReadOnly = r.Bool();
            f.Text = r.Text(PcLimits.BodyMax * 4);
        }

        private bool FileReply(ref WireReader r, PcError e) {
            ulong id = r.Var();
            if (r.Failed) { return false; }
            if (!files.TryGetValue(id, out PcFile f)) {
                f = new PcFile { Id = id };
                files[id] = f;
            }
            f.Loading = false;
            f.Error = e;
            if (e == PcError.Ok) { ReadFile(ref r, f); }
            f.Revision++;
            return !r.Failed;
        }

        private bool Saved(ref WireReader r, uint tok, PcError e) {
            ulong id = r.Var();
            uint rev = (uint)r.Var();
            if (r.Failed) { return false; }
            files.TryGetValue(id, out PcFile f);
            sentText.Remove(tok, out string sent);
            if (e == PcError.Ok && f != null) {
                f.Rev = rev;
                f.Conflict = false;
                if (sent != null) { f.Text = sent; }
                f.Revision++;
            } else if (e == PcError.Conflict) {
                var theirs = new PcFile();
                ReadFile(ref r, theirs);
                if (f != null) {
                    f.Conflict = true;
                    f.TheirRev = theirs.Rev;
                    f.TheirText = theirs.Text;
                }
            }
            Result(tok, PcOp.Save, e, id, rev);
            if (e == PcError.Ok) { Invalidate(); }
            return !r.Failed;
        }
    }
}
