using System;
using System.Collections.Generic;
using ScpItdMod.Net;

namespace ScpItdMod.Rec {
    public enum ScreenState : byte { Picture, NoPower, NoRights, Lost, Waiting, Gap }

    public struct ScreenMeta {
        public ushort Terminal, Camera;
        public uint Epoch, Sequence, Session;
        public double Time;
        public float X, Y, HalfW, HalfH;
        public ScreenState State;
        public bool Live;

        public readonly bool Intersects(float left, float top, float width, float height) =>
            State == ScreenState.Picture && left + width >= X - HalfW && left <= X + HalfW && top >= Y - HalfH && top - height <= Y + HalfH;
    }

    public static class ScreenFrameWire {
        public const int InitialBytes = 64 * 1024;
        public const int MaxPacketBytes = InitialBytes;
        public const int MaxBytes = InitialBytes + SceneTape.MaxObjects * (SceneWire.MaxObjectBytes + 4 * 16);
        public const int MaxViews = 4;
        public static uint Write(WireWriter writer, in ScreenMeta meta, IReadOnlyList<FigurePose> poses, LookTable looks,
            Dictionary<int, ulong> sent, IReadOnlyList<SceneObject> scene) =>
            Write(writer, meta, poses, looks, sent, scene, false, out _);

        public static uint Write(WireWriter writer, in ScreenMeta meta, IReadOnlyList<FigurePose> poses, LookTable looks,
            Dictionary<int, ulong> sent, IReadOnlyList<SceneObject> scene, bool reuseScene, out bool fullScene) {
            CamWire.Header(writer, CamMsg.ScreenFrame);
            writer.U16(meta.Terminal); writer.U16(meta.Camera); writer.U32(meta.Epoch); writer.U32(meta.Sequence); writer.U32(meta.Session);
            writer.U64((ulong)BitConverter.DoubleToInt64Bits(meta.Time)); writer.F32(meta.X); writer.F32(meta.Y); writer.F32(meta.HalfW); writer.F32(meta.HalfH); writer.U8((byte)meta.State);
            int count = meta.State == ScreenState.Picture ? Math.Min(Tape.MaxFigures, poses.Count) : 0;
            writer.Bool(meta.Live); writer.U8((byte)count); uint definitions = 0;
            int budget = InitialBytes - writer.Length - 4 - count * (31 + FigurePose.MaxLimbs * 6);
            for (int i = 0; i < count; i++) {
                FigurePose pose = poses[i]; LookDesc look = looks.Get(pose.Look);
                ulong hash = look?.Hash ?? 0;
                bool include = look != null && (!sent.TryGetValue(pose.Look, out ulong known) || known != hash) && look.WireBytes <= budget;
                if (include) { definitions |= 1u << i; budget -= look.WireBytes; }
                writer.U16(pose.Subject.Entity); writer.U32(pose.Subject.Serial); writer.U32((uint)pose.Look); writer.U64(hash); writer.Bool(include);
                if (include) { look.Write(writer); }
                writer.U8((byte)pose.Flags); writer.F32(pose.X); writer.F32(pose.Y); writer.U8((byte)pose.Limbs); writer.U16(pose.Missing);
                for (int limb = 0; limb < pose.Limbs; limb++) { writer.U16((ushort)pose.L.X(limb)); writer.U16((ushort)pose.L.Y(limb)); writer.U16((ushort)pose.L.A(limb)); }
            }
            SceneWire.Write(writer, meta.State == ScreenState.Picture ? scene : null, MaxBytes, reuseScene, out fullScene);
            return definitions;
        }
    }

}
