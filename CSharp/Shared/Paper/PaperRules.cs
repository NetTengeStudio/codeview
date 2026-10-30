using System;
using ScpItdMod.Docs;
using ScpItdMod.Net;

namespace ScpItdMod.Paper {
    public enum PaperOp : byte { Open, Save, Stamp, Show, Faces, View, Saved, Face, Note }

    public enum PaperError : byte { Ok, Far, Hands, NoStamp, Full, Repeat, Outside, Conflict, Long, Denied, Target }

    public enum PaperMode : byte { Own, Shown }

    public struct FaceAsk {
        public ushort Item;
        public uint Rev;
    }

    public struct PaperView {
        public uint Request;
        public ushort Item;
        public PaperError Result;
        public PaperMode Mode;
        public uint Rev;
        public ushort Presenter;
        public string Body;
        public string Marks;
    }

    public struct PaperFace {
        public ushort Item;
        public uint Rev;
        public bool Thump;
        public string Body;
        public string Marks;
    }

    public static class PaperRules {
        public const string TypeId = "note";
        public const ushort Version = 2;
        public const ulong HolderBit = 1UL << 62;
        public const ulong StampBit = 1UL << 61;
        public const byte Proto = 3;
        public const uint Unknown = uint.MaxValue;
        public const int MaxAsks = 16;
        public const int MaxRequest = 8 + Sheet.BodyMax * 3 + 16;
        public const float ShowSeconds = 120.0f;
        public const float ShowRange = 220.0f;
        public const float FaceRange = 2400.0f;

        public static DocType Type() => new DocType(TypeId, Version, new[] {
            new Field("body", FieldKind.Text, Sheet.BodyMax, multiline: true),
            new Field("marks", FieldKind.Text, Marks.TextMax, writeRoles: StampBit)
        }, createRoles: HolderBit, ownerEdits: false, migrate: Migrate);

        public const int BodySlot = 0;
        public const int MarksSlot = 1;

        private static string[] Migrate(ushort from, string[] values) {
            if (from != 1 || values == null || values.Length != 3) { return null; }
            string title = values[0] ?? "";
            string body = values[1] ?? "";
            string joined = title.Length == 0 ? body : body.Length == 0 ? title : title + "\n" + body;
            if (joined.Length > Sheet.BodyMax) { joined = joined.Substring(0, Sheet.BodyMax); }
            string marks = values[2] == "ok" || values[2] == "deny"
                ? Marks.Format(new[] { Mark.At(values[2], Sheet.Width * 0.7f, Sheet.Height * 0.85f, -0.08f) })
                : "";
            return new[] { joined, marks };
        }

        public static bool ValidBody(string body) =>
            body != null && body.Length <= Sheet.BodyMax && Sheet.LinesOf(body) <= Sheet.Lines;

        public static void Head(WireWriter w, PaperOp op, ushort item) {
            w.Clear();
            w.U8(Proto);
            w.U8((byte)op);
            w.Var(item);
        }

        public static bool Head(ref WireReader r, out PaperOp op, out ushort item) {
            op = default;
            item = 0;
            if (r.U8() != Proto) { return false; }
            op = (PaperOp)r.U8();
            ulong raw = r.Var();
            if (r.Failed || raw > ushort.MaxValue || op > PaperOp.Note) { return false; }
            item = (ushort)raw;
            return true;
        }

        public static void Save(WireWriter w, ushort item, uint rev, bool final, string body, uint request = 0) {
            Head(w, PaperOp.Save, item);
            w.Var(rev);
            w.Bool(final);
            w.Text(body ?? ""); w.U32(request);
        }

        public static bool ReadSave(ref WireReader r, out uint rev, out bool final, out string body) => ReadSave(ref r, out rev, out final, out body, out _);

        public static bool ReadSave(ref WireReader r, out uint rev, out bool final, out string body, out uint request) {
            rev = (uint)r.Count(int.MaxValue);
            final = r.Bool();
            body = r.Text(Sheet.BodyMax * 3); request = r.U32();
            return !r.Failed && r.End;
        }

        public static void Stamp(WireWriter w, ushort item, in Mark mark) {
            Head(w, PaperOp.Stamp, item);
            w.Var(mark.X);
            w.Var(mark.Y);
            w.Var(Zig(mark.Turn));
        }

        public static bool ReadStamp(ref WireReader r, out ushort x, out ushort y, out short turn) {
            ulong rx = r.Var();
            ulong ry = r.Var();
            long rt = Unzig(r.Var());
            x = (ushort)Math.Min(rx, ushort.MaxValue);
            y = (ushort)Math.Min(ry, ushort.MaxValue);
            turn = (short)Math.Clamp(rt, short.MinValue, short.MaxValue);
            return !r.Failed && r.End && rx <= ushort.MaxValue && ry <= ushort.MaxValue;
        }

        public static void Show(WireWriter w, ushort item, ushort target) {
            Head(w, PaperOp.Show, item);
            w.Var(target);
        }

        public static bool ReadShow(ref WireReader r, out ushort target) {
            ulong raw = r.Var();
            target = (ushort)raw;
            return !r.Failed && r.End && raw <= ushort.MaxValue;
        }

