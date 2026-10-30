using System;
using ScpItdMod.Net;

namespace ScpItdMod.Pc {
    public readonly struct PrinterView {
        public readonly PrinterState State;
        public readonly byte Printed, Pages, Progress;
        public PrinterView(PrinterState state, int printed, int pages, float progress) {
            State = state; Printed = (byte)printed; Pages = (byte)pages; Progress = (byte)Math.Clamp((int)MathF.Round(progress * 255), 0, 255);
        }
        public uint Key => ((uint)State << 24) | ((uint)Printed << 16) | ((uint)Pages << 8) | Progress;
        public void Write(WireWriter writer) { writer.Clear(); writer.U8(1); writer.U8((byte)State); writer.U8(Printed); writer.U8(Pages); writer.U8(Progress); }
        public static bool Read(ReadOnlySpan<byte> data, out PrinterView view) {
            view = default; var reader = new WireReader(data);
            if (reader.U8() != 1) { return false; }
            PrinterState state = (PrinterState)reader.U8(); byte printed = reader.U8(), pages = reader.U8(), progress = reader.U8();
            if (reader.Failed || !reader.End || state > PrinterState.Gone || pages > PrintQueue.MaxPages || printed > pages) { return false; }
            view = new PrinterView(state, printed, pages, progress / 255f); return true;
        }
        public string Text => State switch { PrinterState.Off => "Нет питания", PrinterState.NoPaper => "Нет чистых листов", PrinterState.Busy => "Печать", PrinterState.Gone => "Принтер недоступен", _ => "Готов к печати" };
    }
}
