using System;
using ScpItdMod.Net;

namespace ScpItdMod.Rec {
    public static class ScreenFrameParts {
        public const int HeaderBytes = 26;
        public const int PartBytes = CamWire.MaxChunk - HeaderBytes;
        public static int Count(int length) => (length + PartBytes - 1) / PartBytes;
        public static int Write(WireWriter writer, in ScreenMeta meta, ReadOnlySpan<byte> frame, int offset) {
            if (frame.Length == 0 || frame.Length > ScreenFrameWire.MaxBytes || offset < 0 ||
                offset >= frame.Length || offset % PartBytes != 0) { throw new ArgumentOutOfRangeException(nameof(offset)); }
            CamWire.Header(writer, CamMsg.ScreenPart);
            writer.U16(meta.Terminal); writer.U16(meta.Camera); writer.U32(meta.Session);
            writer.U32(meta.Epoch); writer.U32(meta.Sequence); writer.U32((uint)frame.Length); writer.U32((uint)offset);
            int length = Math.Min(PartBytes, frame.Length - offset);
            writer.Raw(frame.Slice(offset, length));
            return length;
        }
    }
}
