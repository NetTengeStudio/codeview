using System;
using System.Collections.Generic;
using ScpItdMod.Net;

namespace ScpItdMod.Pc {
    public enum PcOp : byte { Hello, List, Open, Save, Create, Rename, Delete, Bin, Restore, Dossiers, Dossier, SaveDossier, NewDossier, Print, Cancel, Jobs, Present, Leave }

    public enum PcReply : byte { Info, Folder, File, Saved, Done, Bin, Dossiers, Dossier, Jobs }

    public enum PcError : byte { Ok, Denied, NotFound, Exists, Invalid, TooLarge, Full, Conflict, NotEmpty, NoPower, Far, Busy, NoPrinter, PrinterOff, NoPaper, Depth, Unknown, Timeout, NoServer }

    public enum NodeKind : byte { Folder, Text }

    public readonly struct DesktopIdentity : IEquatable<DesktopIdentity> {
        public readonly Guid Server;
        public readonly ulong Owner;
        public readonly uint Station, Profile;
        public readonly ushort Terminal;
        public DesktopIdentity(Guid server, ulong owner, uint station, uint profile, ushort terminal) {
            Server = server; Owner = owner; Station = station; Profile = profile; Terminal = terminal;
        }
        public bool Valid => Server != Guid.Empty && Owner != 0 && Station != 0 && Terminal != 0;
        public bool Equals(DesktopIdentity other) => Server == other.Server && Owner == other.Owner && Station == other.Station && Profile == other.Profile && Terminal == other.Terminal;
        public override bool Equals(object other) => other is DesktopIdentity identity && Equals(identity);
        public override int GetHashCode() => HashCode.Combine(Server, Owner, Station, Profile, Terminal);
    }

    public static class PcLimits {
        public const int NameMax = 40;
        public const int BodyMax = 4096;
        public const int NodesPerDisk = 512;
        public const int EntriesPerFolder = 128;
        public const int Depth = 8;
        public const int PageChars = 1200;
        public const int PageLines = 24;
        public const int StatusMax = 64;
        public const int NotesMax = 2000;
        public const int DossierList = 128;
        public const int MaxRequest = BodyMax * 3 + 256;
        public const int MaxReply = 64 * 1024;
        public const int Levels = 5;

        public static bool ValidName(string name) {
            if (string.IsNullOrWhiteSpace(name) || name.Length > NameMax || name != name.Trim() || name == "." || name == "..") { return false; }
            for (int i = 0; i < name.Length; i++) {
                char c = name[i];
                if (char.IsControl(c) || c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|') { return false; }
                if (char.IsSurrogate(c)) { return false; }
            }
            return true;
        }
    }

    public static class PcRoles {
        public const ulong Base = 1UL << 58;

        public static ulong Of(int clearance) {
            ulong roles = Base;
            for (int i = 1; i <= Math.Min(clearance, PcLimits.Levels); i++) { roles |= Level(i); }
            return roles;
        }

        public static ulong Level(int level) => level <= 0 ? Base : 1UL << (Math.Min(level, PcLimits.Levels) - 1);
    }

    public struct PcEntry {
        public ulong Id;
        public string Name;
        public NodeKind Kind;
        public byte Level;
        public int Size;
        public double Updated;
        public uint Rev;
        public bool Mine;
    }

    public struct PcRoot {
        public ulong Id;
        public string Name;
        public byte Level;
    }

    public struct PcPrinter {
        public ushort Id;
        internal uint Epoch;
        public string Name;
        public PrinterState State;
    }

    public struct PcJob {
        public uint Id;
        public string Title;
        public ushort Printer;
        public JobState State;
        public PcError Error;
        public byte Pages;
        public byte Printed;
        public float Progress;
        public bool Mine;
    }

    public struct PcDossierRow {
        public ulong Id;
        public string Subject;
        public string Status;
        public byte Level;
        public double Updated;
    }

    public sealed class PcDossierView {
        public ulong Id;
        public uint Rev;
        public string Subject = "";
        public string Status = "";
        public byte Level;
        public string Notes = "";
        public string Closed;
        public bool CanWrite;
        public bool CanClosed;
    }

    public static class PcWire {
        public const string Channel = "scpitd.pc";
        public const string TerminalTag = "scpitd_terminal";
        public const string PrinterTag = "scpitd_printer";
        public const byte Proto = 2;

