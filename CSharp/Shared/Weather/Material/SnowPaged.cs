using System;

namespace ScpItdMod.Weather.Material;

// Indexed storage that grows one fixed page at a time. A table that doubles in place copies every entry and allocates the whole new size at once,
// which happened to the cell table on the first moving snow to reach new ground (22 MB in one step on Vostok-11); a page costs the same whatever the pack size.
internal sealed class SnowPaged<T> where T : struct
{
    private const int Shift = 12, Size = 1 << Shift, Mask = Size - 1;
    private T[][] pages = new T[8][]; private int pageCount;
    internal int Capacity => pageCount * Size;
    internal ref T this[int index] => ref pages[index >> Shift][index & Mask];
    internal void Grow() { if (pageCount == pages.Length) Array.Resize(ref pages, pageCount * 2); pages[pageCount++] = new T[Size]; }
    // Drops the pages that no entry below count needs; the next Grow allocates the page again.
    internal void Trim(int count)
    {
        int keep = Math.Max(1, (count + Size - 1) >> Shift);
        if (pageCount > keep) { Array.Clear(pages, keep, pageCount - keep); pageCount = keep; }
    }
}
