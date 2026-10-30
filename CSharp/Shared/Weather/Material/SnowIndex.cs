using System;
using System.Runtime.CompilerServices;

namespace ScpItdMod.Weather.Material;

/// <summary>Open-addressing map from a packed grid key to a non-negative slot; deletion shifts the probe chain back so lookups never meet tombstones, and Clear is O(capacity) only when it holds entries. Key and slot share one entry, so a probe touches one cache line.</summary>
internal sealed class SnowIndex
{
    private struct Entry { internal long Key; internal int Value; }
    private Entry[] entries; private int mask, count;
    internal SnowIndex(int capacity)
    {
        int size = 16; while (size < capacity * 2) size <<= 1;
        entries = NewTable(size); mask = size - 1;
    }
    private static Entry[] NewTable(int size)
    {
        var table = new Entry[size]; Array.Fill(table, new Entry { Value = -1 }); return table;
    }
    internal int Count => count;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Home(long key, int mask) => (int)((ulong)key * 0x9E3779B97F4A7C15UL >> 40) & mask;
    internal bool TryGetValue(long key, out int value)
    {
        Entry[] table = entries; int m = mask;
        for (int i = Home(key, m); ; i = (i + 1) & m)
        {
            ref Entry e = ref table[i];
            if (e.Value < 0) { value = 0; return false; }
            if (e.Key == key) { value = e.Value; return true; }
        }
    }
    internal void Add(long key, int value)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        if ((count + 1) * 2 > entries.Length) Grow();
        int i = Home(key, mask);
        while (entries[i].Value >= 0) { if (entries[i].Key == key) throw new ArgumentException("Duplicate key"); i = (i + 1) & mask; }
        entries[i] = new Entry { Key = key, Value = value }; count++;
    }
    internal void Remove(long key)
    {
        int i = Home(key, mask);
        while (entries[i].Value >= 0 && entries[i].Key != key) i = (i + 1) & mask;
        if (entries[i].Value < 0) return;
        count--;
        for (int j = (i + 1) & mask; entries[j].Value >= 0; j = (j + 1) & mask)
        {
            int home = Home(entries[j].Key, mask);
            if (((j - home) & mask) >= ((j - i) & mask)) { entries[i] = entries[j]; i = j; }
        }
        entries[i].Value = -1;
    }
    internal void Clear() { if (count == 0) return; Array.Fill(entries, new Entry { Value = -1 }); count = 0; }
    internal void EnsureCapacity(int capacity) { while (entries.Length < capacity * 2) Grow(); }
    private void Grow()
    {
        Entry[] old = entries; entries = NewTable(old.Length * 2); mask = entries.Length - 1; count = 0;
        for (int i = 0; i < old.Length; i++) if (old[i].Value >= 0) Add(old[i].Key, old[i].Value);
    }
}