        public static void WriteDesktop(WireWriter w, in DesktopIdentity identity) {
            Span<byte> server = stackalloc byte[16]; identity.Server.TryWriteBytes(server);
            w.Raw(server); w.U64(identity.Owner); w.U32(identity.Station); w.U32(identity.Profile);
        }
        public static bool ReadDesktop(ref WireReader r, ushort terminal, out DesktopIdentity identity) {
            Span<byte> server = stackalloc byte[16];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(server, r.U64());
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(server.Slice(8), r.U64());
            identity = new DesktopIdentity(new Guid(server), r.U64(), r.U32(), r.U32(), terminal);
            return !r.Failed && identity.Valid;
        }

        public static void Request(WireWriter w, PcOp op, uint token, ushort computer) {
            w.Clear();
            w.U8(Proto);
            w.U8((byte)op);
            w.U32(token);
            w.U32(computer);
        }

        public static bool ReadRequest(ref WireReader r, out PcOp op, out uint token, out ushort computer) {
            op = default;
            token = 0;
            computer = 0;
            if (r.U8() != Proto) { return false; }
            op = (PcOp)r.U8();
            token = r.U32();
            uint c = r.U32();
            if (r.Failed || c == 0 || c > ushort.MaxValue || token == 0 || op > PcOp.Leave) { return false; }
            computer = (ushort)c;
            return true;
        }

        public static void Reply(WireWriter w, PcReply kind, uint token, PcError error) {
            w.Clear();
            w.U8(Proto);
            w.U8((byte)kind);
            w.U32(token);
            w.U8((byte)error);
        }

        public static void Time(WireWriter w, double t) => w.U64((ulong)BitConverter.DoubleToInt64Bits(t));

        public static double Time(ref WireReader r) {
            double t = BitConverter.Int64BitsToDouble((long)r.U64());
            return double.IsFinite(t) ? t : 0;
        }

        public static void Entry(WireWriter w, in PcEntry e) {
            w.Var(e.Id);
            w.Text(e.Name);
            w.U8((byte)e.Kind);
            w.U8(e.Level);
            w.Var((ulong)Math.Max(0, e.Size));
            Time(w, e.Updated);
            w.Var(e.Rev);
            w.Bool(e.Mine);
        }

        public static PcEntry Entry(ref WireReader r) => new PcEntry {
            Id = r.Var(), Name = r.Text(PcLimits.NameMax * 4), Kind = (NodeKind)Math.Min(r.U8(), (byte)1), Level = r.U8(),
            Size = r.Count(PcLimits.BodyMax * 4), Updated = Time(ref r), Rev = (uint)r.Var(), Mine = r.Bool()
        };

        public static void Job(WireWriter w, in PcJob j) {
            w.U32(j.Id);
            w.Text(j.Title);
            w.U32(j.Printer);
            w.U8((byte)j.State);
            w.U8((byte)j.Error);
            w.U8(j.Pages);
            w.U8(j.Printed);
            w.U8((byte)Math.Clamp((int)(j.Progress * 255), 0, 255));
            w.Bool(j.Mine);
        }

        public static PcJob Job(ref WireReader r) {
            var j = new PcJob { Id = r.U32(), Title = r.Text(PcLimits.NameMax * 4) };
            uint printer = r.U32();
            j.Printer = printer > ushort.MaxValue ? (ushort)0 : (ushort)printer;
            j.State = (JobState)Math.Min(r.U8(), (byte)JobState.Cancelled);
            j.Error = (PcError)r.U8();
            j.Pages = r.U8();
            j.Printed = r.U8();
            j.Progress = r.U8() / 255.0f;
            j.Mine = r.Bool();
            return j;
        }

        public static string Explain(PcError e) => e switch {
            PcError.Timeout => "Станция не ответила. Повторите действие.",
            PcError.Ok => "Готово.",
            PcError.Denied => "Отказано в доступе. Недостаточно полномочий для этой операции.",
            PcError.NotFound => "Объект не найден. Возможно, он был удалён или перемещён.",
            PcError.Exists => "Объект с таким именем уже существует в этой папке.",
            PcError.Invalid => "Недопустимое имя или параметр.",
            PcError.TooLarge => "Слишком большой объём данных.",
            PcError.Full => "Достигнут предел: диск или очередь заполнены.",
            PcError.Conflict => "Файл был изменён другим пользователем.",
            PcError.NotEmpty => "Папка не пуста.",
            PcError.NoPower => "Нет питания.",
            PcError.NoServer => "Сервер хранения недоступен: нет питания стойки или связи с ней.",
            PcError.Far => "Вы отошли от компьютера.",
            PcError.Busy => "Компьютер занят, повторите позже.",
            PcError.NoPrinter => "Принтер недоступен.",
            PcError.PrinterOff => "Принтер выключен или не подключён к сети.",
            PcError.NoPaper => "В принтере нет бумаги.",
            PcError.Depth => "Слишком глубокая вложенность папок.",
            _ => "Неизвестная ошибка."
        };
    }
}
