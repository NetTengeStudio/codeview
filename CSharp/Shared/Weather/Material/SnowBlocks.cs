using System;
using System.Collections.Generic;
using ScpItdMod.Core;
using ScpItdMod.Net;

namespace ScpItdMod.Weather.Material;

// Replication of the particles. Every peer seeds the same initial pack, so only the blocks that have changed since the start are ever sent.
// The server numbers a block each time anything in it moved, appeared or vanished. A change made by a press (SnowPrint) is reproduced by every peer from the same press, which travels on its own
// (WeatherNetwork), so a block changed only by presses since a peer last confirmed it is sent as a signature: if the peer's particles agree within a centimetre and a half nothing is replaced, and
// the surface it stands on never jumps back to a server state a little later or earlier. Particles are sent where something else (wind, a shovel, snow in motion) changed the block, or where the
// peer reports that its particles disagree.
internal sealed class SnowBlocks
{
    internal const float Width = .5f, Height = .5f, Tolerance = .015f;
    private const int Columns = 4, MaximumParticles = 1200;
    private struct State { internal uint Revision, Foreign; internal byte Cool; internal ulong Shape; }
    private struct MaterialSignature { internal ushort Density, Reference; internal byte Thermal, Bond, Stain; }
    private struct Staged { internal float X, Y, ReferenceDensity, Density, Enthalpy, Bond, Stain; }
    private readonly SnowMpm material;
    private readonly Dictionary<long, State> blocks = new();
    private readonly HashSet<long> now = new(), touched = new(), pressed = new();
    private readonly List<long> keys = new();
    private readonly List<int> ids = new();
    private readonly Dictionary<long, uint> received = new();
    private Staged[] staged = Array.Empty<Staged>();
    internal SnowBlocks(SnowMpm material, bool trackChanges = true)
    {
        this.material = material;
        if (!trackChanges) return;
        material.Touched = position => touched.Add(KeyAt(position.X, position.Y));
        material.Pressed = position => pressed.Add(KeyAt(position.X, position.Y));
    }
    internal static long Key(int bx, int by) => ((long)bx << 32) | (uint)by;
    internal static long KeyAt(float x, float y) => Key((int)MathF.Floor(x / Width), (int)MathF.Floor(y / Height));
    internal static void Split(long key, out int bx, out int by) { bx = (int)(key >> 32); by = (int)(uint)key; }
    internal static WeatherRect Bounds(long key) { Split(key, out int bx, out int by); return new WeatherRect(bx * Width, by * Height, (bx + 1) * Width, (by + 1) * Height); }
    internal IReadOnlyList<long> Changed => keys;
    internal uint Revision(long key) => blocks.TryGetValue(key, out State state) ? state.Revision : 0;
    // The last revision of a block that a press did not explain: a peer that confirmed this revision or a later one can be sent a signature only.
    internal uint Foreign(long key) => blocks.TryGetValue(key, out State state) ? state.Foreign : 0;

    // Called at the replication rate: blocks with moving particles, blocks touched from outside the solver and blocks that moved a moment ago get a new number.
    internal void Refresh()
    {
        now.Clear(); moving.Clear();
        foreach (int id in material.Awake) { ref readonly SnowPoint p = ref material.Point(id); long key = KeyAt(p.Position.X, p.Position.Y); now.Add(key); moving.Add(key); }
        foreach (long key in touched) { now.Add(key); touchedNow.Add(key); }
        foreach (long key in pressed) { now.Add(key); pressedNow.Add(key); }
        touched.Clear(); pressed.Clear();
        foreach (long key in now)
        {
            blocks.TryGetValue(key, out State state); bool foreign = touchedNow.Contains(key) || moving.Contains(key), outside = foreign || pressedNow.Contains(key);
            ulong shape = Shape(key);
            // Moving particles inside a block that keeps its surface do not make it worth sending again; their last state is sent when the block goes quiet.
            if (state.Revision == 0 || outside || shape != state.Shape)
            {
                if (state.Revision == 0) keys.Add(key);
                state.Revision++; state.Shape = shape;
                if (foreign) state.Foreign = state.Revision;
            }
            state.Cool = moving.Contains(key) ? (byte)1 : state.Cool; blocks[key] = state;
        }
        for (int i = 0; i < keys.Count; i++)
        {
            long key = keys[i]; if (now.Contains(key)) continue;
            State state = blocks[key]; if (state.Cool == 0) continue;
            state.Cool--; state.Revision++; state.Foreign = state.Revision; state.Shape = Shape(key); blocks[key] = state;
        }
        touchedNow.Clear(); pressedNow.Clear();
    }
    private readonly HashSet<long> touchedNow = new(), pressedNow = new(), moving = new();
    private ulong Shape(long key)
    {
        Span<ushort> tops = stackalloc ushort[Columns]; Signature(key, tops); ulong shape = 0;
        for (int c = 0; c < Columns; c++) shape = shape << 16 | (ushort)(tops[c] == 0xFFFF ? 0xFFFF : tops[c] / 8);
        return shape;
    }

