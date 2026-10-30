using ScpItdMod.Net;

namespace ScpItdMod.Pc {
    public static class PcReplyReader {
        public static PcReply Expected(PcOp op) => op switch {
            PcOp.Hello => PcReply.Info,
            PcOp.List => PcReply.Folder,
            PcOp.Open => PcReply.File,
            PcOp.Save => PcReply.Saved,
            PcOp.Bin => PcReply.Bin,
            PcOp.Dossiers => PcReply.Dossiers,
            PcOp.Dossier or PcOp.SaveDossier => PcReply.Dossier,
            PcOp.Jobs => PcReply.Jobs,
            _ => PcReply.Done
        };

        public static bool Valid(WireReader r, PcReply kind, PcError error, ulong expectedId) {
            switch (kind) {
                case PcReply.Info:
                    if (error != PcError.Ok) { return false; }
                    r.U64(); r.U64(); r.U64(); r.U32(); r.U32();
                    r.SkipText(256); r.SkipText(256); r.U8(); r.Bool(); r.Bool(); r.SkipText(256);
                    int roots = r.Count(16);
                    for (int i = 0; i < roots && !r.Failed; i++) { r.Var(); r.SkipText(PcLimits.NameMax * 4); r.U8(); }
                    Printers(ref r);
                    break;
                case PcReply.Folder:
                    if (r.Var() != expectedId) { return false; }
                    if (error != PcError.Ok) { break; }
                    r.Var(); r.SkipText(PcLimits.NameMax * 4); r.SkipText(1024); r.U8(); r.Bool();
                    Entries(ref r);
                    break;
                case PcReply.File:
                    if (r.Var() != expectedId) { return false; }
                    if (error == PcError.Ok) { File(ref r); }
                    break;
                case PcReply.Saved:
                    if (r.Var() != expectedId) { return false; }
                    r.Var();
                    if (error == PcError.Conflict) { File(ref r); }
                    break;
                case PcReply.Done:
                    r.Var();
                    break;
                case PcReply.Bin:
                    if (error != PcError.Ok) { return false; }
                    Entries(ref r);
                    break;
                case PcReply.Dossiers:
                    if (error != PcError.Ok) { return false; }
                    r.Bool();
                    int rows = r.Count(PcLimits.DossierList);
                    for (int i = 0; i < rows && !r.Failed; i++) {
                        r.Var(); r.SkipText(256); r.SkipText(PcLimits.StatusMax * 4); r.U8(); r.U64();
                    }
                    int people = r.Count(64);
                    for (int i = 0; i < people && !r.Failed; i++) { r.SkipText(128); r.SkipText(256); }
                    break;
                case PcReply.Dossier:
                    if (r.Var() != expectedId) { return false; }
                    if (error == PcError.NotFound || error == PcError.Denied) { break; }
                    r.Var(); r.SkipText(256); r.SkipText(PcLimits.StatusMax * 4); r.U8(); r.SkipText(PcLimits.NotesMax * 4);
                    if (r.Bool()) { r.SkipText(PcLimits.NotesMax * 4); }
                    r.Bool();
                    break;
                case PcReply.Jobs:
                    if (error != PcError.Ok) { return false; }
                    Printers(ref r);
                    int jobs = r.Count(32);
                    for (int i = 0; i < jobs && !r.Failed; i++) {
                        r.U32(); r.SkipText(PcLimits.NameMax * 4); r.U32(); r.U8(); r.U8(); r.U8(); r.U8(); r.U8(); r.Bool();
                    }
                    break;
                default: return false;
            }
            return !r.Failed && r.End;
        }

        private static void File(ref WireReader r) {
            r.Var(); r.SkipText(PcLimits.NameMax * 4); r.Var(); r.Bool(); r.SkipText(PcLimits.BodyMax * 4);
        }

        private static void Printers(ref WireReader r) {
            int count = r.Count(16);
            for (int i = 0; i < count && !r.Failed; i++) { r.U32(); r.SkipText(256); r.U8(); }
        }

        private static void Entries(ref WireReader r) {
            int count = r.Count(PcLimits.EntriesPerFolder);
            for (int i = 0; i < count && !r.Failed; i++) {
                r.Var(); r.SkipText(PcLimits.NameMax * 4); r.U8(); r.U8(); r.Count(PcLimits.BodyMax * 4); r.U64(); r.Var(); r.Bool();
            }
        }
    }
}
