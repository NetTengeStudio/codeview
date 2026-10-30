using System;
using ScpItdMod.Core;

namespace ScpItdMod.Weather.Material;

// One stepping leg as the snow meets it. The animation decides where the foot goes and when it carries the body; a carried foot that stands on the snow presses it with its share of the body weight (SnowPrint).
// The shin is not a body: its resistance is the crush strength of the snow on the submerged part (SnowCharacters), because a flat solver would turn a leg into a wall across an unbounded depth.
internal sealed class SnowFoot
{
    internal const float Plant = .15f, LegHeight = .8f, LegWidth = .14f, LoadRamp = .15f;

    // The gait of the walk animation: the target of a foot is lifted by lift * sin(phase) on one side and by the opposite on the other, and a foot whose target is not lifted is pressed to the floor.
    // That is when it carries the body. The speed of the sole says nothing about it: the animated stance foot slides back and forth over the ground at up to the walking speed.
    internal static bool Stance(float phase, float lift, float offset, int side) => lift * MathF.Sin(phase) * (side == 0 ? 1 : -1) + offset <= 0;

    internal readonly float Span;
    internal float Length { get; private set; }
    internal float Height { get; private set; }
    private SnowPosition sole;
    private float carried;
    internal bool Planted { get; private set; }
    // Why a foot does not press, for the console line: 0 stands, 1 swings, 2 has no snow under it, 3 is held above it.
    internal int Reason { get; private set; }
    internal float Gap { get; private set; }
    // Weight carried by the foot (kg) and the mass of the body that drives it (kg).
    internal float Load, Mass = 80;
    internal SnowPosition Sole => sole;
    internal SnowFoot(float length, float height, float span)
    {
        if (!float.IsFinite(length) || length <= 0 || !float.IsFinite(height) || height <= 0 || !float.IsFinite(span) || span <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        Length = length; Height = height; Span = span;
    }
    internal void Resize(float length, float height)
    {
        if (!float.IsFinite(length) || length <= 0 || !float.IsFinite(height) || height <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        Length = length; Height = height;
    }
    // Called once per native frame with the animated sole and whether the gait has this foot on the ground. A foot whose footprints another peer authors (press false) stands without pressing.
    internal void Step(SnowMpm material, float dt, SnowPosition limbSole, bool stance, float bottom, float ceiling, bool press = true)
    {
        sole = limbSole; Gap = float.NaN;
        if (!stance) { Planted = false; carried = 0; Reason = 1; return; }
        bool snow = material.Column((float)limbSole.PreciseX, Length * .5f, bottom, ceiling, out _, out float high);
        Gap = snow ? limbSole.Y - high : float.NaN;
        Planted = snow && limbSole.Y <= high + Plant;
        Reason = Planted ? 0 : snow ? 3 : 2;
        if (!Planted) { carried = 0; return; }
        if (!press) return;
        float share = Mass > 0 ? Math.Clamp(Load / Mass, 0, 1) : 0, move = dt / LoadRamp;
        carried = carried < share ? MathF.Min(share, carried + move) : MathF.Max(share, carried - move);
        float pressure = Mass * carried * SnowConstitutive.Gravity / (Length * Span);
        SnowPrint.Press(material, (float)limbSole.PreciseX, Length * .5f, pressure, dt, bottom, ceiling);
    }
}
