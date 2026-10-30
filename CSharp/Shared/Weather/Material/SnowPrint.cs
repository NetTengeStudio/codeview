using System;
using System.Collections.Generic;
using ScpItdMod.Core;
using ScpItdMod.Net;

namespace ScpItdMod.Weather.Material;

// One press as it is applied and as it travels: quantised before it is applied, so the authority and every replica that apply the same presses in the same order to the same particles compute the same snow.
internal readonly struct SnowPress
{
    internal const int Bytes = 18;
    private const float Millimetre = .001f, PascalStep = 4, SecondStep = 1 / 2400f;
    private readonly int x, bottom, ceiling;
    private readonly ushort half, pressure;
    private readonly byte frame, rate;
    private SnowPress(int x, int bottom, int ceiling, ushort half, ushort pressure, byte frame, byte rate) { this.x = x; this.bottom = bottom; this.ceiling = ceiling; this.half = half; this.pressure = pressure; this.frame = frame; this.rate = rate; }
    internal SnowPress(float x, float half, float pressure, float dt, float bottom, float ceiling, float rate) : this(
        (int)MathF.Round(x / Millimetre), (int)MathF.Floor(bottom / Millimetre), (int)MathF.Ceiling(ceiling / Millimetre),
        (ushort)Math.Clamp((int)MathF.Round(half / Millimetre), 1, ushort.MaxValue), (ushort)Math.Clamp((int)MathF.Round(pressure / PascalStep), 0, ushort.MaxValue),
        (byte)Math.Clamp((int)MathF.Round(dt / SecondStep), 1, byte.MaxValue), (byte)Math.Clamp((int)MathF.Round(rate), 1, byte.MaxValue)) { }
    internal float X => x * Millimetre;
    internal float Bottom => bottom * Millimetre;
    internal float Ceiling => ceiling * Millimetre;
    internal float Half => half * Millimetre;
    internal float Pressure => pressure * PascalStep;
    internal float Seconds => frame * SecondStep;
    internal float Rate => rate;
    internal void Write(WireWriter w) { w.U32((uint)x); w.U32((uint)bottom); w.U32((uint)ceiling); w.U16(half); w.U16(pressure); w.U8(frame); w.U8(rate); }
    // Limits every peer's press is held to, whatever it sends: the density a press may add is bounded by Rate * Seconds, so a press cannot compact snow faster than a
    // legitimate contact does. A frame longer than a tenth of a second is a stall, not a footfall, and is clamped to the longest legitimate step.
    internal const byte MaximumFrame = 240, MaximumRate = 2;
    internal const float MaximumPressure = 100_000;
    internal static bool Read(ref WireReader r, out SnowPress press)
    {
        int x = (int)r.U32(), bottom = (int)r.U32(), ceiling = (int)r.U32();
        ushort half = r.U16(), pressure = r.U16(); byte frame = r.U8(), rate = r.U8();
        press = new SnowPress(x, bottom, ceiling, half, (ushort)Math.Min((int)pressure, (int)(MaximumPressure / PascalStep)), (byte)Math.Min(frame, MaximumFrame), (byte)Math.Min(rate, MaximumRate));
        return !r.Failed && press.ceiling > press.bottom && press.ceiling - press.bottom < 20000 && Math.Abs((long)press.x) < 100_000_000;
    }
}

// The number of presses a peer may send, as a token bucket: a legitimate player presses a few hundred times a second at most (one per particle column under each boot), so a
// peer that sends more is refused the excess without any effect on the snow.
internal struct PressBudget
{
    internal const double PerSecond = 1000, Burst = 2000;
    private double tokens, updated;
    private bool started;
    internal bool Take(double now, int presses)
    {
        if (!started) { started = true; tokens = Burst; updated = now; }
        tokens = Math.Min(Burst, tokens + Math.Max(0, now - updated) * PerSecond); updated = now;
        if (presses > tokens) return false;
        tokens -= presses; return true;
    }
}

// The load a peer may press onto the snow under its own character: the impulse of its presses (pressure over the footprint of a boot, for the frame each press covers) can average
// no more than the weight of the character, with a burst for a landing. A press beyond it is refused, so forged pressure cannot press more than a body can carry, however many
// packets carry it.
internal struct PressLoad
{
    internal const float Gravity = 9.81f, BootWidth = .13f, Allowance = 1.5f, BurstSeconds = .5f, BodyMass = 80;
    private double tokens, updated;
    private bool started;
    internal static float Impulse(in SnowPress press) => press.Pressure * 2 * press.Half * BootWidth * press.Seconds;
    internal bool Take(double now, float impulse, float mass)
    {
        float rate = mass * Gravity * Allowance, capacity = rate * BurstSeconds;
        if (!(rate > 0)) return false;
        if (!started) { started = true; tokens = capacity; updated = now; }
        tokens = Math.Min(capacity, tokens + Math.Max(0, now - updated) * rate); updated = now;
        if (impulse > tokens) return false;
        tokens -= impulse; return true;
    }
}

