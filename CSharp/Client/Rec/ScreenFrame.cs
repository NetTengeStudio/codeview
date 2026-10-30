using System;
using System.Collections.Generic;
using ScpItdMod.Net;

namespace ScpItdMod.Rec {
    public sealed class ScreenFrame {
        private readonly FigurePose[][] frames = { new FigurePose[Tape.MaxFigures], new FigurePose[Tape.MaxFigures] };
        private readonly ulong[][] hashes = { new ulong[Tape.MaxFigures], new ulong[Tape.MaxFigures] };
        private readonly Dictionary<(int, ulong), LookDesc> looks = new Dictionary<(int, ulong), LookDesc>();
        private readonly List<(int, ulong)> removed = new List<(int, ulong)>();
        private readonly LookDesc[] definitions = new LookDesc[Tape.MaxFigures];
        private readonly List<SceneObject>[] scenes = { new List<SceneObject>(SceneTape.InitialObjects), new List<SceneObject>(SceneTape.InitialObjects) };
        private readonly bool[] sceneReady = new bool[2];
        private int active;
        private byte[] assembly = Array.Empty<byte>();
        private bool[] parts = Array.Empty<bool>();
        private ScreenMeta pending;
        private int assemblyLength, partsLeft;
        private bool Bound(ushort target, ushort source, uint subscription, uint epoch, uint sequence) =>
            target != 0 && source != 0 && subscription != 0 && epoch != 0 && sequence != 0 &&
            (terminal == 0 || target == terminal) && (camera == 0 || source == camera) &&
            (session == 0 || subscription == session) &&
            (!Ready || target == Meta.Terminal && (epoch == Meta.Epoch ? (int)(sequence - Meta.Sequence) > 0 : (int)(epoch - Meta.Epoch) > 0));
        private bool ApplyPart(ReadOnlySpan<byte> bytes, double now) {
            if (bytes.Length <= ScreenFrameParts.HeaderBytes || bytes.Length > CamWire.MaxChunk) { return false; }
            var reader = new WireReader(bytes.Slice(2));
            var meta = new ScreenMeta { Terminal = reader.U16(), Camera = reader.U16(), Session = reader.U32(), Epoch = reader.U32(), Sequence = reader.U32() };
            uint length = reader.U32(), offset = reader.U32();
            int size = bytes.Length - ScreenFrameParts.HeaderBytes;
            if (!Bound(meta.Terminal, meta.Camera, meta.Session, meta.Epoch, meta.Sequence) ||
                length <= CamWire.MaxChunk || length > ScreenFrameWire.MaxBytes || offset >= length ||
                offset % ScreenFrameParts.PartBytes != 0 || size != Math.Min(ScreenFrameParts.PartBytes, length - offset)) { return false; }
            bool same = assemblyLength != 0 && meta.Terminal == pending.Terminal && meta.Camera == pending.Camera &&
                meta.Session == pending.Session && meta.Epoch == pending.Epoch && meta.Sequence == pending.Sequence;
            if (!same) {
                if (assemblyLength != 0 && (meta.Terminal != pending.Terminal || meta.Session != pending.Session ||
                    (meta.Epoch == pending.Epoch ? (int)(meta.Sequence - pending.Sequence) <= 0 : (int)(meta.Epoch - pending.Epoch) <= 0))) { return false; }
                assemblyLength = (int)length; pending = meta;
                if (assembly.Length < assemblyLength) { assembly = new byte[assemblyLength]; }
                partsLeft = ScreenFrameParts.Count(assemblyLength);
                if (parts.Length < partsLeft) { parts = new bool[partsLeft]; } else { Array.Clear(parts); }
            } else if (length != assemblyLength) { return false; }
            int index = (int)offset / ScreenFrameParts.PartBytes;
            ReadOnlySpan<byte> data = bytes.Slice(ScreenFrameParts.HeaderBytes);
            if (parts[index]) { return false; }
            data.CopyTo(assembly.AsSpan((int)offset, size)); parts[index] = true;
            if (--partsLeft != 0) { return false; }
            assemblyLength = 0;
            // Match the envelope to the complete frame before publishing any state.
            var full = new WireReader(assembly.AsSpan(0, (int)length));
            if (full.U8() != CamWire.Proto || full.U8() != (byte)CamMsg.ScreenFrame || full.U16() != meta.Terminal ||
                full.U16() != meta.Camera || full.U32() != meta.Epoch || full.U32() != meta.Sequence || full.U32() != meta.Session) { return false; }
            return Apply(assembly.AsSpan(0, (int)length), now);
        }
        private readonly ushort terminal, camera;
        private readonly uint session;
        public ScreenFrame(ushort terminal = 0, uint session = 0, ushort camera = 0) {
            this.terminal = terminal; this.session = session; this.camera = camera;
        }
        public ScreenMeta Meta { get; private set; }
        public int Count { get; private set; }
        public bool Ready { get; private set; }
        public uint Version { get; private set; }
        public double ReceivedAt { get; private set; }
        public FigurePose Pose(int index) => frames[active][index];
        public LookDesc Look(int index) => looks.TryGetValue((frames[active][index].Look, hashes[active][index]), out LookDesc look) ? look : null;
        public IReadOnlyList<SceneObject> Scene => scenes[active];
        public bool HistoricalSceneReady => sceneReady[active];

