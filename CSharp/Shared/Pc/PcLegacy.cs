using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using ScpItdMod.Docs;
using ScpItdMod.Net;

namespace ScpItdMod.Pc {
    internal sealed class LegacyDisk {
        public string Key;
        public string Receipt;
        public int Read, Write;
        public readonly List<LegacyNode> Nodes = new List<LegacyNode>();
    }
    internal readonly struct LegacyNode {
        public readonly string Name, Text;
        public readonly int Parent, Read, Write;
        public readonly bool Folder;
        public LegacyNode(string name, string text, int parent, int read, int write, bool folder) { Name = name; Text = text; Parent = parent; Read = read; Write = write; Folder = folder; }
    }
    public static class PcLegacy {
        public const int MaxBytes = 64 << 20;
        internal static bool Read(ReadOnlySpan<byte> data, out List<LegacyDisk> disks) {
            disks = null;
            if (data.Length > MaxBytes || !Checked(data, 0x4D524554, out WireReader reader)) { return false; }
            int count = reader.Count(1 << 16);
            var parsed = new List<LegacyDisk>(Math.Min(count, 256));
            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < count && !reader.Failed; i++) {
                string key = reader.Text(256);
                ReadOnlySpan<byte> bytes = reader.Bytes(1 << 24);
                if (reader.Failed || key.Length == 0 || key.Length > 48 || !keys.Add(key) || !Checked(bytes, 0x53465653, out WireReader diskReader)) { return false; }
                var disk = new LegacyDisk { Key = key, Receipt = "terminals:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) };
                if (!Node(disk, ref diskReader, -1, 0, 0) || diskReader.Failed || !diskReader.End) { return false; }
                parsed.Add(disk);
            }
            if (reader.Failed || !reader.End) { return false; }
            disks = parsed; return true;
        }
        private static bool Checked(ReadOnlySpan<byte> data, uint magic, out WireReader reader) {
            reader = default;
            if (data.Length < 8 || Crc32.Of(data.Slice(0, data.Length - 4)) != BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(data.Length - 4))) { return false; }
            reader = new WireReader(data.Slice(0, data.Length - 4)); return reader.U32() == magic;
        }
        private static bool Node(LegacyDisk disk, ref WireReader reader, int parent, int depth, int inherited) {
            if (depth >= PcLimits.Depth || disk.Nodes.Count >= PcLimits.NodesPerDisk - 1) { return false; }
            string name = reader.Text(256); bool folder = reader.Bool();
            string read = reader.Text(512), write = reader.Text(512);
            if (reader.Failed || !Level(read, out int readLevel) || !Level(write, out int writeLevel) || depth == 0 && !folder || depth > 0 && !PcLimits.ValidName(name)) { return false; }
            readLevel = Math.Max(inherited, readLevel); writeLevel = Math.Max(readLevel, writeLevel);
            if (depth == 0) { disk.Read = readLevel; disk.Write = writeLevel; }
            int index = disk.Nodes.Count;
            if (depth > 0) { disk.Nodes.Add(new LegacyNode(name, "", parent, readLevel, writeLevel, folder)); }
            if (folder) {
                int children = reader.Count(PcLimits.EntriesPerFolder);
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < children && !reader.Failed; i++) {
                    int child = disk.Nodes.Count;
                    if (!Node(disk, ref reader, depth == 0 ? -1 : index, depth + 1, readLevel) || !names.Add(disk.Nodes[child].Name)) { return false; }
                }
            } else {
                reader.Count(int.MaxValue); string text = reader.Text(PcLimits.BodyMax * 4);
                if (text.Length > PcLimits.BodyMax) { return false; }
                disk.Nodes[index] = new LegacyNode(name, text, parent, readLevel, writeLevel, false);
            }
            return !reader.Failed;
        }
        private static bool Level(string policy, out int level) {
            level = 0;
            if (string.IsNullOrWhiteSpace(policy)) { return true; }
            ReadOnlySpan<char> value = policy.AsSpan().Trim();
            return value.StartsWith("clearance>=", StringComparison.OrdinalIgnoreCase) && int.TryParse(value.Slice(11).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out level) && level >= 0 && level <= PcLimits.Levels;
        }
    }
}