// What a loaded boot or wheel does to the snow under it. The snow is pressed: the pressure of the load, spread with depth like under a strip footing, compacts every particle below the contact to the density
// at which the snow can carry that pressure (the crush law of SnowConstitutive), and the column above is lowered by the volume that went out of it. Nothing is pushed sideways and nothing is woken, because
// in three dimensions the snow of a footprint is compacted and flows out of the plane, while a two-dimensional solver would have to heap it beside the boot.
internal static class SnowPrint
{
    internal const float Shoulder = .08f, Rate = 1.2f, Spread = 1;
    private struct Grain { internal int Id, Bin; internal float Y, Thickness, Weight, Shrink; }
    private static Grain[] entries = new Grain[512];
    private static double[] keys = new double[512];
    private static readonly List<int> ids = new(512);
    // Counted since the console line was last written: particles compressed, presses that found snow, and of those the presses whose print already carries their load.
    internal static int Compressed, Pressing, Settled;
    internal static int Last { get; private set; }
    // Every press applied while replicas are listening is written here (WeatherNetwork sends and clears it each tick); a replica applies the same presses in the same order. A footprint has one
    // author: the authority for the characters it steps, the client for the character it controls, which presses at once and sends its presses up for the authority to apply and pass on.
    internal const int JournalLimit = 8192, Authority = -1;
    internal struct Entry
    {
        internal SnowPress Press;
        internal int Owner;
        internal ushort Predictor;
        internal readonly bool Upstream => Owner == Authority && Predictor == 0;
        internal readonly bool VisibleTo(int session, ushort character) => Owner != session && (Predictor == 0 || Predictor != character);
    }
    internal static bool Recording;
    internal static readonly List<Entry> Journal = new(256);
    internal static void Relay(SnowMpm material, in SnowPress press, int owner)
    {
        Apply(material, press);
        if (Recording && Journal.Count < JournalLimit) Journal.Add(new Entry { Press = press, Owner = owner });
    }
    // Presses the snow under a contact of the given half length centred at x; pressure in Pa. Density rises by at most rate per second, so a footprint forms while the load comes onto the foot.
    // Returns false where there is no snow to press.
    internal static bool Press(SnowMpm material, float x, float half, float pressure, float dt, float bottom, float ceiling, float rate = Rate, ushort predictor = 0)
    {
        var press = new SnowPress(x, half, pressure, dt, bottom, ceiling, rate);
        bool any = Apply(material, press);
        if (any && Recording && Journal.Count < JournalLimit) Journal.Add(new Entry { Press = press, Owner = Authority, Predictor = predictor });
        return any;
    }
    internal static bool Apply(SnowMpm material, in SnowPress press)
    {
        Last = 0;
        float x = press.X, half = press.Half, pressure = press.Pressure, bottom = press.Bottom, ceiling = press.Ceiling, growth = 1 + press.Rate * press.Seconds;
        if (!(pressure > 0) || !material.Column(x, half, bottom, ceiling, out float low, out float high) || high - low < SnowMpm.CellSize * .5f) return false;
        float reach = half + Shoulder, left = x - reach, width = 2 * half;
        material.Collect(left, low - .01f, x + reach, high + .01f, ids);
        int count = ids.Count; if (count == 0) return false;
        if (count > entries.Length) { entries = new Grain[count * 2]; keys = new double[count * 2]; }
        for (int i = 0; i < count; i++)
        {
            ref readonly SnowPoint point = ref material.Point(ids[i]);
            float px = (float)point.Position.PreciseX, py = (float)point.Position.PreciseY, dx = MathF.Abs(px - x);
            float weight = dx <= half ? 1 : .5f * (1 + MathF.Cos(MathF.PI * (dx - half) / Shoulder));
            float depth = MathF.Max(0, high - py), stress = pressure * width / (width + Spread * depth);
            float density = point.Density, target = MathF.Min(SnowConstitutive.MaximumDensity, SnowConstitutive.FreshDensity * MathF.Cbrt(stress / SnowConstitutive.CrushStrength(SnowConstitutive.FreshDensity)));
            float ratio = target > density ? 1 + (target / density - 1) * weight : 1;
            ratio = MathF.Min(ratio, growth);
            float thickness = (float)(point.Mass / density) / SnowConstitutive.Spacing;
            int bin = (int)((px - left) / SnowConstitutive.Spacing);
            entries[i] = new Grain { Id = ids[i], Bin = bin, Y = py, Thickness = thickness, Weight = ratio, Shrink = thickness * (1 - 1 / ratio) };
            keys[i] = bin * 1e4 + (py - low);
        }
        Array.Sort(keys, entries, 0, count);
        float below = 0; int current = int.MinValue; bool any = false;
        for (int i = 0; i < count; i++)
        {
            ref Grain e = ref entries[i];
            if (e.Bin != current) { current = e.Bin; below = 0; }
            if (e.Weight > 1.0005f)
            {
                material.Compress(e.Id, MathF.Max(low + .01f, e.Y - below - e.Shrink * .5f), e.Weight); any = true; Last++;
            }
            else if (below > 0) material.Compress(e.Id, MathF.Max(low + .01f, e.Y - below), 1);
            below += e.Shrink;
        }
        Pressing++; Compressed += Last; if (!any) Settled++;
        return any;
    }
}