        public bool Apply(ReadOnlySpan<byte> bytes, double now) {
            if (bytes.Length > ScreenFrameWire.MaxBytes || !double.IsFinite(now)) { return false; }
            if (bytes.Length >= 2 && bytes[0] == CamWire.Proto && bytes[1] == (byte)CamMsg.ScreenPart) { return ApplyPart(bytes, now); }
            var reader = new WireReader(bytes);
            if (reader.U8() != CamWire.Proto || reader.U8() != (byte)CamMsg.ScreenFrame) { return false; }
            var meta = new ScreenMeta { Terminal = reader.U16(), Camera = reader.U16(), Epoch = reader.U32(), Sequence = reader.U32(), Session = reader.U32(),
                Time = BitConverter.Int64BitsToDouble((long)reader.U64()), X = reader.F32(), Y = reader.F32(), HalfW = reader.F32(), HalfH = reader.F32(), State = (ScreenState)reader.U8(), Live = reader.Bool() };
            int count = reader.U8(), target = 1 - active;
            if (meta.Session == 0 || terminal != 0 && meta.Terminal != terminal || session != 0 && meta.Session != session ||
                camera != 0 && meta.State == ScreenState.Picture && meta.Camera != camera || meta.Terminal == 0 || meta.Epoch == 0 || meta.Sequence == 0 || count > Tape.MaxFigures || meta.State > ScreenState.Gap || !double.IsFinite(meta.Time) || meta.Time < 0 ||
                !float.IsFinite(meta.X) || !float.IsFinite(meta.Y) || !float.IsFinite(meta.HalfW) || !float.IsFinite(meta.HalfH) ||
                meta.State == ScreenState.Picture && (meta.Camera == 0 || meta.HalfW <= 0 || meta.HalfH <= 0) || meta.State != ScreenState.Picture && count != 0 ||
                Ready && (meta.Terminal != Meta.Terminal || meta.Epoch != Meta.Epoch && (int)(meta.Epoch - Meta.Epoch) <= 0)) { return false; }
            // Ignore duplicate and reordered packets before touching the inactive buffers.
            // Unreliable screen frames may arrive after a newer picture was committed.
            if (Ready && meta.Epoch == Meta.Epoch && (int)(meta.Sequence - Meta.Sequence) <= 0) { return false; }
            Array.Clear(definitions);
            for (int i = 0; i < count; i++) {
                var pose = new FigurePose { Subject = new Subject(reader.U16(), reader.U32()), Look = (int)reader.U32() };
                ulong hash = reader.U64();
                if (pose.Look < 0 || pose.Look > 1 << 20) { return false; }
                if (reader.Bool()) {
                    LookDesc definition = LookDesc.Read(ref reader);
                    if (definition.Hash != hash) { return false; }
                    definitions[i] = definition;
                }
                pose.Flags = (PoseFlags)reader.U8(); pose.X = reader.F32(); pose.Y = reader.F32(); pose.Limbs = reader.U8(); pose.Missing = reader.U16();
                if (!pose.ValidLimbs || !float.IsFinite(pose.X) || !float.IsFinite(pose.Y) || ((byte)pose.Flags & 128) != 0) { return false; }
                for (int limb = 0; limb < pose.Limbs; limb++) { pose.L.Set(limb, (short)reader.U16(), (short)reader.U16(), (short)reader.U16()); }
                frames[target][i] = pose; hashes[target][i] = hash;
            }
            bool sameCamera = Ready && meta.Epoch == Meta.Epoch && meta.Camera == Meta.Camera;
            if (!SceneWire.Read(ref reader, scenes[target], out sceneReady[target], scenes[active],
                    sameCamera && sceneReady[active]) || reader.Failed || !reader.End) { return false; }
            bool epoch = !Ready || meta.Epoch != Meta.Epoch;
            if (epoch) { looks.Clear(); }
            for (int i = 0; i < count; i++) { if (definitions[i] != null) { looks[(frames[target][i].Look, hashes[target][i])] = definitions[i]; } }
            active = target; Meta = meta; Count = count; Ready = true; ReceivedAt = now;
            Version++;
            if (looks.Count > Tape.MaxFigures * 2) {
                removed.Clear();
                foreach (var pair in looks) {
                    bool used = false;
                    for (int i = 0; i < Count; i++) { if (pair.Key.Item1 == frames[active][i].Look && pair.Key.Item2 == hashes[active][i]) { used = true; break; } }
                    if (!used) { removed.Add(pair.Key); }
                }
                for (int i = 0; i < removed.Count; i++) { looks.Remove(removed[i]); }
            }
            return true;
        }
    }
}