    private void Signature(long key, Span<ushort> tops)
    {
        WeatherRect r = Bounds(key);
        for (int c = 0; c < Columns; c++)
        {
            float x = r.Left + Width * (c + .5f) / Columns;
            tops[c] = material.Column(x, Width / Columns * .5f, r.Bottom, r.Top, out _, out float high) ? (ushort)Math.Clamp((int)MathF.Round((high - r.Bottom) * 1000), 0, 0xFFFE) : (ushort)0xFFFF;
        }
    }
    private MaterialSignature MaterialState()
    {
        if (ids.Count == 0) return default;
        double density = 0, reference = 0, thermal = 0, bond = 0; float stain = 0;
        for (int i = 0; i < ids.Count; i++)
        {
            ref readonly SnowPoint point = ref material.Point(ids[i]);
            density += point.Density; reference += point.ReferenceDensity; thermal += Thermal(point.Enthalpy); bond += point.Bond;
            stain = MathF.Max(stain, point.Stain);
        }
        return new MaterialSignature {
            Density = (ushort)Math.Clamp((int)Math.Round(density * 10 / ids.Count), 0, ushort.MaxValue),
            Reference = (ushort)Math.Clamp((int)Math.Round(reference * 10 / ids.Count), 0, ushort.MaxValue),
            Thermal = (byte)Math.Clamp((int)Math.Round(thermal / ids.Count), 0, 255),
            Bond = (byte)Math.Clamp((int)Math.Round(bond * 255 / ids.Count), 0, 255),
            Stain = (byte)Math.Clamp((int)MathF.Round(stain * 255), 0, 255)
        };
    }
    private static bool Agrees(in MaterialSignature a, in MaterialSignature b) =>
        Math.Abs(a.Density - b.Density) <= 30 && Math.Abs(a.Reference - b.Reference) <= 30 &&
        Math.Abs(a.Thermal - b.Thermal) <= 2 && Math.Abs(a.Bond - b.Bond) <= 5 && Math.Abs(a.Stain - b.Stain) <= 3;
    // Header: block, revision, signature-only flag, particle count and the four column tops. The particles follow unless only the signature was asked for.
    internal bool Write(WireWriter w, long key, bool signatureOnly)
    {
        WeatherRect r = Bounds(key); Split(key, out int bx, out int by);
        material.Collect(r.Left, r.Bottom, r.Right, r.Top, ids);
        if (ids.Count > MaximumParticles) signatureOnly = false;
        w.U32((uint)bx); w.U32((uint)by); w.U32(Revision(key)); w.Bool(signatureOnly); w.U16((ushort)Math.Min(ids.Count, MaximumParticles));
        Span<ushort> tops = stackalloc ushort[Columns]; Signature(key, tops); for (int c = 0; c < Columns; c++) w.U16(tops[c]);
        MaterialSignature state = MaterialState();
        w.U16(state.Density); w.U16(state.Reference); w.U8(state.Thermal); w.U8(state.Bond); w.U8(state.Stain);
        if (signatureOnly) return true;
        for (int i = 0; i < Math.Min(ids.Count, MaximumParticles); i++)
        {
            ref readonly SnowPoint p = ref material.Point(ids[i]);
            w.U8((byte)Math.Clamp((int)((p.Position.X - r.Left) / Width * 256), 0, 255));
            w.U8((byte)Math.Clamp((int)((p.Position.Y - r.Bottom) / Height * 256), 0, 255));
            w.U8((byte)Math.Clamp((int)MathF.Round(p.ReferenceDensity / SnowConstitutive.MaximumDensity * 255), 1, 255));
            w.U8((byte)Math.Clamp((int)MathF.Round(p.Density / SnowConstitutive.MaximumDensity * 255), 1, 255));
            w.U8(Thermal(p.Enthalpy));
            w.U8((byte)Math.Clamp((int)MathF.Round(p.Bond * 255), 0, 255));
            w.U8((byte)Math.Clamp((int)MathF.Round(p.Stain * 255), 0, 255));
        }
        return true;
    }