        public static void Faces(WireWriter w, ReadOnlySpan<FaceAsk> asks) {
            Head(w, PaperOp.Faces, 0);
            int n = Math.Min(asks.Length, MaxAsks);
            w.Var((ulong)n);
            for (int i = 0; i < n; i++) {
                w.Var(asks[i].Item);
                w.Var(asks[i].Rev == Unknown ? 0UL : asks[i].Rev + 1UL);
            }
        }

        public static int ReadFaces(ref WireReader r, Span<FaceAsk> into) {
            int n = r.Count(MaxAsks);
            for (int i = 0; i < n && !r.Failed; i++) {
                ulong item = r.Var();
                ulong rev = r.Var();
                if (item > ushort.MaxValue || rev > uint.MaxValue) { return -1; }
                into[i] = new FaceAsk { Item = (ushort)item, Rev = rev == 0 ? Unknown : (uint)(rev - 1) };
            }
            return r.Failed || !r.End ? -1 : n;
        }

        public static void View(WireWriter w, in PaperView view) {
            Head(w, PaperOp.View, view.Item);
            w.U8((byte)view.Result);
            w.U8((byte)view.Mode);
            w.Var(view.Rev);
            w.Var(view.Presenter);
            w.Text(view.Body ?? "");
            w.Text(view.Marks ?? ""); w.U32(view.Request);
        }

        public static bool ReadView(ref WireReader r, ushort item, out PaperView view) {
            view = new PaperView { Item = item, Result = (PaperError)r.U8(), Mode = (PaperMode)r.U8() };
            view.Rev = (uint)r.Count(int.MaxValue);
            ulong presenter = r.Var();
            view.Presenter = (ushort)presenter;
            view.Body = r.Text(Sheet.BodyMax * 3);
            view.Marks = r.Text(Marks.TextMax * 3); view.Request = r.U32();
            return !r.Failed && r.End && presenter <= ushort.MaxValue && view.Mode <= PaperMode.Shown;
        }

        public static void Saved(WireWriter w, ushort item, PaperError result, uint rev, uint request = 0) {
            Head(w, PaperOp.Saved, item);
            w.U8((byte)result);
            w.Var(rev); w.U32(request);
        }

        public static bool ReadSaved(ref WireReader r, out PaperError result, out uint rev) => ReadSaved(ref r, out result, out rev, out _);

        public static bool ReadSaved(ref WireReader r, out PaperError result, out uint rev, out uint request) {
            result = (PaperError)r.U8();
            rev = (uint)r.Count(int.MaxValue); request = r.U32();
            return !r.Failed && r.End;
        }

        public static void Face(WireWriter w, in PaperFace face) {
            Head(w, PaperOp.Face, face.Item);
            w.Var(face.Rev);
            w.Bool(face.Thump);
            w.Text(face.Body ?? "");
            w.Text(face.Marks ?? "");
        }

        public static bool ReadFace(ref WireReader r, ushort item, out PaperFace face) {
            face = new PaperFace { Item = item, Rev = (uint)r.Count(int.MaxValue), Thump = r.Bool() };
            face.Body = r.Text(Sheet.BodyMax * 3);
            face.Marks = r.Text(Marks.TextMax * 3);
            return !r.Failed && r.End;
        }

        public static void Note(WireWriter w, ushort item, PaperError result) {
            Head(w, PaperOp.Note, item);
            w.U8((byte)result);
        }

        public static bool ReadNote(ref WireReader r, out PaperError result) {
            result = (PaperError)r.U8();
            return !r.Failed && r.End;
        }

        public static PaperError Of(MarkError error) => error switch {
            MarkError.Ok => PaperError.Ok,
            MarkError.Full => PaperError.Full,
            MarkError.Repeat => PaperError.Repeat,
            _ => PaperError.Outside
        };

        public static PaperError Of(DocError error) => error switch {
            DocError.Ok => PaperError.Ok,
            DocError.Conflict => PaperError.Conflict,
            DocError.TooLarge => PaperError.Long,
            DocError.Invalid => PaperError.Long,
            _ => PaperError.Denied
        };

        public static string Explain(PaperError error) => error switch {
            PaperError.Far => "Лист далеко.",
            PaperError.Hands => "Чтобы писать, держите лист в руке, а вторую руку освободите.",
            PaperError.NoStamp => "В руке нет печати.",
            PaperError.Full => "На листе больше нет места для печатей.",
            PaperError.Repeat => "Эта печать уже стоит на листе.",
            PaperError.Outside => "Печать должна попасть на лист.",
            PaperError.Conflict => "Лист изменили, пока вы писали. Показан свежий текст.",
            PaperError.Long => "На листе кончилось место.",
            PaperError.Target => "Рядом нет того, кому можно показать лист.",
            PaperError.Denied => "Сервер не принял действие.",
            _ => ""
        };

        private static ulong Zig(long value) => (ulong)((value << 1) ^ (value >> 63));

        private static long Unzig(ulong value) => (long)(value >> 1) ^ -(long)(value & 1);
    }
}
