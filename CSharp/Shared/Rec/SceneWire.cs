using System;
using System.Collections.Generic;
using System.Text;
using ScpItdMod.Net;

namespace ScpItdMod.Rec {
    public static class SceneWire {
        private const int FixedBytes = 37;
        public const int MaxObjectBytes = FixedBytes + 2 + 80 * 4;
        // An absent scene is distinct from a recorded empty scene. Never fall back
        // to current-world MapEntity state for an archive frame.
        public static ulong Hash(IReadOnlyList<SceneObject> scene) {
            ulong hash = 14695981039346656037UL;
            hash = Mix(hash, (ulong)scene.Count);
            for (int i = 0; i < scene.Count; i++) {
                SceneObject o = scene[i];
                hash = Mix(hash, o.Key);
                hash = Mix(hash, (byte)o.Flags);
                hash = Mix(hash, (uint)BitConverter.SingleToInt32Bits(o.X));
                hash = Mix(hash, (uint)BitConverter.SingleToInt32Bits(o.Y));
                hash = Mix(hash, (uint)BitConverter.SingleToInt32Bits(o.Rotation));
                hash = Mix(hash, (uint)BitConverter.SingleToInt32Bits(o.ScaleX));
                hash = Mix(hash, (uint)BitConverter.SingleToInt32Bits(o.ScaleY));
                hash = Mix(hash, ((ulong)o.Width << 16) | o.Height);
                hash = Mix(hash, o.Color);
                hash = Mix(hash,(uint)(o.Shadow?.Count ?? 0));
                if (o.Shadow != null) {
                    foreach (ShadowEdge edge in o.Shadow.Edges) {
                        hash=Mix(hash,BitConverter.SingleToUInt32Bits(edge.AX)); hash=Mix(hash,BitConverter.SingleToUInt32Bits(edge.AY));
                        hash=Mix(hash,BitConverter.SingleToUInt32Bits(edge.BX)); hash=Mix(hash,BitConverter.SingleToUInt32Bits(edge.BY));
                    }
                }
                foreach (char c in o.Prefab) { hash = Mix(hash, c); }
                hash = Mix(hash, 0xFFUL);
            }
            return hash;
        }
        private static ulong Mix(ulong h, ulong v) => unchecked((h ^ v) * 1099511628211UL);

        public static bool Write(WireWriter writer, IReadOnlyList<SceneObject> scene, int maxBytes) =>
            Write(writer, scene, maxBytes, false, out _);

        public static bool Write(WireWriter writer, IReadOnlyList<SceneObject> scene, int maxBytes,
            bool referencePrevious, out bool completeDefinition) {
            completeDefinition = false;
            if (scene != null && referencePrevious && writer.Length <= maxBytes - 9) {
                writer.U8(2); writer.U64(Hash(scene)); return true;
            }
            if (scene == null || scene.Count > SceneTape.MaxObjects || writer.Length > maxBytes - 3) {
                writer.U8(0); return false;
            }
            int size = 3;
            for (int i = 0; i < scene.Count; i++) {
                SceneObject v = scene[i];
                if (!v.Valid || (i > 0 && scene[i - 1].Key >= v.Key)) { writer.U8(0); return false; }
                int length = Encoding.UTF8.GetByteCount(v.Prefab);
                int objectBytes=FixedBytes + length + (length < 128 ? 1 : 2) + (v.Shadow?.Count ?? 0) * 16;
                if (objectBytes > maxBytes - writer.Length - size) { writer.U8(0); return false; }
                size += objectBytes;
            }
            writer.U8(1); writer.U16((ushort)scene.Count);
            completeDefinition = true;
            for (int i = 0; i < scene.Count; i++) {
                SceneObject v = scene[i];
                writer.U16(v.Id); writer.U32(v.Generation); writer.U8((byte)v.Flags);
                writer.Text(v.Prefab);
                writer.F32(v.X); writer.F32(v.Y); writer.F32(v.Rotation);
                writer.F32(v.ScaleX); writer.F32(v.ScaleY);
                writer.U16(v.Width); writer.U16(v.Height); writer.U32(v.Color);
                writer.U16((ushort)(v.Shadow?.Count ?? 0));
                if (v.Shadow != null) { foreach (ShadowEdge e in v.Shadow.Edges) { writer.F32(e.AX); writer.F32(e.AY); writer.F32(e.BX); writer.F32(e.BY); } }
            }
            return true;
        }
        public static bool Read(ref WireReader reader, List<SceneObject> output, out bool available,
            IReadOnlyList<SceneObject> previous = null, bool previousValid = false) {
            output.Clear(); available = false;
            int flag = reader.U8();
            if (reader.Failed || flag > 2) { return false; }
            if (flag == 0) { return true; }
            if (flag == 2) {
                ulong expected = reader.U64();
                if (reader.Failed) { return false; }
                if (!previousValid || previous == null || Hash(previous) != expected) { return true; }
                for (int i = 0; i < previous.Count; i++) { output.Add(previous[i]); }
                available = true;
                return true;
            }
            int count = reader.U16();
            if (reader.Failed || count > SceneTape.MaxObjects) { return false; }
            ulong key = 0;
            for (int i = 0; i < count; i++) {
                ushort id = reader.U16();
                uint generation = reader.U32();
                SceneFlags flags = (SceneFlags)reader.U8();
                string prefab = reader.Text(320);
                float x = reader.F32(), y = reader.F32(), rotation = reader.F32();
                float sx = reader.F32(), sy = reader.F32();
                ushort w = reader.U16(), h = reader.U16();
                uint color = reader.U32();
                int edgeCount=reader.U16(); SceneShadow shadow=null;
                if (reader.Failed || edgeCount > reader.Remaining/16) { output.Clear(); return false; }
                if (edgeCount != 0) {
                    var edges=new ShadowEdge[edgeCount];
                    for (int k=0; k<edgeCount; k++) {
                        edges[k]=new ShadowEdge(reader.F32(),reader.F32(),reader.F32(),reader.F32());
                        if (!edges[k].Valid) { output.Clear(); return false; }
                    }
                    shadow=SceneShadow.Adopt(edges);
                }
                SceneObject v = new SceneObject(id, generation, prefab, flags, x, y, rotation, sx, sy, w, h, color, shadow);
                if (reader.Failed || !v.Valid || i > 0 && v.Key <= key) { output.Clear(); return false; }
                output.Add(v); key = v.Key;
            }
            available = true;
            return true;
        }
    }
}