    // One byte for the thermal state: half-degree steps of the temperature of dry snow, then 136 steps of the liquid fraction of wet snow.
    private static byte Thermal(double enthalpy)
    {
        if (enthalpy < 0) return (byte)Math.Clamp((int)Math.Round(-enthalpy / SnowConstitutive.IceHeat * 2), 0, 119);
        return (byte)(120 + Math.Clamp((int)Math.Round(enthalpy / SnowConstitutive.LatentHeat * 135), 0, 135));
    }
    private static float Enthalpy(byte code) => code < 120 ? -code / 2f * SnowConstitutive.IceHeat : (code - 120) / 135f * SnowConstitutive.LatentHeat;
    internal enum Outcome { Malformed, Agreed, Replaced, NeedParticles }
    // Reads a block message. A signature that agrees with the local particles leaves them alone; one that does not asks for the particles (or, when they came with it, replaces the block).
    internal Outcome Read(ref WireReader r, out long key, out uint revision)
    {
        int bx = (int)r.U32(), by = (int)r.U32(); revision = r.U32(); bool signatureOnly = r.Bool(); int count = r.U16();
        Span<ushort> sent = stackalloc ushort[Columns]; for (int c = 0; c < Columns; c++) sent[c] = r.U16();
        MaterialSignature sentState = new MaterialSignature { Density = r.U16(), Reference = r.U16(), Thermal = r.U8(), Bond = r.U8(), Stain = r.U8() };
        key = Key(bx, by);
        if (r.Failed || revision == 0 || count > MaximumParticles || Math.Abs((long)bx) > 100000 || Math.Abs((long)by) > 100000) return Outcome.Malformed;
        WeatherRect box = Bounds(key);
        if (signatureOnly ? !r.End : count * 7 != r.Remaining) return Outcome.Malformed;
        if (received.TryGetValue(key, out uint previous) && revision <= previous) return Outcome.Agreed;
        if (signatureOnly)
        {
            Span<ushort> local = stackalloc ushort[Columns]; Signature(key, local);
            material.Collect(box.Left, box.Bottom, box.Right, box.Top, ids);
            MaterialSignature localState = MaterialState();
            bool agrees = MathF.Abs(ids.Count - count) <= Math.Max(2, count * .03f) && Agrees(in sentState, in localState);
            for (int c = 0; c < Columns && agrees; c++)
                agrees = sent[c] == 0xFFFF ? local[c] == 0xFFFF : local[c] != 0xFFFF && MathF.Abs(sent[c] - local[c]) <= Tolerance * 1000;
            if (!agrees) return Outcome.NeedParticles;
            received[key] = revision; return Outcome.Agreed;
        }
        if (staged.Length < count) Array.Resize(ref staged, Math.Max(count, staged.Length * 2));
        float top = float.MinValue;
        for (int i = 0; i < count; i++)
        {
            float x = box.Left + (r.U8() + .5f) / 256f * Width, y = box.Bottom + (r.U8() + .5f) / 256f * Height;
            float reference = r.U8() / 255f * SnowConstitutive.MaximumDensity, density = r.U8() / 255f * SnowConstitutive.MaximumDensity;
            float enthalpy = Enthalpy(r.U8()), bond = r.U8() / 255f, stain = r.U8() / 255f;
            if (!(reference > 0) || !(density > 0)) return Outcome.Malformed;
            staged[i] = new Staged { X = x, Y = y, ReferenceDensity = reference, Density = density, Enthalpy = enthalpy, Bond = bond, Stain = stain };
            top = MathF.Max(top, y);
        }
        if (r.Failed || !r.End) return Outcome.Malformed;
        material.Collect(box.Left, box.Bottom, box.Right, box.Top, ids);
        for (int i = 0; i < ids.Count; i++) material.Remove(ids[i]);
        for (int i = 0; i < count; i++)
        {
            Staged s = staged[i];
            SnowMatrix elastic = SnowPlasticity.Overburden(s.Density * SnowConstitutive.Gravity * MathF.Max(0, top - s.Y));
            material.Seed(new SnowPoint { Position = new Vec2(s.X, s.Y), Mass = s.ReferenceDensity * SnowConstitutive.Area, ReferenceVolume = SnowConstitutive.Area,
                Enthalpy = s.Enthalpy, Elastic = elastic, Plastic = s.ReferenceDensity / (s.Density * elastic.Determinant), Bond = s.Bond, Stain = s.Stain }, true);
        }
        received[key] = revision;
        return Outcome.Replaced;
    }
}
